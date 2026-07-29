namespace CiFail.Cli;

/// <summary>
/// The one exit-code taxonomy for every cifail command, so scripts can branch on *why*
/// something failed instead of on which command produced it.
///
/// <para>
/// The key constraint is backwards compatibility: <c>1</c> means "a negative result", not
/// "an error". That keeps the two codes CI already depends on unchanged — <c>analyze</c>
/// still exits 0/1/2 (matched / analyzed-but-unmatched / bad input) and
/// <c>rules validate</c> still exits 0/1 (clean / has errors) — while everything else gets
/// a specific code. Anything genuinely wrong is 2 or above.
/// </para>
/// </summary>
public static class ExitCodes
{
    /// <summary>Success.</summary>
    public const int Ok = 0;

    /// <summary>
    /// The command ran fine but the answer was negative: no rule matched, the regex didn't
    /// hit, the lint found errors. Not a failure of the tool.
    /// </summary>
    public const int Negative = 1;

    /// <summary>Bad invocation: unknown flag/value, missing argument, unreadable input.</summary>
    public const int Usage = 2;

    /// <summary>The thing you asked for doesn't exist (no such analysis id, no such rule).</summary>
    public const int NotFound = 3;

    /// <summary>Configuration is malformed or invalid (<c>~/.cifail/config.yaml</c>).</summary>
    public const int Config = 4;

    /// <summary>
    /// The history store could not be opened or reached: unavailable provider, bad
    /// connection string, unreachable or unauthorized <c>--server</c>.
    /// </summary>
    public const int StoreUnavailable = 5;

    /// <summary>An optional external dependency is missing (e.g. no AI model, no git).</summary>
    public const int DependencyUnavailable = 6;

    /// <summary>Produced a result, but it isn't usable as-is (e.g. a rule draft that failed validation).</summary>
    public const int NotUsable = 7;

    /// <summary>An unexpected error. Set <c>CIFAIL_DEBUG=1</c> to see the stack trace.</summary>
    public const int Internal = 70;

    /// <summary>Interrupted (Ctrl-C), following the shell's 128 + SIGINT convention.</summary>
    public const int Canceled = 130;
}
