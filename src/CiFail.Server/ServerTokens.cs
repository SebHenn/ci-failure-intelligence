namespace CiFail.Server;

/// <summary>
/// A configured client bearer token plus an operator-facing name (R20). The name is purely for
/// the operator's bookkeeping — it lets a single client be rotated or revoked without disturbing
/// the others (just remove its entry). Tokens are never logged.
/// </summary>
public sealed record NamedToken(string Name, string Token);

/// <summary>Parses the multi-token sources for <c>cifail serve</c> (env list + tokens file).</summary>
public static class ServerTokens
{
    /// <summary>
    /// Parse a comma-separated token list (e.g. <c>CIFAIL_SERVER_TOKENS</c>). Each entry is a bare
    /// token, auto-named <c>token1</c>, <c>token2</c>, … Blank entries are ignored.
    /// </summary>
    public static IReadOnlyList<NamedToken> ParseList(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return Array.Empty<NamedToken>();

        var tokens = new List<NamedToken>();
        var i = 1;
        foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            tokens.Add(new NamedToken($"token{i++}", raw));
        return tokens;
    }

    /// <summary>
    /// Parse a tokens file: one entry per line as <c>&lt;token&gt; [name]</c> (token first, optional
    /// name = the rest of the line). Blank lines and <c>#</c> comments are skipped. Token-first means
    /// base64 padding (<c>=</c>) in the token never trips the parser.
    /// </summary>
    public static IReadOnlyList<NamedToken> ParseFile(string path)
    {
        var tokens = new List<NamedToken>();
        var i = 1;
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var parts = trimmed.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            var name = parts.Length > 1 ? parts[1].Trim() : $"token{i}";
            tokens.Add(new NamedToken(name, parts[0]));
            i++;
        }
        return tokens;
    }
}
