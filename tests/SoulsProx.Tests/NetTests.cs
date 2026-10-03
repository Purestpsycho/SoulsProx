using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using SoulsProx.Games;
using SoulsProx.Net;
using SoulsProx.Proximity;

namespace SoulsProx.Tests;

public class ConnectionCodeTests
{
    [Fact]
    public void Round_trips()
    {
        var code = new ConnectionCode(IPEndPoint.Parse("203.0.113.7:47800"), IPEndPoint.Parse("192.168.1.20:47800"), ConnectionCode.NewSecret());
        string text = code.ToString();
        Assert.StartsWith("SPX-", text);

        Assert.True(ConnectionCode.TryParse(text, out var parsed, out _));
        Assert.Equal(code.Public, parsed!.Public);
        Assert.Equal(code.Lan, parsed.Lan);
        Assert.Equal(code.Secret, parsed.Secret);
    }

    [Fact]
    public void Tolerates_case_spaces_and_lookalike_letters()
    {
        var code = new ConnectionCode(null, IPEndPoint.Parse("10.0.0.2:1234"), ConnectionCode.NewSecret());
        string messy = "  " + code.ToString().ToLowerInvariant().Replace("-", " - ").Replace('0', 'o') + "\n";
        Assert.True(ConnectionCode.TryParse(messy, out var parsed, out _));
        Assert.Null(parsed!.Public);
        Assert.Equal(code.Lan, parsed.Lan);
    }

    [Fact]
    public void Detects_typos()
    {
        var text = new ConnectionCode(IPEndPoint.Parse("203.0.113.7:47800"), null, ConnectionCode.NewSecret()).ToString();
        int i = text.Length - 6;
        char replacement = text[i] == 'A' ? 'B' : 'A';
        var broken = text[..i] + replacement + text[(i + 1)..];
        Assert.False(ConnectionCode.TryParse(broken, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Rejects_garbage()
    {
        Assert.False(ConnectionCode.TryParse("hello", out _, out _));
        Assert.False(ConnectionCode.TryParse("", out _, out _));
        Assert.False(ConnectionCode.TryParse("SPX-12", out _, out _));
    }
}

public class PacketCryptoTests
{
    private static (PacketCrypto A, PacketCrypto B) Pair()
    {
        var a = ConnectionCode.NewSecret();
        var b = ConnectionCode.NewSecret();
        return (new PacketCrypto(a, b), new PacketCrypto(b, a));
    }

    [Fact]
    public void Round_trips_both_directions()
    {
        var (a, b) = Pair();
        byte[] payload = [1, 2, 3, 4, 5];
        Assert.True(b.TryOpen(a.Seal(PacketType.Voice, payload), out var type, out var opened));
        Assert.Equal(PacketType.Voice, type);
        Assert.Equal(payload, opened);

        Assert.True(a.TryOpen(b.Seal(PacketType.State, payload), out type, out opened));
        Assert.Equal(PacketType.State, type);
        Assert.Equal(payload, opened);
    }

    [Fact]
    public void Rejects_tampering()
    {
        var (a, b) = Pair();
        var packet = a.Seal(PacketType.Voice, [9, 9, 9]);
        packet[^1] ^= 1;
        Assert.False(b.TryOpen(packet, out _, out _));

        var packet2 = a.Seal(PacketType.Voice, [9, 9, 9]);
        packet2[3] = (byte)PacketType.Bye; // header is authenticated too
        Assert.False(b.TryOpen(packet2, out _, out _));
    }

    [Fact]
    public void Rejects_other_sessions_and_own_packets()
    {
        var (a, _) = Pair();
        var (_, stranger) = Pair();
        var packet = a.Seal(PacketType.Hello, []);
        Assert.False(stranger.TryOpen(packet, out _, out _));
        Assert.False(a.TryOpen(packet, out _, out _)); // can't be reflected back at the sender
    }
}

public class PayloadTests
{
    [Fact]
    public void State_round_trips()
    {
        var report = new PeerReport(GameId.DarkSouls3, true, "Solaire of Astora", new Vector3(1.5f, -2.25f, 300f), 1234567890123);
        Assert.True(Payloads.TryDecodeState(Payloads.EncodeState(report), out var decoded));
        Assert.Equal(report, decoded);
    }

    [Fact]
    public void Voice_round_trips()
    {
        byte[] opus = [7, 8, 9];
        var p = Payloads.EncodeVoice(42, true, opus);
        Assert.True(Payloads.TryDecodeVoice(p, out uint seq, out bool radio, out var data));
        Assert.Equal(42u, seq);
        Assert.True(radio);
        Assert.Equal(opus, data.ToArray());
    }
}

public class StunTests
{
    [Fact]
    public void Parses_xor_mapped_address()
    {
        var request = Stun.CreateBindingRequest(out var txId);
        Assert.Equal(20, request.Length);

        // Build a Binding Success response for 203.0.113.7:47800.
        var response = new byte[20 + 12];
        BinaryPrimitives.WriteUInt16BigEndian(response, 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 12);
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(4), Stun.MagicCookie);
        txId.CopyTo(response, 8);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(20), 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(22), 8);
        response[25] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(26), (ushort)(47800 ^ (Stun.MagicCookie >> 16)));
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(28), 0xCB007107u ^ Stun.MagicCookie);

        Assert.True(Stun.TryParseBindingResponse(response, out var parsedTx, out var mapped));
        Assert.Equal(txId, parsedTx);
        Assert.Equal(IPEndPoint.Parse("203.0.113.7:47800"), mapped);
    }

    [Fact]
    public void Our_packets_are_not_mistaken_for_stun()
    {
        var crypto = new PacketCrypto(ConnectionCode.NewSecret(), ConnectionCode.NewSecret());
        var packet = crypto.Seal(PacketType.Voice, new byte[40]);
        Assert.False(Stun.IsStunMessage(packet));
    }
}

public class PeerLinkTests
{
    [Fact]
    public async Task Two_links_on_one_pc_connect_and_exchange_state_and_voice()
    {
        using var a = new PeerLink(0, ConnectionCode.NewSecret());
        using var b = new PeerLink(0, ConnectionCode.NewSecret());

        var gotState = new TaskCompletionSource<PeerReport>();
        var gotVoice = new TaskCompletionSource<(uint, byte[], bool)>();
        b.ReportReceived += r => gotState.TrySetResult(r);
        b.VoiceReceived += (seq, opus, radio) => gotVoice.TrySetResult((seq, opus.ToArray(), radio));

        // Swap codes (they only contain LAN addresses here since we skip STUN).
        a.Connect(b.MyCode);
        b.Connect(a.MyCode);

        await WaitUntil(() => a.State == LinkState.Connected && b.State == LinkState.Connected, TimeSpan.FromSeconds(5));

        var report = new PeerReport(GameId.DarkSouls3, true, "Siegward", new Vector3(1, 2, 3), 0);
        a.SendState(report);
        a.SendVoice(7, [1, 2, 3], radio: true);

        Assert.Equal(report, await gotState.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        var (seq, data, isRadio) = await gotVoice.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(7u, seq);
        Assert.Equal([1, 2, 3], data);
        Assert.True(isRadio);
    }

    [Fact]
    public void Refuses_own_code()
    {
        using var a = new PeerLink(0, ConnectionCode.NewSecret());
        Assert.Throws<ArgumentException>(() => a.Connect(a.MyCode));
    }

    [Fact]
    public async Task Disconnect_notifies_friend()
    {
        using var a = new PeerLink(0, ConnectionCode.NewSecret());
        using var b = new PeerLink(0, ConnectionCode.NewSecret());
        a.Connect(b.MyCode);
        b.Connect(a.MyCode);
        await WaitUntil(() => a.State == LinkState.Connected && b.State == LinkState.Connected, TimeSpan.FromSeconds(5));

        a.Disconnect();
        Assert.Equal(LinkState.Idle, a.State);
        await WaitUntil(() => b.State == LinkState.Connecting, TimeSpan.FromSeconds(3));
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time");
            await Task.Delay(20);
        }
    }
}
