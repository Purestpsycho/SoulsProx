using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SoulsProx.Net;

public enum PacketType : byte
{
    Hello = 1,
    HelloAck = 2,
    State = 3,
    Voice = 4,
    Bye = 5,
}

/// <summary>
/// Seals and opens SoulsProx packets with AES-GCM. Each direction has its own key, derived from
/// both players' code secrets, so only someone holding both codes can read or forge packets.
///
/// Wire format: 'S' 'P' version type | counter (8 bytes, big endian) | ciphertext | tag (16 bytes).
/// The 12-byte header is authenticated; the counter is the GCM nonce.
/// </summary>
public sealed class PacketCrypto : IDisposable
{
    public const int HeaderLength = 12;
    public const int TagLength = 16;
    public const int Overhead = HeaderLength + TagLength;
    private const byte Magic0 = (byte)'S', Magic1 = (byte)'P', WireVersion = 1;

    private readonly AesGcm _send;
    private readonly AesGcm _receive;
    private readonly object _sendLock = new();
    private ulong _sendCounter;

    public PacketCrypto(ReadOnlySpan<byte> mySecret, ReadOnlySpan<byte> theirSecret)
    {
        _send = new AesGcm(DeriveKey(mySecret, theirSecret), TagLength);
        _receive = new AesGcm(DeriveKey(theirSecret, mySecret), TagLength);
    }

    private static byte[] DeriveKey(ReadOnlySpan<byte> from, ReadOnlySpan<byte> to)
    {
        var label = Encoding.ASCII.GetBytes("SoulsProx v1 direction key");
        var input = new byte[label.Length + from.Length + to.Length];
        label.CopyTo(input, 0);
        from.CopyTo(input.AsSpan(label.Length));
        to.CopyTo(input.AsSpan(label.Length + from.Length));
        return SHA256.HashData(input);
    }

    public static bool LooksLikePacket(ReadOnlySpan<byte> data) =>
        data.Length >= Overhead && data[0] == Magic0 && data[1] == Magic1 && data[2] == WireVersion;

    public byte[] Seal(PacketType type, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[Overhead + payload.Length];
        var header = packet.AsSpan(0, HeaderLength);
        header[0] = Magic0;
        header[1] = Magic1;
        header[2] = WireVersion;
        header[3] = (byte)type;

        Span<byte> nonce = stackalloc byte[12];
        lock (_sendLock)
        {
            ulong counter = ++_sendCounter;
            BinaryPrimitives.WriteUInt64BigEndian(header[4..], counter);
            header[4..].CopyTo(nonce[4..]);
            _send.Encrypt(nonce, payload, packet.AsSpan(HeaderLength, payload.Length), packet.AsSpan(HeaderLength + payload.Length), header);
        }
        return packet;
    }

    /// <summary>Verifies and decrypts a packet. Returns false for anything forged, corrupted or not ours.</summary>
    public bool TryOpen(ReadOnlySpan<byte> packet, out PacketType type, out byte[] payload)
    {
        type = default;
        payload = [];
        if (!LooksLikePacket(packet)) return false;

        var header = packet[..HeaderLength];
        Span<byte> nonce = stackalloc byte[12];
        header[4..].CopyTo(nonce[4..]);
        var cipher = packet[HeaderLength..^TagLength];
        var plain = new byte[cipher.Length];
        try
        {
            lock (_receive) _receive.Decrypt(nonce, cipher, packet[^TagLength..], plain, header);
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
        type = (PacketType)header[3];
        payload = plain;
        return true;
    }

    public void Dispose()
    {
        _send.Dispose();
        _receive.Dispose();
    }
}
