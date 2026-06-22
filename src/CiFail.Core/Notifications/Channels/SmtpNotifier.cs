using System.Net;
using System.Net.Mail;
using CiFail.Core.Configuration;

namespace CiFail.Core.Notifications.Channels;

/// <summary>
/// Optional email channel via SMTP (BCL <see cref="SmtpClient"/>). The password, if any, comes
/// from the env var named by <see cref="SmtpConfig.PasswordEnv"/> — never the config file.
/// </summary>
public sealed class SmtpNotifier : INotifier
{
    private readonly SmtpConfig _config;

    public SmtpNotifier(SmtpConfig config) => _config = config;

    public string Name => "smtp";

    public void Notify(Notification n)
    {
        var a = n.Analysis;
        using var message = new MailMessage(_config.From, _config.To)
        {
            Subject = n.Title,
            Body =
                $"Event: {n.EventKey}\n" +
                $"Rule: {a.RuleId}\nFingerprint: {a.Fingerprint}\n" +
                $"Ecosystem: {a.Ecosystem}\nSource: {a.Source}\nStatus: {a.Status}\n\n" +
                a.Excerpt,
        };

        using var client = new SmtpClient(_config.Host, _config.Port) { EnableSsl = _config.UseSsl };
        var password = string.IsNullOrEmpty(_config.PasswordEnv)
            ? null
            : Environment.GetEnvironmentVariable(_config.PasswordEnv);
        if (!string.IsNullOrEmpty(_config.Username))
            client.Credentials = new NetworkCredential(_config.Username, password);

        client.Send(message);
    }
}
