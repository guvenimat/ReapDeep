using System.Net;
using System.Net.Sockets;

namespace ReapDeep.Ingestion.Capture;

public static class IpAnonymizer
{
    const int KeptIpv6Bytes = 6;

    public static string? Format(IPAddress? address, bool anonymize)
    {
        if (address is null) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (!anonymize) return address.ToString();

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork) bytes[3] = 0;
        else Array.Clear(bytes, KeptIpv6Bytes, bytes.Length - KeptIpv6Bytes);
        return new IPAddress(bytes).ToString();
    }
}
