using CiFail.Core.Models;
using CiFail.Core.Output;

namespace CiFail.Cli.Output;

/// <summary>
/// `cifail analyze --json` output. The contract now lives in
/// <see cref="CiFail.Core.Output.AnalysisJson"/> so the CLI and the <c>cifail serve</c>
/// HTTP API serialize one identical schema; this is a thin wrapper kept for callers.
/// </summary>
public static class JsonOutput
{
    public static string Serialize(Analysis analysis) => AnalysisJson.Serialize(analysis);
}
