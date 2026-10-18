using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using WslRemoteSetup.Core.Validation;

namespace WslRemoteSetup.Core.Network;

/// <summary>IPv4 CIDR. 실제 어댑터의 주소·접두 길이에서 계산하며 /24를 가정하지 않는다.</summary>
public readonly record struct Ipv4Cidr(uint Network, int PrefixLength)
{
    public static Ipv4Cidr FromAddress(IPAddress address, int prefixLength)
    {
        if (prefixLength is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(prefixLength));
        var value = ToUInt32(address);
        return new Ipv4Cidr(value & Mask(prefixLength), prefixLength);
    }

    public static bool TryParse(string? text, out Ipv4Cidr cidr)
    {
        cidr = default;
        if (string.IsNullOrEmpty(text)) return false;
        var slash = text.IndexOf('/');
        if (slash <= 0) return false;
        if (!InputValidator.TryParseIPv4(text[..slash], out var address)) return false;
        var prefixText = text[(slash + 1)..];
        if (!int.TryParse(prefixText, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix > 32)
            return false;
        cidr = FromAddress(address, prefix);
        return true;
    }

    public bool Contains(IPAddress address) => (ToUInt32(address) & Mask(PrefixLength)) == Network;

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, Network);
        return $"{new IPAddress(bytes)}/{PrefixLength}";
    }

    internal static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) throw new ArgumentException("IPv4 주소가 아닙니다.", nameof(address));
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static uint Mask(int prefix) => prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);

    public static bool IsPrivate(IPAddress address)
    {
        var v = ToUInt32(address);
        return (v & 0xFF000000) == 0x0A000000      // 10/8
            || (v & 0xFFF00000) == 0xAC100000      // 172.16/12
            || (v & 0xFFFF0000) == 0xC0A80000;     // 192.168/16
    }

    public static bool IsLoopbackOrLinkLocal(IPAddress address)
    {
        var v = ToUInt32(address);
        return (v & 0xFF000000) == 0x7F000000 || (v & 0xFFFF0000) == 0xA9FE0000;
    }
}
