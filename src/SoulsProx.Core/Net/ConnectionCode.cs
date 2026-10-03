using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SoulsProx.Net;

/// <summary>
/// What two players swap (e.g. over Discord) to find each other: where to reach me,
/// plus a random secret that, combined with the friend's secret, keys the encryption.
/// Looks like "SPX-ABCD-EFGH-…".
/// </summary>
public sealed record ConnectionCode(IPEndPoint? Public, IPEndPoint? Lan, byte[] Secret)
{
    public const int SecretLength = 8;
    private const byte Version = 1;
    private const int ByteLength = 1 + 6 + 6 + SecretLength + 1;
    private const string Prefix = "SPX";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretLength);

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        bytes[0] = Version;
        WriteEndpoint(bytes[1..7], Public);
        WriteEndpoint(bytes[7..13], Lan);
        Secret.AsSpan().CopyTo(bytes[13..]);
        bytes[^1] = Checksum(bytes[..^1]);

        string body = Base32Encode(bytes);
        var sb = new StringBuilder(Prefix);
        for (int i = 0; i < body.Length; i += 4) sb.Append('-').Append(body.AsSpan(i, Math.Min(4, body.Length - i)));
        return sb.ToString();
    }

    public static bool TryParse(string? text, out ConnectionCode? code, out string? error)
    {
        code = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "The code is empty."; return false; }

        var clean = new StringBuilder();
        foreach (char ch in text.ToUpperInvariant())
            if (char.IsAsciiLetterOrDigit(ch)) clean.Append(ch);
        string s = clean.ToString();
        if (!s.StartsWith(Prefix, StringComparison.Ordinal)) { error = "That doesn't look like a SoulsProx code (it should start with SPX-)."; return false; }

        var bytes = Base32Decode(s[Prefix.Length..]);
        if (bytes is null || bytes.Length < ByteLength) { error = "The code is incomplete. Copy the whole thing."; return false; }
        if (bytes[0] != Version) { error = "That code is from a different SoulsProx version."; return false; }
        if (Checksum(bytes.AsSpan(0, ByteLength - 1)) != bytes[ByteLength - 1]) { error = "The code has a typo in it. Copy and paste it rather than typing."; return false; }

        code = new ConnectionCode(ReadEndpoint(bytes.AsSpan(1, 6)), ReadEndpoint(bytes.AsSpan(7, 6)), bytes.AsSpan(13, SecretLength).ToArray());
        return true;
    }

    private static void WriteEndpoint(Span<byte> dest, IPEndPoint? ep)
    {
        dest.Clear();
        if (ep is null || !ep.Address.TryWriteBytes(dest[..4], out int written) || written != 4) return;
        BinaryPrimitives.WriteUInt16BigEndian(dest[4..], (ushort)ep.Port);
    }

    private static IPEndPoint? ReadEndpoint(ReadOnlySpan<byte> src)
    {
        int port = BinaryPrimitives.ReadUInt16BigEndian(src[4..]);
        if (port == 0 || src[..4].IndexOfAnyExcept((byte)0) < 0) return null;
        return new IPEndPoint(new IPAddress(src[..4]), port);
    }

    private static byte Checksum(ReadOnlySpan<byte> data) => SHA256.HashData(data)[0];

    private static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[]? Base32Decode(string s)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (char raw in s)
        {
            char ch = raw switch { 'O' => '0', 'I' or 'L' => '1', _ => raw };
            int v = Alphabet.IndexOf(ch);
            if (v < 0) return null;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. bytes];
    }
}
