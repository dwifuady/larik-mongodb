using Larik.Data;
using MongoDB.Bson;

namespace Larik.Data.MongoDB;

/// <summary>
/// The gate a read-only agent session passes before anything runs: the host asks
/// <see cref="IDatabaseProvider.ReadOnlyPolicy"/> and refuses the statement unless
/// <see cref="IsProvenRead"/> says yes. MongoDB has no server-side read-only session this provider
/// could open, so the proof is structural instead of transactional: every statement is judged by the
/// same splitter the editor and execution use, and a unit is a read only when it is a
/// <c>SELECT</c> the translator compiles to a read pipeline (or a <c>USE</c> that only re-targets the
/// session's database), or a mongosh call on the read allow-list whose arguments carry no writing
/// pipeline stage.
/// <para>
/// Fail-closed, deliberately: unparseable text, a chained call, an unknown method or a mixed batch is
/// refused rather than executed. The mongosh side is an allow-list because the passthrough runner can
/// write (insert/update/delete) and mongosh itself is a general-purpose shell.
/// </para>
/// </summary>
internal sealed class MongoReadOnlyPolicy : IReadOnlyStatementPolicy
{
    public static MongoReadOnlyPolicy Instance { get; } = new();

    /// <summary>
    /// mongosh methods that only read, matching the reference studio's list so an agent's script
    /// behaves the same on either. Being on this list does not mean the passthrough runner implements
    /// the method: the gate answers "is this a read", and an unsupported form still fails at execution
    /// with its own message.
    /// </summary>
    private static readonly HashSet<string> ReadMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "find",
        "findOne",
        "aggregate",
        "count",
        "countDocuments",
        "estimatedDocumentCount",
        "distinct",
        "getIndexes",
        "getCollectionNames",
        "stats",
    };

    /// <summary>
    /// Aggregation stages that write: <c>$out</c> replaces a collection and <c>$merge</c> upserts into
    /// one, so a read method carrying either is not a read.
    /// </summary>
    private static readonly string[] WriteStages = ["$out", "$merge"];

    private MongoReadOnlyPolicy()
    {
    }

    public bool IsProvenRead(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        // The execution path splits exactly this way (including shell-vs-SQL tagging), so the gate can
        // never judge a different set of units than the one that would run.
        var spans = MongoTSqlDialect.Instance.Split(sql);
        if (spans.Count == 0)
        {
            return false;
        }

        foreach (var span in spans)
        {
            if (span.Start < 0 || span.Length == 0 || span.Start > sql.Length - span.Length)
            {
                return false;
            }

            var statement = sql.Substring(span.Start, span.Length);
            var isRead = span.Language == MongoScriptClassifier.Shell
                ? IsReadShell(statement)
                : IsReadSql(statement);

            if (!isRead)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A <c>USE</c> only points the session at another database - it reads nothing and writes nothing,
    /// and the reference studio allows it for the same reason. Everything else must translate to a
    /// <c>SELECT</c>, whose pipeline is then checked for a writing stage in case the translator ever
    /// grows one.
    /// </summary>
    private static bool IsReadSql(string statement)
    {
        try
        {
            return TSqlMongoCompiler.Parse(statement) switch
            {
                MSelect select => !HasWriteStage(select.Pipeline),
                MUse => true,
                _ => false,
            };
        }
        catch (Exception ex) when (ex is TranslationException or FormatException or InvalidOperationException)
        {
            // Refusing is the point: text the translator cannot read is not provably a read.
            return false;
        }
    }

    /// <summary>
    /// One <c>db.collection.method(...)</c> or <c>db.method(...)</c> call, no chain, on the read
    /// allow-list, with arguments free of <c>$out</c>/<c>$merge</c>. Chained calls are refused because
    /// the runner cannot execute them anyway and judging an arbitrary suffix as a read is guesswork.
    /// </summary>
    private static bool IsReadShell(string statement)
    {
        var text = statement.Trim().TrimEnd(';').Trim();
        if (!text.StartsWith("db.", StringComparison.OrdinalIgnoreCase) || !text.EndsWith(')'))
        {
            return false;
        }

        var open = text.IndexOf('(');
        if (open <= "db.".Length)
        {
            return false;
        }

        var close = MatchingParen(text, open);
        if (close != text.Length - 1)
        {
            return false;
        }

        // db.collection.method(...) names the collection then the method; db.method(...) names only
        // the method (db.getCollectionNames(), db.stats()).
        var head = text["db.".Length..open];
        var lastDot = head.LastIndexOf('.');
        var method = (lastDot < 0 ? head : head[(lastDot + 1)..]).Trim();
        if (!ReadMethods.Contains(method))
        {
            return false;
        }

        return !ContainsWriteStage(text[(open + 1)..close]);
    }

    /// <summary>Index of the <c>)</c> closing <paramref name="open"/>, or -1 when the call is malformed.</summary>
    private static int MatchingParen(string text, int open)
    {
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            var current = text[index];
            if (current is '"' or '\'')
            {
                index = EndOfString(text, index);
                if (index < 0)
                {
                    return -1;
                }

                continue;
            }

            switch (current)
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    if (depth < 0)
                    {
                        return -1;
                    }

                    if (depth == 0)
                    {
                        return current == ')' ? index : -1;
                    }

                    break;
            }
        }

        return -1;
    }

    /// <summary>
    /// True when a <c>$out</c>/<c>$merge</c> key appears anywhere in the arguments, quoted or bare.
    /// String contents are read so a quoted key is still found, but only a key counts: the same text
    /// used as a value (<c>{note: "$out"}</c>) is harmless, and skipping strings outright would hide
    /// <c>{"$out": "archive"}</c>.
    /// </summary>
    private static bool ContainsWriteStage(string arguments)
    {
        var index = 0;
        while (index < arguments.Length)
        {
            var current = arguments[index];
            if (current is '"' or '\'')
            {
                var close = EndOfString(arguments, index);
                if (close < 0)
                {
                    // Unterminated: refuse rather than guess where the argument ends.
                    return true;
                }

                if (IsWriteStage(arguments[(index + 1)..close]) && NextNonSpace(arguments, close + 1) == ':')
                {
                    return true;
                }

                index = close + 1;
                continue;
            }

            if (current == '$')
            {
                var start = index;
                while (index < arguments.Length
                    && (char.IsLetterOrDigit(arguments[index]) || arguments[index] is '_' or '$'))
                {
                    index++;
                }

                if (IsWriteStage(arguments[start..index]) && NextNonSpace(arguments, index) == ':')
                {
                    return true;
                }

                continue;
            }

            index++;
        }

        return false;
    }

    private static bool IsWriteStage(string name) =>
        WriteStages.Contains(name, StringComparer.Ordinal);

    private static bool HasWriteStage(IEnumerable<BsonDocument> pipeline) =>
        pipeline.Any(stage => stage.Elements.Any(element => IsWriteStage(element.Name)));

    /// <summary>Index of the quote closing the string starting at <paramref name="open"/>, or -1.</summary>
    private static int EndOfString(string text, int open)
    {
        var quote = text[open];
        for (var index = open + 1; index < text.Length; index++)
        {
            if (text[index] == '\\')
            {
                index++;
                continue;
            }

            if (text[index] == quote)
            {
                return index;
            }
        }

        return -1;
    }

    private static char? NextNonSpace(string text, int index)
    {
        while (index < text.Length)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                return text[index];
            }

            index++;
        }

        return null;
    }
}
