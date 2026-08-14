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

    /// <summary>
    /// Send a notification to every enabled channel, blocking until they have all been tried.
    /// </summary>
    public void Dispatch(Notification notification)
    {
        if (!ShouldSend(notification)) return;
        Deliver(notification);
    }

    /// <summary>
    /// Start delivery and return immediately, handing back a task that completes when every
    /// channel has been tried.
    ///
    /// <para>
    /// Channels are blocking HTTP/SMTP calls with a 10-second timeout each, and the server
    /// dispatched them inline — so six configured channels could add a minute to the
    /// <c>POST /analyze</c> that triggered them, for work the caller does not wait on. Filtering
    /// and dedupe still happen synchronously here, so "was this suppressed?" is decided in the
    /// caller's order rather than by whichever background task wins a race.
    /// </para>
    ///
    /// <para>
    /// The returned task is what makes this testable: a caller that needs to observe the effect
    /// (a test, or a shutdown path) can await it, so moving work off the request thread does not
    /// turn the notification tests into a timing gamble.
    /// </para>
    /// </summary>
    public Task DispatchAsync(Notification notification)
    {
        if (!ShouldSend(notification)) return Task.CompletedTask;
        return Task.Run(() => Deliver(notification));
    }

    /// <summary>Event filter + dedupe. Cheap, and deliberately not deferred — see DispatchAsync.</summary>
    private bool ShouldSend(Notification notification) =>
        _notifiers.Count > 0 && _events.Contains(notification.Event) && !IsDuplicate(notification);

    private void Deliver(Notification notification)
    {
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
            PruneExpired(now);
            return false;
        }
    }

    /// <summary>
    /// Drop entries that can no longer suppress anything.
    ///
    /// <para>
    /// Every distinct <c>(event, fingerprint)</c> added one permanent entry, so in a long-running
    /// <c>cifail serve</c> the dedupe map grew without bound — one entry per failure the server
    /// had ever seen, for the lifetime of the process, to answer a question with a five-minute
    /// horizon. Sweeping only when the map is large keeps the common path a single lookup.
    /// </para>
    /// </summary>
    private void PruneExpired(DateTimeOffset now)
    {
        if (_recent.Count < PruneThreshold) return;

        foreach (var key in _recent.Where(e => now - e.Value >= _dedupeWindow)
                     .Select(e => e.Key).ToList())
        {
            _recent.Remove(key);
        }
    }

    /// <summary>Entry count that triggers a sweep — high enough that sweeps are rare.</summary>
    private const int PruneThreshold = 512;

    /// <summary>
    /// Build a dispatcher from config: one channel per configured target (Slack, generic webhook,
    /// Discord, Teams, SMTP, GitHub issue). Returns null when nothing is configured, so the server
    /// can skip notifications entirely.
    /// </summary>
    public static NotificationDispatcher? FromConfig(NotificationsConfig config)
    {
        var notifiers = new List<INotifier>();
        if (!string.IsNullOrWhiteSpace(config.SlackWebhookUrl))
            notifiers.Add(new SlackNotifier(config.SlackWebhookUrl));
        if (!string.IsNullOrWhiteSpace(config.WebhookUrl))
            notifiers.Add(new WebhookNotifier(config.WebhookUrl));
        if (!string.IsNullOrWhiteSpace(config.DiscordWebhookUrl))
            notifiers.Add(new DiscordNotifier(config.DiscordWebhookUrl));
        if (!string.IsNullOrWhiteSpace(config.TeamsWebhookUrl))
            notifiers.Add(new TeamsNotifier(config.TeamsWebhookUrl));
        if (config.Smtp is { } smtp && !string.IsNullOrWhiteSpace(smtp.Host))
            notifiers.Add(new SmtpNotifier(smtp));
        if (GitHubIssueNotifier.FromConfig(config.GitHub) is { } gh)
            notifiers.Add(gh);

        if (notifiers.Count == 0) return null;

        var events = config.Events
            .Select(Notification.ParseEvent)
            .Where(e => e is not null)
            .Select(e => e!.Value);

        return new NotificationDispatcher(
            notifiers, events, TimeSpan.FromSeconds(Math.Max(0, config.DedupeSeconds)));
    }
}
