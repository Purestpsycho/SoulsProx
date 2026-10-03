using System.Numerics;

namespace SoulsProx.Games;

public enum GameId : byte
{
    None = 0,
    DarkSoulsRemastered = 1,
    DarkSouls2 = 2,
    DarkSouls3 = 3,
    EldenRing = 4,
}

public static class GameIdExtensions
{
    public static string DisplayName(this GameId game) => game switch
    {
        GameId.DarkSoulsRemastered => "Dark Souls Remastered",
        GameId.DarkSouls2 => "Dark Souls II: SotFS",
        GameId.DarkSouls3 => "Dark Souls III",
        GameId.EldenRing => "Elden Ring",
        _ => "No game",
    };
}

/// <summary>Another player character that exists in our own game world.</summary>
public sealed record RemotePlayer(string Name, Vector3 Position);

/// <summary>
/// What the game looks like right now from the local player's point of view.
/// Positions are in the game's world units (metres). Y is up in all supported games.
/// </summary>
public sealed record GameSnapshot
{
    public required GameId Game { get; init; }

    /// <summary>True when the local character is loaded into the world (not in a menu or loading screen).</summary>
    public required bool InWorld { get; init; }

    public string LocalName { get; init; } = "";
    public Vector3 LocalPosition { get; init; }

    /// <summary>
    /// Horizontal (X/Z) unit vector for the direction the listener is facing: the camera if we can read it,
    /// otherwise the character's facing. Null if neither is known.
    /// </summary>
    public Vector2? ListenerForward { get; init; }

    /// <summary>
    /// Identifier for the coordinate frame <see cref="LocalPosition"/> is expressed in (e.g. a map block id).
    /// Two positions are only comparable when their frames match. 0 = a single shared world frame.
    /// </summary>
    public long Frame { get; init; }

    public IReadOnlyList<RemotePlayer> Remotes { get; init; } = [];

    public static GameSnapshot NotInWorld(GameId game) => new() { Game = game, InWorld = false };
}
