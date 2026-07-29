using YamlDotNet.Core;

namespace CiFail.Core.Configuration;

/// <summary>
/// Thrown when <c>~/.cifail/config.yaml</c> exists but can't be read as config.
///
/// <para>
/// YamlDotNet's own <see cref="YamlException"/> says "(Line: 4, Col: 3, Idx: 42) - ..." with no
/// hint as to which file, and reaches the user as a raw stack trace. This carries the path and
/// position so the CLI can point at the offending line and exit with a config-specific code.
/// </para>
/// </summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string path, string message, int? line = null, int? column = null, Exception? inner = null)
        : base(message, inner)
    {
        Path = path;
        Line = line;
        Column = column;
    }

    /// <summary>The config file that failed to load.</summary>
    public string Path { get; }

    /// <summary>1-based line of the problem, when the parser reported one.</summary>
    public int? Line { get; }

    /// <summary>1-based column of the problem, when the parser reported one.</summary>
    public int? Column { get; }

    /// <summary><c>path:line:col</c> when a position is known, otherwise just the path.</summary>
    public string Location => Line is null ? Path : $"{Path}:{Line}{(Column is null ? "" : $":{Column}")}";

    /// <summary>Wrap a parser failure, lifting the position out of the YamlDotNet exception.</summary>
    public static ConfigException FromYaml(string path, YamlException ex)
    {
        // YamlException.Message embeds the position already; strip it so the caller can render
        // "path:line:col: message" without saying the numbers twice.
        var message = ex.Message;
        var close = message.StartsWith('(') ? message.IndexOf(") - ", StringComparison.Ordinal) : -1;
        if (close >= 0) message = message[(close + 4)..];

        var line = ex.Start.Line > 0 ? (int)ex.Start.Line : (int?)null;
        var column = ex.Start.Column > 0 ? (int)ex.Start.Column : (int?)null;
        return new ConfigException(path, message, line, column, ex);
    }
}
