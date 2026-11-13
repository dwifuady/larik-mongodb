using System.Globalization;
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
/// pipeline stage, optionally narrowed by the safe <c>find</c> cursor modifiers.
/// <para>
/// Fail-closed, deliberately: unparseable text, an unknown method, an unsafe chained call or a mixed
/// batch is refused rather than executed. The mongosh side is an allow-list because the passthrough
/// runner can write (insert/update/delete) and mongosh itself is a general-purpose shell.
/// </para>
/// </summary>
internal sealed class MongoReadOnlyPolicy : IReadOnlyStatementPolicy
{
    public static MongoReadOnlyPolicy Instance { get; } = new();

    /// <summary>
    /// Read methods the passthrough runner executes, split by the level they belong to. Keeping the
    /// two sets separate means <c>db.getIndexes()</c> and <c>db.collection.getCollectionNames()</c>
    /// are refused instead of passing the gate and failing at execution. <c>aggregate</c> is
    /// deliberately absent: it can write through <c>$out</c>/<c>$merge</c>, and a text scan is not
    /// enough to prove a parsed pipeline is read-only.
    /// </summary>
    private static readonly HashSet<string> CollectionReadMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "find",
        "findOne",
        "count",
        "countDocuments",
        "estimatedDocumentCount",
        "distinct",
        "getIndexes",
        "stats",
    };

    private static readonly HashSet<string> DatabaseReadMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "getCollectionNames",
        "stats",
    };

    /// <summary>
    /// Cursor modifiers that only narrow a <c>find</c>. Anything else chained after a call is not
    /// provably a read: mongosh cursors have methods that execute arbitrary callbacks.
    /// </summary>
    private static readonly HashSet<string> ReadSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "limit",
        "skip",
        "sort",
        "project",
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
    /// One <c>db.collection.method(...)</c> or <c>db.method(...)</c> call on the read allow-list,
    /// optionally followed by the cursor modifiers that only narrow a <c>find</c>
    /// (<c>.limit</c>, <c>.skip</c>, <c>.sort</c>, <c>.project</c>). Arguments must be free of
    /// <c>$out</c>/<c>$merge</c>. Any other chained method is refused: a cursor callback can write.
    /// </summary>
    private static bool IsReadShell(string statement)
    {
        if (!MongoShellSyntax.TrySplitCalls(statement, out var head, out var baseBody, out var suffixes))
        {
            return false;
        }

        // db.collection.method(...) names the collection then the method; db.method(...) names only
        // the method (db.getCollectionNames(), db.stats()).
        var lastDot = head.LastIndexOf('.');
        var method = (lastDot < 0 ? head : head[(lastDot + 1)..]).Trim();
        var allowed = lastDot < 0 ? DatabaseReadMethods : CollectionReadMethods;
        if (!allowed.Contains(method) || ContainsWriteStage(baseBody))
        {
            return false;
        }

        if (suffixes.Count == 0)
        {
            return true;
        }

        // Cursor modifiers are only meaningful on a collection-level find(); the runner refuses them
        // elsewhere too.
        if (lastDot < 0 || !method.Equals("find", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var suffix in suffixes)
        {
            if (!ReadSuffixes.Contains(suffix.Method) || ContainsWriteStage(suffix.Body))
            {
                return false;
            }

            // limit/skip must be the non-negative integer the runner will parse; anything else is
            // malformed, not a read we can stand behind.
            if (IsCountSuffix(suffix.Method)
                && (!int.TryParse(suffix.Body.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when a <c>$out</c>/<c>$merge</c> key appears anywhere in the arguments, quoted or bare.
    /// String contents are read so a quoted key is still found, but only a key counts: the same text
    /// used as a value (<c>{note: "$out"}</c>) is harmless, and skipping strings outright would hide
    /// <c>{"$out": "archive"}</c>.
    /// <para>
    /// This is a text heuristic for the allow-listed read methods, whose arguments never execute
    /// pipeline stages. It is not sufficient for <c>aggregate</c> (a JSON escape such as
    /// <c>\u0024out</c> parses to a writing stage), which is exactly why <c>aggregate</c> is not on
    /// the allow-list.
    /// </para>
    /// </summary>
    private static bool ContainsWriteStage(string arguments)
    {
        var index = 0;
        while (index < arguments.Length)
        {
            var current = arguments[index];
            if (current is '"' or '\'')
            {
                var close = MongoShellSyntax.EndOfString(arguments, index);
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

    private static bool IsCountSuffix(string method) =>
        method.Equals("limit", StringComparison.OrdinalIgnoreCase)
        || method.Equals("skip", StringComparison.OrdinalIgnoreCase);

    private static bool IsWriteStage(string name) =>
        WriteStages.Contains(name, StringComparer.Ordinal);

    private static bool HasWriteStage(IEnumerable<BsonDocument> pipeline) =>
        pipeline.Any(stage => stage.Elements.Any(element => IsWriteStage(element.Name)));

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
