using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using SoulsProx.Games;
using SoulsProx.Proximity;

namespace SoulsProx.Net;

/// <summary>Plaintext payload layouts (everything is encrypted by <see cref="PacketCrypto"/> on the wire).</summary>
public static class Payloads
{
    private const int MaxNameBytes = 64;

    // State: game u8 | inWorld u8 | frame i64 | x f32 | y f32 | z f32 | nameLen u8 | name utf8
    public static byte[] EncodeState(PeerReport r)
    {
        var name = Encoding.UTF8.GetBytes(r.Name);
        if (name.Length > MaxNameBytes) name = name[..MaxNameBytes];
        var buf = new byte[2 + 8 + 12 + 1 + name.Length];
        buf[0] = (byte)r.Game;
        buf[1] = r.InWorld ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(2), r.Frame);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(10), r.Position.X);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(14), r.Position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(18), r.Position.Z);
        buf[22] = (byte)name.Length;
        name.CopyTo(buf, 23);
        return buf;
    }

    public static bool TryDecodeState(ReadOnlySpan<byte> p, out PeerReport? report)
    {
        report = null;
        if (p.Length < 23 || p.Length < 23 + p[22]) return false;
        var pos = new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(p[10..]),
            BinaryPrimitives.ReadSingleLittleEndian(p[14..]),
            BinaryPrimitives.ReadSingleLittleEndian(p[18..]));
        report = new PeerReport(
            (GameId)p[0],
            p[1] != 0,
            Encoding.UTF8.GetString(p.Slice(23, p[22])),
            pos,
            BinaryPrimitives.ReadInt64LittleEndian(p[2..]));
        return true;
    }

    // Voice: seq u32 | flags u8 (bit 0 = radio) | opus bytes
    public static byte[] EncodeVoice(uint seq, bool radio, ReadOnlySpan<byte> opus)
    {
        var buf = new byte[5 + opus.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, seq);
        buf[4] = radio ? (byte)1 : (byte)0;
        opus.CopyTo(buf.AsSpan(5));
        return buf;
    }

    public static bool TryDecodeVoice(byte[] p, out uint seq, out bool radio, out ReadOnlyMemory<byte> opus)
    {
        seq = 0;
        radio = false;
        opus = default;
        if (p.Length < 6) return false;
        seq = BinaryPrimitives.ReadUInt32LittleEndian(p);
        radio = (p[4] & 1) != 0;
        opus = p.AsMemory(5);
        return true;
    }
}
