using CiFail.Core.Configuration;
using CiFail.Core.Notifications;
using CiFail.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Notifications;

public class NotificationDispatcherTests
{
    /// <summary>Records every notification it receives; can be told to throw to prove isolation.</summary>
    private sealed class FakeNotifier : INotifier
    {
        public List<Notification> Received { get; } = new();
        public bool Throw { get; init; }
        public string Name => "fake";

        public void Notify(Notification notification)
        {
            Received.Add(notification);
            if (Throw) throw new InvalidOperationException("channel is down");
        }
    }

    private static StoredAnalysis Stored(string fingerprint) => new()
    {
        Id = 1,
        AnalyzedAt = DateTimeOffset.UtcNow,
        Source = "test.log",
        Ecosystem = "dotnet",
        RuleId = "nuget-nu1101",
        Matched = true,
        Fingerprint = fingerprint,
        LogHash = "deadbeef",
        Excerpt = "error NU1101",
    };

    private static Notification Note(NotificationEvent ev, string fp = "nuget-nu1101:abc") =>
        new(ev, Stored(fp));

    [Fact]
    public void Dispatch_delivers_to_every_notifier()
    {
        var a = new FakeNotifier();
        var b = new FakeNotifier();
        var dispatcher = new NotificationDispatcher(new[] { a, b });

        dispatcher.Dispatch(Note(NotificationEvent.NewFailure));

        a.Received.Should().ContainSingle();
        b.Received.Should().ContainSingle();
    }

    [Fact]
    public void HasNotifiers_reflects_whether_any_channel_is_wired()
    {
        new NotificationDispatcher(Array.Empty<INotifier>()).HasNotifiers.Should().BeFalse();
        new NotificationDispatcher(new[] { new FakeNotifier() }).HasNotifiers.Should().BeTrue();
    }

    [Fact]
    public void Only_enabled_events_are_delivered()
    {
        var notifier = new FakeNotifier();
        var dispatcher = new NotificationDispatcher(
            new[] { notifier },
            events: new[] { NotificationEvent.Resolved });

        dispatcher.Dispatch(Note(NotificationEvent.NewFailure));
        dispatcher.Dispatch(Note(NotificationEvent.Resolved));

        notifier.Received.Should().ContainSingle()
            .Which.Event.Should().Be(NotificationEvent.Resolved);
    }

    [Fact]
    public void Empty_event_filter_enables_all_events()
    {
        var notifier = new FakeNotifier();
        var dispatcher = new NotificationDispatcher(new[] { notifier }, events: Array.Empty<NotificationEvent>());

        dispatcher.Dispatch(Note(NotificationEvent.NewFailure));
        dispatcher.Dispatch(Note(NotificationEvent.Recurrence));
        dispatcher.Dispatch(Note(NotificationEvent.Resolved));

        notifier.Received.Should().HaveCount(3);
    }

    [Fact]
    public void Duplicate_event_and_fingerprint_is_suppressed_within_the_dedupe_window()
    {
        var notifier = new FakeNotifier();
        var dispatcher = new NotificationDispatcher(
            new[] { notifier }, dedupeWindow: TimeSpan.FromMinutes(5));

        dispatcher.Dispatch(Note(NotificationEvent.NewFailure, "fp-1"));
        dispatcher.Dispatch(Note(NotificationEvent.NewFailure, "fp-1")); // duplicate

        notifier.Received.Should().ContainSingle();
    }

    [Fact]
    public void Dedupe_is_keyed_on_both_event_and_fingerprint()
    {
        var notifier = new FakeNotifier();
        var dispatcher = new NotificationDispatcher(
            new[] { notifier }, dedupeWindow: TimeSpan.FromMinutes(5));

        // Same fingerprint but different events — both go through.
        dispatcher.Dispatch(Note(NotificationEvent.NewFailure, "fp-1"));
        dispatcher.Dispatch(Note(NotificationEvent.Resolved, "fp-1"));
        // Same event but different fingerprint — also goes through.
        dispatcher.Dispatch(Note(NotificationEvent.NewFailure, "fp-2"));

        notifier.Received.Should().HaveCount(3);
    }

    [Fact]
    public void Zero_dedupe_window_disables_suppression()
    {
        var notifier = new FakeNotifier();
        var dispatcher = new NotificationDispatcher(
            new[] { notifier }, dedupeWindow: TimeSpan.Zero);

        dispatcher.Dispatch(Note(NotificationEvent.NewFailure, "fp-1"));
        dispatcher.Dispatch(Note(NotificationEvent.NewFailure, "fp-1"));

        notifier.Received.Should().HaveCount(2);
    }

    [Fact]
    public void A_throwing_notifier_never_breaks_dispatch_or_the_other_channels()
    {
        var broken = new FakeNotifier { Throw = true };
        var healthy = new FakeNotifier();
        var dispatcher = new NotificationDispatcher(new INotifier[] { broken, healthy });

        var act = () => dispatcher.Dispatch(Note(NotificationEvent.NewFailure));

        act.Should().NotThrow();
        healthy.Received.Should().ContainSingle();
    }

    [Fact]
    public void FromConfig_returns_null_when_no_channel_is_configured()
    {
        NotificationDispatcher.FromConfig(new NotificationsConfig())
            .Should().BeNull();
    }

    [Fact]
    public void FromConfig_builds_a_dispatcher_when_a_slack_url_is_set()
    {
        var dispatcher = NotificationDispatcher.FromConfig(new NotificationsConfig
        {
            SlackWebhookUrl = "https://hooks.slack.example/abc",
        });

        dispatcher.Should().NotBeNull();
        dispatcher!.HasNotifiers.Should().BeTrue();
    }
}
