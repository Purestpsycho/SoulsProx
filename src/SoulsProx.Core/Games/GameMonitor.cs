using System.Diagnostics;
using SoulsProx.Memory;

namespace SoulsProx.Games;

/// <summary>
/// Background loop that finds a supported game, attaches to it read-only and keeps
/// <see cref="Latest"/> up to date. Survives the game being closed and relaunched.
/// </summary>
public sealed class GameMonitor : IDisposable
{
    /// <summary>All supported games. Each entry creates a fresh reader per game process.</summary>
    public static readonly IReadOnlyList<Func<IGameReader>> ReaderFactories =
    [
        () => new DarkSouls3Reader(),
    ];

    private readonly TimeSpan _readInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    private volatile GameSnapshot? _latest;
    private volatile string _status = "Looking for a game…";
    private volatile ProcessMemory? _memory;
    private volatile IGameReader? _reader;

    public GameMonitor(TimeSpan? readInterval = null)
    {
        _readInterval = readInterval ?? TimeSpan.FromMilliseconds(50);
        _thread = new Thread(Run) { IsBackground = true, Name = "SoulsProx game monitor" };
    }

    /// <summary>Most recent snapshot, or null when no game is attached.</summary>
    public GameSnapshot? Latest => _latest;

    public string Status => _status;

    public GameId AttachedGame => _reader?.Game ?? GameId.None;

    public void Start() => _thread.Start();

    /// <summary>Raw values from the attached reader, for diagnostics. Safe to call from any thread.</summary>
    public IReadOnlyList<(string Key, string Value)> Diagnostics()
    {
        var memory = _memory;
        var reader = _reader;
        if (memory is null || reader is null) return [];
        try { return reader.Diagnostics(memory).ToList(); }
        catch (Exception ex) { return [("error", ex.Message)]; }
    }

    private void Run()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_memory is null) TryAttach();
                else if (!_memory.IsAlive) Detach("Game closed. Looking for a game…");
                else if (_reader is null) TryInitialize();
                else _latest = _reader.Read(_memory);
            }
            catch (Exception ex)
            {
                _status = $"Error: {ex.Message}";
                _latest = null;
            }

            // Signature scans copy the whole exe image, so back off if the game never matches.
            var wait = _reader is not null ? _readInterval
                : _memory is not null && _initAttempts > 5 ? TimeSpan.FromSeconds(10)
                : TimeSpan.FromSeconds(2);
            token.WaitHandle.WaitOne(wait);
        }
        Detach("Stopped");
    }

    private Func<IGameReader>? _pendingFactory;
    private int _initAttempts;

    private void TryAttach()
    {
        foreach (var factory in ReaderFactories)
        {
            var probe = factory();
            var processes = Process.GetProcessesByName(probe.ProcessName);
            var process = processes.FirstOrDefault();
            foreach (var p in processes.Skip(1)) p.Dispose();
            if (process is null) continue;

            var memory = ProcessMemory.TryOpen(process, out var error);
            if (memory is null)
            {
                _status = $"Found {probe.Game.DisplayName()} but can't read it: {error}";
                process.Dispose();
                return;
            }
            _memory = memory;
            _pendingFactory = factory;
            _initAttempts = 0;
            _status = $"Found {probe.Game.DisplayName()}, locating game data…";
            return;
        }
        _status = "Looking for a game…";
    }

    private void TryInitialize()
    {
        _initAttempts++;
        var reader = _pendingFactory!();
        if (reader.TryInitialize(_memory!, out var status))
        {
            _reader = reader;
            _status = $"{reader.Game.DisplayName()}: {status}";
        }
        else
        {
            _status = $"{reader.Game.DisplayName()}: {status}";
        }
    }

    private void Detach(string status)
    {
        _reader = null;
        _latest = null;
        var memory = _memory;
        _memory = null;
        if (memory is not null)
        {
            memory.Process.Dispose();
            memory.Dispose();
        }
        _status = status;
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
