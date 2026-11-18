using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReapDeep.Analytics;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Alerts;

public sealed class WebhookSender : IDisposable
{
    public const int MaxUrlLength = 2000;
    readonly bool _allowPrivate;
    readonly HttpClient _http;

    public WebhookSender(IConfiguration cfg)
    {
        _allowPrivate = bool.TryParse(cfg["Alerts:AllowPrivateWebhooks"], out var allow) && allow;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (ctx, ct) =>
            {
                var address = await ResolveAllowedAsync(ctx.DnsEndPoint.Host, ct);
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, ctx.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ReapDeep-Alerts/1.0");
    }

    public static Uri ParseUrl(string? url)
    {
        var text = Ensure.NotBlank(url, "webhook_url", MaxUrlLength);
        Ensure.That(Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo),
            "webhook_url must be an http or https URL without credentials");
        return uri!;
    }

    public async Task CheckDestinationAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            await ResolveAllowedAsync(uri.Host, ct);
        }
        catch (WebhookBlockedException e)
        {
            throw new ValidationException(e.Message);
        }
        catch (SocketException)
        {
            throw new ValidationException($"webhook_url: host '{uri.Host}' does not resolve");
        }
    }

    public static string Serialize(AlertWebhookPayload payload) => JsonSerializer.Serialize(payload, AppJsonContext.Default.AlertWebhookPayload);

    public async Task<WebhookResult> SendAsync(Uri uri, string secret, string payload, long alertId, long deliveryId, int attempt, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.TryAddWithoutValidation("X-ReapDeep-Signature", "sha256=" + Sign(secret, body));
        req.Headers.TryAddWithoutValidation("X-ReapDeep-Alert", alertId.ToString(CultureInfo.InvariantCulture));
        req.Headers.TryAddWithoutValidation("X-ReapDeep-Delivery", deliveryId.ToString(CultureInfo.InvariantCulture));
        req.Headers.TryAddWithoutValidation("X-ReapDeep-Attempt", attempt.ToString(CultureInfo.InvariantCulture));
        try
        {
            using var res = await _http.SendAsync(req, ct);
            var status = (int)res.StatusCode;
            return res.IsSuccessStatusCode ? new(status, null, false) : new(status, $"HTTP {status}", AlertRules.Retryable(status));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or WebhookBlockedException)
        {
            var blocked = e as WebhookBlockedException ?? e.InnerException as WebhookBlockedException;
            return blocked is not null ? new(null, blocked.Message, false) : new(null, e.Message, true);
        }
    }

    public static string Sign(string secret, byte[] body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    async Task<IPAddress> ResolveAllowedAsync(string host, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0) throw new WebhookBlockedException($"webhook_url: host '{host}' does not resolve");
        if (!_allowPrivate && addresses.FirstOrDefault(IsPrivate) is { } blocked)
            throw new WebhookBlockedException($"webhook_url: {host} resolves to a private or local address ({blocked}); set Alerts__AllowPrivateWebhooks to allow it");
        return addresses[0];
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
            return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var v6 = ip.GetAddressBytes();
            if (EmbeddedIpv4(v6) is { } inner) return IsPrivate(inner);
            return (v6[0] & 0xFE) == 0xFC;
        }
        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => true,
            100 => b[1] is >= 64 and <= 127,
            169 => b[1] == 254,
            172 => b[1] is >= 16 and <= 31,
            192 => b[1] == 168 || (b[1] == 0 && b[2] == 0),
            >= 224 => true,
            _ => false,
        };
    }

    static IPAddress? EmbeddedIpv4(byte[] v6)
    {
        if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xFF && v6[3] == 0x9B && v6.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
            return new IPAddress(v6.AsSpan(12, 4));
        if (v6[0] == 0x20 && v6[1] == 0x02) return new IPAddress(v6.AsSpan(2, 4));
        if (v6.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0 && v6.AsSpan(12).IndexOfAnyExcept((byte)0) >= 0) return new IPAddress(v6.AsSpan(12, 4));
        return null;
    }

    public void Dispose() => _http.Dispose();
}

public sealed record WebhookResult(int? Status, string? Error, bool Retryable);

public sealed class WebhookBlockedException(string message) : Exception(message);
