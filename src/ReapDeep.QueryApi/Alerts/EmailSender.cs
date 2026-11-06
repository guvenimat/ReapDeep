using System.Net;
using System.Net.Mail;
using System.Text.Json;
using ReapDeep.Analytics;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Alerts;

public sealed class EmailSender(IConfiguration cfg)
{
    public const int MaxRecipients = 10;
    readonly string? _host = cfg["Alerts:Smtp:Host"];
    readonly int _port = cfg.GetValue("Alerts:Smtp:Port", 587);
    readonly string _from = cfg["Alerts:Smtp:From"] ?? "alerts@reapdeep.local";
    readonly string? _user = cfg["Alerts:Smtp:Username"];
    readonly string? _password = cfg["Alerts:Smtp:Password"];
    readonly bool _ssl = cfg.GetValue("Alerts:Smtp:EnableSsl", false);

    public bool Configured => !string.IsNullOrWhiteSpace(_host);

    public static string[] ParseRecipients(List<string>? emails)
    {
        var list = (emails ?? []).Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Ensure.That(list.Length is > 0 and <= MaxRecipients, $"email_to needs 1-{MaxRecipients} addresses");
        foreach (var e in list)
            Ensure.That(e.Length <= 254 && MailAddress.TryCreate(e, out var a) && a.Address == e, $"Invalid email address: '{e}'");
        return list;
    }

    public async Task<WebhookResult> SendAsync(string[] to, string payload, CancellationToken ct)
    {
        if (!Configured) return new WebhookResult(null, "No SMTP server is configured", false);
        var message = JsonSerializer.Deserialize(payload, AppJsonContext.Default.EmailMessage)!;
        using var mail = new MailMessage { From = new MailAddress(_from, "ReapDeep alerts"), Subject = message.Subject, Body = message.Text, IsBodyHtml = false };
        foreach (var r in to) mail.To.Add(r);
        using var smtp = new SmtpClient(_host, _port) { EnableSsl = _ssl, Timeout = 10_000, DeliveryMethod = SmtpDeliveryMethod.Network };
        if (!string.IsNullOrEmpty(_user)) smtp.Credentials = new NetworkCredential(_user, _password);
        try
        {
            await smtp.SendMailAsync(mail, ct);
            return new WebhookResult(250, null, false);
        }
        catch (SmtpException e)
        {
            var code = (int)e.StatusCode;
            return new WebhookResult(code > 0 ? code : null, $"SMTP: {e.Message}", code < 500);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            return new WebhookResult(null, $"SMTP: {e.Message}", true);
        }
    }
}
