namespace CiFail.Core.Models;

/// <summary>
/// The build/test ecosystem a log most likely came from. Used to scope which
/// rule packs are applied and surfaced in output.
/// </summary>
public enum Ecosystem
{
    Unknown = 0,
    Dotnet,
    Node,
    Python,
    Java,
    Go,
    Rust,
    Ruby,
    Php,
    Cpp,
    Infra,
    Swift,
    Android,
    Scala,
    Elixir,
    Generic,
}
