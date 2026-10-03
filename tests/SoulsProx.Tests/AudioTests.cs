using SoulsProx.Audio;

namespace SoulsProx.Tests;

public class AudioTests
{
    private const int Frame = VoiceFormat.FrameSamples;

    private static float[] Tone(int frameIndex, float freq = 440f, float amp = 0.3f)
    {
        var f = new float[Frame];
        for (int i = 0; i < Frame; i++)
        {
            double t = (frameIndex * Frame + i) / (double)VoiceFormat.SampleRate;
            f[i] = amp * (float)Math.Sin(2 * Math.PI * freq * t);
        }
        return f;
    }

    private static List<byte[]> EncodeTone(int frames)
    {
        using var enc = new VoiceEncoder();
        return Enumerable.Range(0, frames).Select(i => enc.Encode(Tone(i)).ToArray()).ToList();
    }

    private static float[] ReadStereo(PeerVoice voice, int frames)
    {
        var buf = new float[frames * Frame * 2];
        int n = voice.Read(buf);
        Assert.Equal(buf.Length, n);
        return buf;
    }

    private static (double L, double R) Energy(ReadOnlySpan<float> stereo)
    {
        double l = 0, r = 0;
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            l += stereo[i] * stereo[i];
            r += stereo[i + 1] * stereo[i + 1];
        }
        return (l, r);
    }

    [Fact]
    public void Encoder_produces_small_packets()
    {
        var packets = EncodeTone(50);
        Assert.All(packets, p => Assert.InRange(p.Length, 1, 400));
        double kbps = packets.Sum(p => p.Length) * 8 / 1000.0; // 50 frames = 1 s
        Assert.InRange(kbps, 5, 64);
    }

    /// <summary>Feeds packets the way the network does (one per 20 ms of playback) and returns everything played.</summary>
    private static float[] Stream(PeerVoice voice, List<byte[]> packets, IEnumerable<int> order, int extraFrames = 0)
    {
        var output = new List<float>();
        foreach (int i in order)
        {
            voice.Enqueue((uint)i, packets[i], false);
            output.AddRange(ReadStereo(voice, 1));
        }
        if (extraFrames > 0) output.AddRange(ReadStereo(voice, extraFrames));
        return [.. output];
    }

    private static ReadOnlySpan<float> LastFrames(float[] stereo, int frames) => stereo.AsSpan(stereo.Length - frames * Frame * 2);

    [Fact]
    public void Decoded_voice_is_audible_and_silent_when_gain_is_zero()
    {
        var packets = EncodeTone(60);
        var voice = new PeerVoice();
        voice.SetSpatial(1f, 0f);
        var (l, r) = Energy(LastFrames(Stream(voice, packets, Enumerable.Range(0, 30)), 10));
        Assert.True(l > 1 && r > 1, $"expected sound, got L={l} R={r}");
        Assert.Equal(l, r, 1); // centred

        voice.SetSpatial(0f, 0f);
        var (l2, r2) = Energy(LastFrames(Stream(voice, packets, Enumerable.Range(30, 30)), 10));
        Assert.True(l2 < 0.01 && r2 < 0.01, $"expected silence, got L={l2} R={r2}");
    }

    [Fact]
    public void Pan_right_makes_right_channel_louder()
    {
        var packets = EncodeTone(30);
        var voice = new PeerVoice();
        voice.SetSpatial(1f, 0.8f);
        var (l, r) = Energy(LastFrames(Stream(voice, packets, Enumerable.Range(0, 30)), 10));
        Assert.True(r > l * 10, $"L={l} R={r}");
    }

    [Fact]
    public void Survives_loss_and_reordering()
    {
        var packets = EncodeTone(40);
        var voice = new PeerVoice();
        voice.SetSpatial(1f, 0f);
        // Drop 10 and 20, swap 15/16.
        var order = Enumerable.Range(0, 40).Where(i => i is not 10 and not 20).ToList();
        (order[14], order[15]) = (order[15], order[14]);
        var played = Stream(voice, packets, order);

        // Every 20 ms chunk after start-up should have sound in it (losses are concealed, not gaps).
        for (int frame = 5; frame < order.Count - 1; frame++)
        {
            var (l, _) = Energy(played.AsSpan(frame * Frame * 2, Frame * 2));
            Assert.True(l > 0.01, $"frame {frame} was silent");
        }
    }

    [Fact]
    public void Goes_quiet_after_speech_ends_and_restarts_on_new_speech()
    {
        var packets = EncodeTone(60);
        var voice = new PeerVoice();
        voice.SetSpatial(1f, 0f);
        var first = Stream(voice, packets, Enumerable.Range(0, 10), extraFrames: 20);
        var (quiet, _) = Energy(LastFrames(first, 5));
        Assert.True(quiet < 0.01, $"expected silence after the talk spurt, got {quiet}");

        // New talk spurt with a sequence jump (the sender wasn't transmitting for a while).
        var second = Stream(voice, packets, Enumerable.Range(40, 20));
        var (l, _) = Energy(LastFrames(second, 10));
        Assert.True(l > 1, $"expected the new spurt to play, got {l}");
    }

    [Fact]
    public void Backlog_is_trimmed_to_keep_latency_low()
    {
        var packets = EncodeTone(30);
        var voice = new PeerVoice();
        voice.SetSpatial(1f, 0f);
        for (uint i = 0; i < 30; i++) voice.Enqueue(i, packets[(int)i], false);
        // Only the newest few frames (plus a little concealment) should play, not 600 ms of stale audio.
        var played = ReadStereo(voice, 30);
        var (tail, _) = Energy(LastFrames(played, 20));
        Assert.True(tail < 0.01, $"stale audio kept playing: {tail}");
    }
    [Fact]
    public void Voice_gate_hangs_open_then_closes()
    {
        var gate = new VoiceGate { ThresholdDb = -40 };
        Assert.True(gate.ShouldTransmit(-20, false, false));
        for (int i = 0; i < VoiceGate.HangFrames - 1; i++) Assert.True(gate.ShouldTransmit(-80, false, false));
        Assert.False(gate.ShouldTransmit(-80, false, false));
    }

    [Fact]
    public void Push_to_talk_and_radio()
    {
        var gate = new VoiceGate { Mode = TalkMode.PushToTalk };
        Assert.False(gate.ShouldTransmit(0, false, false));
        Assert.True(gate.ShouldTransmit(-100, true, false));
        Assert.True(gate.ShouldTransmit(-100, false, true));
    }

    [Fact]
    public void Level_meter_math()
    {
        Assert.Equal(-100f, VoiceGate.LevelDb(new float[Frame]));
        var full = Enumerable.Repeat(1f, Frame).ToArray();
        Assert.Equal(0f, VoiceGate.LevelDb(full), 2);
    }

    [Fact]
    public void Resampler_produces_expected_length()
    {
        var rs = new LinearResampler(44_100, 48_000);
        int total = 0;
        var input = new float[441];
        for (int i = 0; i < 100; i++) rs.Process(input, s => total += s.Length);
        Assert.InRange(total, 48_000 - 5, 48_000 + 5);
    }
}
