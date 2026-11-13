namespace Larik.Data.MongoDB;

/// <summary>A cursor modifier chained after a mongosh call, such as <c>.limit(5)</c>.</summary>
internal readonly record struct ShellSuffix(string Method, string Body);

/// <summary>
/// Splits a mongosh statement into its first <c>db.collection.method(...)</c> call and any chained
/// suffix calls. The read gate and the runner share this so they always see the same shape.
/// </summary>
internal static class MongoShellSyntax
{
    /// <summary>
    /// Splits <c>db.users.find({}).sort({ a: 1 }).limit(5)</c> into the head
    /// <c>users.find</c>, the base body <c>{}</c>, and the suffixes. Returns false on anything that
    /// is not a well-formed chain, including trailing text.
    /// </summary>
    public static bool TrySplitCalls(
        string statement,
        out string head,
        out string baseBody,
        out List<ShellSuffix> suffixes)
    {
        head = string.Empty;
        baseBody = string.Empty;
        suffixes = [];

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
        if (close < 0)
        {
            return false;
        }

        head = text["db.".Length..open].Trim();
        baseBody = text[(open + 1)..close].Trim();

        var index = close + 1;
        while (index < text.Length)
        {
            index = SkipSpace(text, index);
            if (index >= text.Length || text[index] != '.')
            {
                return false;
            }

            index = SkipSpace(text, index + 1);
            var start = index;
            while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
            {
                index++;
            }

            if (index == start)
            {
                return false;
            }

            var method = text[start..index];
            index = SkipSpace(text, index);
            if (index >= text.Length || text[index] != '(')
            {
                return false;
            }

            var suffixClose = MatchingParen(text, index);
            if (suffixClose < 0)
            {
                return false;
            }

            suffixes.Add(new ShellSuffix(method, text[(index + 1)..suffixClose].Trim()));
            index = suffixClose + 1;
        }

        return true;
    }

    /// <summary>Index of the <c>)</c> closing <paramref name="open"/>, or -1 when malformed.</summary>
    public static int MatchingParen(string text, int open)
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

    /// <summary>Index of the quote closing the string starting at <paramref name="open"/>, or -1.</summary>
    public static int EndOfString(string text, int open)
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

    private static int SkipSpace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }
}
