using CiFail.Core.Configuration;
using CiFail.Core.Notifications.Channels;

namespace CiFail.Core.Notifications;

/// <summary>
/// Fans a <see cref="Notification"/> out to the configured <see cref="INotifier"/>s. Best-effort
/// and non-fatal: each channel is isolated in try/catch, only opted-in events are sent, and a
/// short per-(fingerprint, event) dedupe window keeps a flapping failure from spamming.
/// </summary>
public sealed class NotificationDispatcher
{
    private readonly IReadOnlyList<INotifier> _notifiers;
    private readonly HashSet<NotificationEvent> _events;
    private readonly TimeSpan _dedupeWindow;
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public NotificationDispatcher(
        IEnumerable<INotifier> notifiers,
        IEnumerable<NotificationEvent>? events = null,
        TimeSpan? dedupeWindow = null)
    {
        _notifiers = notifiers.ToList();
        // Null/empty events => all events are enabled.
        var set = (events ?? Enumerable.Empty<NotificationEvent>()).ToHashSet();
        _events = set.Count > 0 ? set : new HashSet<NotificationEvent>(Enum.GetValues<NotificationEvent>());
        _dedupeWindow = dedupeWindow ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>True when at least one channel is configured (so callers can skip work).</summary>
    public bool HasNotifiers => _notifiers.Count > 0;

    public void Dispatch(Notification notification)
    {
        if (_notifiers.Count == 0 || !_events.Contains(notification.Event) || IsDuplicate(notification))
            return;

        foreach (var notifier in _notifiers)
        {
            try { notifier.Notify(notification); }
            catch { /* best-effort: a broken channel must never affect analysis */ }
        }
    }

    private bool IsDuplicate(Notification n)
    {
        if (_dedupeWindow <= TimeSpan.Zero) return false;
        var key = $"{n.EventKey}|{n.Analysis.Fingerprint}";
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (_recent.TryGetValue(key, out var last) && now - last < _dedupeWindow)
                return true;
            _recent[key] = now;
            return false;
        }
    }

    /// <summary>
    /// Build a dispatcher from config: one channel per configured target (Slack, generic webhook,
    /// SMTP). Returns null when nothing is configured, so the server can skip notifications entirely.
    /// </summary>
    public static NotificationDispatcher? FromConfig(NotificationsConfig config)
    {
        var notifiers = new List<INotifier>();
        if (!string.IsNullOrWhiteSpace(config.SlackWebhookUrl))
            notifiers.Add(new SlackNotifier(config.SlackWebhookUrl));
        if (!string.IsNullOrWhiteSpace(config.WebhookUrl))
            notifiers.Add(new WebhookNotifier(config.WebhookUrl));
        if (config.Smtp is { } smtp && !string.IsNullOrWhiteSpace(smtp.Host))
            notifiers.Add(new SmtpNotifier(smtp));

        if (notifiers.Count == 0) return null;

        var events = config.Events
            .Select(Notification.ParseEvent)
            .Where(e => e is not null)
            .Select(e => e!.Value);

        return new NotificationDispatcher(
            notifiers, events, TimeSpan.FromSeconds(Math.Max(0, config.DedupeSeconds)));
    }
}
