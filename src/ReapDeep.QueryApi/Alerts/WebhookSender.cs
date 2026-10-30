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

    public async Task<(int? Status, string? Error)> SendAsync(Uri uri, string secret, AlertWebhookPayload payload, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, AppJsonContext.Default.AlertWebhookPayload);
        using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.TryAddWithoutValidation("X-ReapDeep-Signature", "sha256=" + Sign(secret, body));
        req.Headers.TryAddWithoutValidation("X-ReapDeep-Alert", payload.AlertId.ToString());
        try
        {
            using var res = await _http.SendAsync(req, ct);
            var status = (int)res.StatusCode;
            return (status, res.IsSuccessStatusCode ? null : $"HTTP {status}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or WebhookBlockedException)
        {
            return (null, (e.InnerException as WebhookBlockedException)?.Message ?? e.Message);
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
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
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

    public void Dispose() => _http.Dispose();
}

public sealed class WebhookBlockedException(string message) : Exception(message);
