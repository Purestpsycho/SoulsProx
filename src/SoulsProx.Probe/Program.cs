using System.Globalization;
using SoulsProx;
using SoulsProx.Games;
using SoulsProx.Settings;

// SoulsProx Probe: prints what SoulsProx can read from a running game.
// Usage: SoulsProx.Probe [--seconds N] [--interval MS] [--selftest]
//   --seconds N    stop after N seconds (default: run until Ctrl+C)
//   --interval MS  time between printouts (default 1000)
//   --selftest     start the whole voice engine without the window (mic, speakers, STUN) and print its status

int seconds = 0, interval = 1000;
bool selfTest = args.Contains("--selftest");
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--seconds") seconds = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
    if (args[i] == "--interval") interval = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
}

var stopAt = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

if (selfTest)
{
    Log.Init(AppSettings.Directory, "log-selftest.txt");
    var path = AppSettings.FilePath("selftest");
    using var engine = new ProxChatEngine(AppSettings.Load(path), path, portOverride: 0);
    await engine.StartAsync();
    Console.WriteLine($"Mic:    {engine.MicStatus}");
    Console.WriteLine($"Output: {engine.OutputStatus}");
    Console.WriteLine($"UDP port {engine.Link.LocalPort}, LAN {engine.Link.LanEndpoint}, public {engine.Link.PublicEndpoint?.ToString() ?? "unknown"} {engine.Link.NatNote}");
    Console.WriteLine($"My code: {engine.Link.MyCode}");
    while (!cancel.IsCancellationRequested && DateTime.UtcNow < stopAt)
    {
        Thread.Sleep(interval);
        Console.WriteLine($"  mic level {engine.MicLevelDb,6:F1} dB  transmitting {engine.Transmitting}  game: {engine.Game.Status}");
    }
    return;
}

using var monitor = new GameMonitor();
monitor.Start();

while (!cancel.IsCancellationRequested && DateTime.UtcNow < stopAt)
{
    Thread.Sleep(interval);
    Console.WriteLine($"--- {DateTime.Now:HH:mm:ss.fff}  {monitor.Status}");
    var snap = monitor.Latest;
    if (snap is not null)
    {
        if (!snap.InWorld)
        {
            Console.WriteLine("  not in world (menu / loading)");
        }
        else
        {
            var p = snap.LocalPosition;
            var f = snap.ListenerForward;
            Console.WriteLine($"  me: '{snap.LocalName}' pos ({p.X:F2}, {p.Y:F2}, {p.Z:F2}) forward {(f is { } v ? $"({v.X:F2}, {v.Y:F2})" : "?")} frame {snap.Frame}");
            foreach (var r in snap.Remotes)
            {
                float d = System.Numerics.Vector3.Distance(p, r.Position);
                Console.WriteLine($"  other: '{r.Name}' pos ({r.Position.X:F2}, {r.Position.Y:F2}, {r.Position.Z:F2}) distance {d:F1} m");
            }
        }
    }
    foreach (var (key, value) in monitor.Diagnostics())
        Console.WriteLine($"    {key}: {value}");
}
