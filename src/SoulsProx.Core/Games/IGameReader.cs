using SoulsProx.Memory;

namespace SoulsProx.Games;

public interface IGameReader
{
    GameId Game { get; }

    /// <summary>Process name without ".exe".</summary>
    string ProcessName { get; }

    /// <summary>
    /// Locates the game's globals. Called repeatedly (every couple of seconds) until it returns true,
    /// because right after launch the code may not be unpacked yet.
    /// </summary>
    bool TryInitialize(ProcessMemory memory, out string status);

    /// <summary>Reads the current state. Only called after a successful <see cref="TryInitialize"/>.</summary>
    GameSnapshot Read(ProcessMemory memory);

    /// <summary>Extra raw values for the Probe tool / debug panel.</summary>
    IEnumerable<(string Key, string Value)> Diagnostics(ProcessMemory memory);
}
