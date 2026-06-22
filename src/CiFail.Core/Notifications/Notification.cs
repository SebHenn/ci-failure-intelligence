using CiFail.Core.Storage;

namespace CiFail.Core.Notifications;

/// <summary>What happened to a failure, for outbound notifications (R13).</summary>
public enum NotificationEvent
{
    /// <summary>A fingerprint seen for the first time.</summary>
    NewFailure,

    /// <summary>A previously-seen fingerprint happened again.</summary>
    Recurrence,

    /// <summary>A failure was marked resolved (manual or auto).</summary>
    Resolved,
}

/// <summary>A single notification: the event plus the analysis it concerns.</summary>
public sealed record Notification(NotificationEvent Event, StoredAnalysis Analysis)
{
    /// <summary>Stable kebab-case key used in config (<c>events:</c>) and webhook payloads.</summary>
    public string EventKey => Event switch
    {
        NotificationEvent.NewFailure => "new-failure",
        NotificationEvent.Recurrence => "recurrence",
        NotificationEvent.Resolved => "resolved",
        _ => "failure",
    };

    /// <summary>A short human title, e.g. for a Slack message or email subject.</summary>
    public string Title => Event switch
    {
        NotificationEvent.NewFailure => $"New CI failure: {Analysis.RuleId}",
        NotificationEvent.Recurrence => $"CI failure recurred: {Analysis.RuleId}",
        NotificationEvent.Resolved => $"CI failure resolved: {Analysis.RuleId}",
        _ => $"CI failure: {Analysis.RuleId}",
    };

    /// <summary>Parse a kebab-case event key from config; null if unrecognized.</summary>
    public static NotificationEvent? ParseEvent(string key) => key.Trim().ToLowerInvariant() switch
    {
        "new-failure" or "newfailure" => NotificationEvent.NewFailure,
        "recurrence" => NotificationEvent.Recurrence,
        "resolved" => NotificationEvent.Resolved,
        _ => null,
    };
}

/// <summary>
/// An outbound channel (Slack, generic webhook, email…). Implementations should be quick and may
/// throw — the <see cref="NotificationDispatcher"/> isolates and swallows failures so a broken
/// channel never affects analysis.
/// </summary>
public interface INotifier
{
    /// <summary>Channel name, for logging/diagnostics.</summary>
    string Name { get; }

    /// <summary>Deliver one notification (best-effort).</summary>
    void Notify(Notification notification);
}
