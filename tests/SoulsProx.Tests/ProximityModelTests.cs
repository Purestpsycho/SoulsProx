using System.Numerics;
using SoulsProx.Games;
using SoulsProx.Proximity;

namespace SoulsProx.Tests;

public class ProximityModelTests
{
    private static readonly ProximitySettings Settings = new() { NearDistance = 5, FarDistance = 35, MaxPan = 0.8f };

    private static GameSnapshot Me(Vector3 pos, Vector2? forward = null, params RemotePlayer[] remotes) => new()
    {
        Game = GameId.DarkSouls3,
        InWorld = true,
        LocalName = "Me",
        LocalPosition = pos,
        ListenerForward = forward ?? new Vector2(0, 1),
        Remotes = remotes,
    };

    private static PeerReport Friend(Vector3 pos, string name = "Friend", long frame = 0) =>
        new(GameId.DarkSouls3, true, name, pos, frame);

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(5f, 1f)]
    [InlineData(20f, 0.25f)]
    [InlineData(35f, 0f)]
    [InlineData(100f, 0f)]
    public void Distance_gain_curve(float distance, float expected)
    {
        Assert.Equal(expected, ProximityModel.DistanceGain(distance, 5, 35), 3);
    }

    [Fact]
    public void Gain_decreases_monotonically()
    {
        float last = 2f;
        for (float d = 0; d <= 40; d += 0.5f)
        {
            float g = ProximityModel.DistanceGain(d, 5, 35);
            Assert.True(g <= last);
            last = g;
        }
    }

    [Fact]
    public void Source_to_the_right_pans_right()
    {
        // Facing +Z; with the left-handed convention +X is to the right.
        float pan = ProximityModel.Pan(Vector3.Zero, new Vector2(0, 1), new Vector3(10, 0, 0), 0.8f, swap: false);
        Assert.Equal(0.8f, pan, 3);
        float left = ProximityModel.Pan(Vector3.Zero, new Vector2(0, 1), new Vector3(-10, 0, 0), 0.8f, swap: false);
        Assert.Equal(-0.8f, left, 3);
    }

    [Fact]
    public void Source_ahead_or_behind_is_centred()
    {
        Assert.Equal(0f, ProximityModel.Pan(Vector3.Zero, new Vector2(0, 1), new Vector3(0, 0, 10), 0.8f, false), 3);
        Assert.Equal(0f, ProximityModel.Pan(Vector3.Zero, new Vector2(0, 1), new Vector3(0, 0, -10), 0.8f, false), 3);
    }

    [Fact]
    public void Swap_flips_pan()
    {
        float pan = ProximityModel.Pan(Vector3.Zero, new Vector2(0, 1), new Vector3(10, 0, 0), 0.8f, swap: true);
        Assert.Equal(-0.8f, pan, 3);
    }

    [Fact]
    public void Pan_fades_when_very_close()
    {
        float pan = ProximityModel.Pan(Vector3.Zero, new Vector2(0, 1), new Vector3(0.3f, 0, 0), 0.8f, false);
        Assert.InRange(pan, 0f, 0.2f);
    }

    [Fact]
    public void Behind_is_slightly_quieter()
    {
        float front = ProximityModel.FrontBackGain(Vector3.Zero, new Vector2(0, 1), new Vector3(0, 0, 10));
        float behind = ProximityModel.FrontBackGain(Vector3.Zero, new Vector2(0, 1), new Vector3(0, 0, -10));
        Assert.Equal(1f, front, 3);
        Assert.Equal(0.8f, behind, 3);
    }

    [Fact]
    public void Uses_friend_character_found_in_our_world_by_name()
    {
        var me = Me(Vector3.Zero, null, new RemotePlayer("Someone", new Vector3(100, 0, 0)), new RemotePlayer("Friend", new Vector3(0, 0, 3)));
        // The friend's own report says somewhere else; the in-world character wins.
        var r = ProximityModel.Compute(me, Friend(new Vector3(500, 0, 500)), Settings, onlyPeer: false);
        Assert.Equal(SpatialSource.InWorld, r.Source);
        Assert.Equal(3f, r.Distance!.Value, 3);
        Assert.Equal(1f, r.Gain, 3);
    }

    [Fact]
    public void Single_remote_player_is_assumed_to_be_the_friend()
    {
        var me = Me(Vector3.Zero, null, new RemotePlayer("Renamed", new Vector3(0, 0, 20)));
        var r = ProximityModel.Compute(me, Friend(new Vector3(500, 0, 500)), Settings, onlyPeer: true);
        Assert.Equal(SpatialSource.InWorld, r.Source);
        Assert.Equal(20f, r.Distance!.Value, 3);
    }

    [Fact]
    public void Falls_back_to_reported_position_in_same_frame()
    {
        var r = ProximityModel.Compute(Me(Vector3.Zero), Friend(new Vector3(0, 0, 20)), Settings);
        Assert.Equal(SpatialSource.Reported, r.Source);
        Assert.Equal(0.25f, r.Gain, 3);
    }

    [Fact]
    public void Different_frame_and_not_in_world_is_out_of_range()
    {
        var r = ProximityModel.Compute(Me(Vector3.Zero), Friend(new Vector3(0, 0, 1), frame: 42), Settings);
        Assert.Equal(SpatialSource.OutOfRange, r.Source);
        Assert.Equal(0f, r.Gain);
    }

    [Fact]
    public void Out_of_range_volume_is_a_floor()
    {
        var s = Settings with { OutOfRangeVolume = 0.1f };
        var r = ProximityModel.Compute(Me(Vector3.Zero), Friend(new Vector3(0, 0, 1000)), s);
        Assert.Equal(0.1f, r.Gain, 3);
    }

    [Fact]
    public void Outside_world_is_normal_chat_by_default()
    {
        var notInWorld = GameSnapshot.NotInWorld(GameId.DarkSouls3);
        var r = ProximityModel.Compute(notInWorld, Friend(Vector3.Zero), Settings);
        Assert.Equal(SpatialSource.OutsideWorld, r.Source);
        Assert.Equal(1f, r.Gain);

        var noGame = ProximityModel.Compute(null, null, Settings);
        Assert.Equal(1f, noGame.Gain);

        var muted = ProximityModel.Compute(null, null, Settings with { FullVolumeOutsideWorld = false });
        Assert.Equal(0f, muted.Gain);
    }

    [Fact]
    public void Hold_keeps_last_in_world_result_during_loading_screens()
    {
        var hold = new SpatialHold(TimeSpan.FromSeconds(10));
        var t0 = DateTime.UtcNow;
        var inWorld = new SpatialResult(0.3f, 0.5f, 15f, SpatialSource.InWorld);
        var outside = new SpatialResult(1f, 0f, null, SpatialSource.OutsideWorld);

        Assert.Equal(inWorld, hold.Apply(inWorld, t0));
        Assert.Equal(inWorld, hold.Apply(outside, t0.AddSeconds(5)));
        Assert.Equal(outside, hold.Apply(outside, t0.AddSeconds(11)));
    }
}
