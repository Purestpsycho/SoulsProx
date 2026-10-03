using SoulsProx.Audio;

namespace SoulsProx.Tests;

public class EchoCancellerTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Chunk = EchoCanceller.ChunkSamples;
    private const int Rate = VoiceFormat.SampleRate;

    /// <summary>Speech-like test signal: noise in 200 ms bursts with pauses, like syllables.</summary>
    private static float[] SpeechLike(int seconds, int seed, float amplitude = 0.2f)
    {
        var rng = new Random(seed);
        var s = new float[seconds * Rate];
        float lp = 0;
        for (int i = 0; i < s.Length; i++)
        {
            bool on = (i / (Rate / 5)) % 3 != 2; // two 200 ms "syllables", then a 200 ms pause
            float white = (float)(rng.NextDouble() * 2 - 1);
            lp += (white - lp) * 0.3f; // tilt the spectrum down a bit
            s[i] = on ? lp * amplitude * 3 : 0;
        }
        return s;
    }

    private static double Energy(ReadOnlySpan<float> s)
    {
        double e = 0;
        foreach (float x in s) e += x * x;
        return e;
    }

    private static double Db(double ratio) => 10 * Math.Log10(Math.Max(ratio, 1e-12));

    /// <summary>
    /// Plays <paramref name="farEnd"/>, lets an attenuated, delayed copy leak into the mic (plus optional
    /// near-end speech), and returns what the canceller sends on.
    /// </summary>
    private static float[] Run(EchoCanceller? ec, float[] farEnd, float[] nearEnd, int echoDelayMs, float echoGain)
    {
        int delay = echoDelayMs * Rate / 1000;
        var output = new float[nearEnd.Length];
        var chunk = new float[Chunk];
        float lp = 0;
        for (int start = 0; start + Chunk <= nearEnd.Length; start += Chunk)
        {
            ec?.ProcessRender(farEnd.AsSpan(start, Chunk));
            for (int i = 0; i < Chunk; i++)
            {
                int t = start + i - delay;
                float echo = t >= 0 ? farEnd[t] * echoGain : 0;
                lp += (echo - lp) * 0.5f; // a little acoustic low-pass
                chunk[i] = lp + nearEnd[start + i];
            }
            ec?.ProcessCapture(chunk);
            chunk.CopyTo(output, start);
        }
        return output;
    }

    [Fact]
    public void Removes_echo_of_what_we_played()
    {
        using var ec = EchoCanceller.TryCreate(echoCancellation: true, noiseSuppression: false, out var error);
        Assert.True(ec is not null, $"native library failed to load: {error}");

        var far = SpeechLike(8, seed: 1);
        var silentNear = new float[far.Length];
        var outSignal = Run(ec!, far, silentNear, echoDelayMs: 50, echoGain: 0.3f);

        // Compare the last 3 seconds (after the canceller has converged) with what leaked into the mic.
        int from = far.Length - 3 * Rate;
        var leaked = Run(null, far, silentNear, 50, 0.3f);
        double reduction = Db(Energy(leaked.AsSpan(from)) / Energy(outSignal.AsSpan(from)));
        output.WriteLine($"Echo reduced by {reduction:F1} dB");
        Assert.True(reduction > 20, $"only {reduction:F1} dB of echo removed");
    }

    [Fact]
    public void Keeps_your_own_voice_when_nothing_is_playing()
    {
        using var ec = EchoCanceller.TryCreate(echoCancellation: true, noiseSuppression: false, out var error);
        Assert.True(ec is not null, error);

        var near = SpeechLike(4, seed: 2, amplitude: 0.1f);
        var silentFar = new float[near.Length];
        var outSignal = Run(ec!, silentFar, near, 50, 0.3f);

        int from = Rate; // skip the first second
        double change = Db(Energy(outSignal.AsSpan(from)) / Energy(near.AsSpan(from)));
        output.WriteLine($"Own voice changed by {change:F1} dB with nothing playing");
        Assert.True(change > -3, $"near-end speech was attenuated by {-change:F1} dB");
    }

    [Fact]
    public void Keeps_your_voice_while_removing_echo()
    {
        using var ec = EchoCanceller.TryCreate(echoCancellation: true, noiseSuppression: false, out var error);
        Assert.True(ec is not null, error);

        // Friend talks for 6 s (echo leaks in); for the last 2 s you talk while they're silent.
        var far = SpeechLike(8, seed: 3);
        Array.Clear(far, 6 * Rate, 2 * Rate);
        var near = new float[far.Length];
        var mine = SpeechLike(2, seed: 4, amplitude: 0.1f);
        mine.CopyTo(near, 6 * Rate);

        var outSignal = Run(ec!, far, near, 50, 0.3f);
        int from = 6 * Rate + Rate / 2;
        double change = Db(Energy(outSignal.AsSpan(from)) / Energy(near.AsSpan(from)));
        output.WriteLine($"Own voice changed by {change:F1} dB right after the friend stopped");
        Assert.True(change > -6, $"your voice was attenuated by {-change:F1} dB after the friend stopped talking");
    }
}
