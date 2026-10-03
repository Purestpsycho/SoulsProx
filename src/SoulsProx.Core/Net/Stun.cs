using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace SoulsProx.Net;

/// <summary>
/// Minimal STUN (RFC 5389) Binding client: asks a public server "what address do my packets come from?"
/// so the friend knows where to send. Only addresses are exchanged; no audio goes through STUN servers.
/// </summary>
public static class Stun
{
    public const uint MagicCookie = 0x2112A442;
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingSuccess = 0x0101;
    private const ushort AttrMappedAddress = 0x0001;
    private const ushort AttrXorMappedAddress = 0x0020;

    public static readonly (string Host, int Port)[] Servers =
    [
        ("stun.l.google.com", 19302),
        ("stun.cloudflare.com", 3478),
        ("stun1.l.google.com", 19302),
    ];

    public static byte[] CreateBindingRequest(out byte[] transactionId)
    {
        transactionId = RandomNumberGenerator.GetBytes(12);
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(packet, BindingRequest);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), MagicCookie);
        transactionId.CopyTo(packet, 8);
        return packet;
    }

    public static bool IsStunMessage(ReadOnlySpan<byte> data) =>
        data.Length >= 20 && (data[0] & 0xC0) == 0 && BinaryPrimitives.ReadUInt32BigEndian(data[4..]) == MagicCookie;

    public static bool TryParseBindingResponse(ReadOnlySpan<byte> data, out byte[] transactionId, out IPEndPoint? mapped)
    {
        transactionId = [];
        mapped = null;
        if (!IsStunMessage(data) || BinaryPrimitives.ReadUInt16BigEndian(data) != BindingSuccess) return false;
        transactionId = data.Slice(8, 12).ToArray();

        int length = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        var attrs = data.Slice(20, Math.Min(length, data.Length - 20));
        IPEndPoint? plain = null;
        while (attrs.Length >= 4)
        {
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(attrs);
            int len = BinaryPrimitives.ReadUInt16BigEndian(attrs[2..]);
            if (attrs.Length < 4 + len) break;
            var value = attrs.Slice(4, len);
            // Address attribute: 0x00, family (1 = IPv4), port, address.
            if (len >= 8 && value[1] == 0x01)
            {
                int port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
                var addr = value.Slice(4, 4).ToArray();
                if (type == AttrXorMappedAddress)
                {
                    port ^= (int)(MagicCookie >> 16);
                    Span<byte> cookie = stackalloc byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(cookie, MagicCookie);
                    for (int i = 0; i < 4; i++) addr[i] ^= cookie[i];
                    mapped = new IPEndPoint(new IPAddress(addr), port);
                    return true;
                }
                if (type == AttrMappedAddress) plain = new IPEndPoint(new IPAddress(addr), port);
            }
            int padded = (len + 3) & ~3;
            attrs = attrs[Math.Min(attrs.Length, 4 + padded)..];
        }
        mapped = plain;
        return mapped is not null;
    }
}
