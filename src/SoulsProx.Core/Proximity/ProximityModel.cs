using System.Numerics;
using SoulsProx.Games;

namespace SoulsProx.Proximity;

public sealed record ProximitySettings
{
    /// <summary>Within this distance (metres) the friend is at full volume.</summary>
    public float NearDistance { get; init; } = 5f;

    /// <summary>At and beyond this distance the friend is at <see cref="OutOfRangeVolume"/>.</summary>
    public float FarDistance { get; init; } = 35f;

    /// <summary>Volume floor when far away or not findable (0 = silent).</summary>
    public float OutOfRangeVolume { get; init; }

    /// <summary>How far toward one ear a voice can be panned (0 = mono, 1 = fully one-sided).</summary>
    public float MaxPan { get; init; } = 0.8f;

    public bool SwapLeftRight { get; init; }

    /// <summary>When either player is in a menu / loading screen / not running the game, talk normally.</summary>
    public bool FullVolumeOutsideWorld { get; init; } = true;
}

/// <summary>What the friend tells us about themselves over the network.</summary>
public sealed record PeerReport(GameId Game, bool InWorld, string Name, Vector3 Position, long Frame);

public enum SpatialSource
{
    /// <summary>One of us isn't in the game world; plain voice chat.</summary>
    OutsideWorld,
    /// <summary>Found the friend's character in our own game world.</summary>
    InWorld,
    /// <summary>Using the position the friend reported (same coordinate frame).</summary>
    Reported,
    /// <summary>Can't place the friend near us.</summary>
    OutOfRange,
    /// <summary>Friend is holding the radio key.</summary>
    Radio,
    /// <summary>Test beacon at a fixed spot.</summary>
    Beacon,
}

public readonly record struct SpatialResult(float Gain, float Pan, float? Distance, SpatialSource Source);

public static class ProximityModel
{
    /// <summary>Distance under which panning fades toward centre, so it doesn't flip when standing on top of each other.</summary>
    private const float PanFadeDistance = 1.5f;

    /// <summary>A voice directly behind you is this much quieter, as a front/back cue.</summary>
    private const float BehindAttenuation = 0.2f;

    /// <summary>
    /// Volume for a distance: 1 inside <paramref name="near"/>, 0 at <paramref name="far"/>,
    /// with a quadratic fall-off in between (roughly -12 dB halfway).
    /// </summary>
    public static float DistanceGain(float distance, float near, float far)
    {
        if (distance <= near) return 1f;
        if (distance >= far || far <= near) return 0f;
        float t = (distance - near) / (far - near);
        return (1f - t) * (1f - t);
    }

    /// <summary>
    /// Stereo pan in [-1, 1] (negative = left) for a source as heard by a listener at
    /// <paramref name="listener"/> facing <paramref name="forward"/> (a horizontal unit vector in X/Z).
    /// </summary>
    public static float Pan(Vector3 listener, Vector2? forward, Vector3 source, float maxPan, bool swap)
    {
        if (forward is not { } f) return 0f;
        var dir = new Vector2(source.X - listener.X, source.Z - listener.Z);
        float horizontal = dir.Length();
        if (horizontal < 1e-3f) return 0f;
        dir /= horizontal;

        // Right-hand side of the listener in the X/Z plane (Y up, left-handed like Direct3D).
        var right = new Vector2(f.Y, -f.X);
        float pan = Vector2.Dot(dir, right) * maxPan * Math.Clamp(horizontal / PanFadeDistance, 0f, 1f);
        return swap ? -pan : pan;
    }

    /// <summary>Extra attenuation in [1 - BehindAttenuation, 1] depending on whether the source is in front.</summary>
    public static float FrontBackGain(Vector3 listener, Vector2? forward, Vector3 source)
    {
        if (forward is not { } f) return 1f;
        var dir = new Vector2(source.X - listener.X, source.Z - listener.Z);
        float len = dir.Length();
        if (len < PanFadeDistance) return 1f;
        float facing = Vector2.Dot(dir / len, f); // 1 in front, -1 behind
        return facing >= 0 ? 1f : 1f + BehindAttenuation * facing;
    }

    /// <summary>Volume and pan for a voice coming from a known world position.</summary>
    public static SpatialResult ForPosition(GameSnapshot local, Vector3 source, ProximitySettings s, SpatialSource kind)
    {
        float distance = Vector3.Distance(local.LocalPosition, source);
        float gain = DistanceGain(distance, s.NearDistance, s.FarDistance) * FrontBackGain(local.LocalPosition, local.ListenerForward, source);
        gain = MathF.Max(gain, s.OutOfRangeVolume);
        float pan = Pan(local.LocalPosition, local.ListenerForward, source, s.MaxPan, s.SwapLeftRight);
        return new SpatialResult(gain, pan, distance, kind);
    }

    /// <summary>
    /// Works out how loud the friend should be and where they should sound from.
    /// </summary>
    /// <param name="local">Our game state, or null if no game is running.</param>
    /// <param name="peer">The friend's last report, or null if we haven't heard one.</param>
    /// <param name="onlyPeer">True when the friend is the only person we're connected to.</param>
    public static SpatialResult Compute(GameSnapshot? local, PeerReport? peer, ProximitySettings s, bool onlyPeer = true)
    {
        if (local is not { InWorld: true } || peer is not { InWorld: true } || peer.Game != local.Game)
        {
            return s.FullVolumeOutsideWorld
                ? new SpatialResult(1f, 0f, null, SpatialSource.OutsideWorld)
                : new SpatialResult(s.OutOfRangeVolume, 0f, null, SpatialSource.OutOfRange);
        }

        var match = FindByName(local.Remotes, peer.Name);
        if (match is null && onlyPeer && local.Remotes.Count == 1) match = local.Remotes[0];
        if (match is not null) return ForPosition(local, match.Position, s, SpatialSource.InWorld);

        if (peer.Frame == local.Frame) return ForPosition(local, peer.Position, s, SpatialSource.Reported);

        return new SpatialResult(s.OutOfRangeVolume, 0f, null, SpatialSource.OutOfRange);
    }

    internal static RemotePlayer? FindByName(IReadOnlyList<RemotePlayer> remotes, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        return remotes.FirstOrDefault(r => string.Equals(r.Name.Trim(), name, StringComparison.Ordinal))
            ?? remotes.FirstOrDefault(r => string.Equals(r.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Keeps using the last in-world result for a short time when someone hits a loading screen,
/// so the friend doesn't suddenly jump to full volume while warping.
/// </summary>
public sealed class SpatialHold(TimeSpan holdFor)
{
    private SpatialResult? _lastInWorld;
    private DateTime _lastInWorldAt;

    public SpatialResult Apply(SpatialResult current, DateTime now)
    {
        if (current.Source is SpatialSource.InWorld or SpatialSource.Reported or SpatialSource.OutOfRange)
        {
            _lastInWorld = current;
            _lastInWorldAt = now;
            return current;
        }
        if (current.Source == SpatialSource.OutsideWorld && _lastInWorld is { } last && now - _lastInWorldAt < holdFor)
            return last;
        return current;
    }
}
