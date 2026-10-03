using System.Numerics;
using SoulsProx.Memory;

namespace SoulsProx.Games;

/// <summary>
/// Dark Souls III (1.15.x).
/// Signature from SoulMemory (FrankvdStam/SoulSplitter); structure offsets from
/// DS3RuntimeScripting (AmySouls) and the community Cheat Engine tables.
/// </summary>
public sealed class DarkSouls3Reader : IGameReader
{
    private static readonly RipSignature[] WorldChrManSignatures =
    [
        new("WorldChrMan", "48 8b 0d ? ? ? ? 45 33 c0 48 8d 55 e7 e8 ? ? ? ? 0f 2f 73 70 72 0d f3", 3, 7),
        new("WorldChrMan (alt)", "48 8b 1d ? ? ? 04 48 8b f9 48 85 db ? ? 8b 11 85 d2 ? ? 8d", 3, 7),
    ];

    // Known static address in 1.15 when the exe loads at its preferred base.
    private const long PreferredImageBase = 0x140000000;
    private const long WorldChrManStatic115 = 0x144768E78;

    // WorldChrMan
    private const int MainPlayer = 0x80;
    private const int PlayerSlots = 0x40;      // -> array of slot entries
    private const int PlayerSlotSize = 0x38;   // entry + 0x0 = PlayerIns*
    private const int MaxPlayerSlots = 6;      // Seamless Co-op: up to 6 players
    private const int CameraHolder = 0x31A0;   // -> +0x30 = camera direction (3 floats)
    private const int CameraVector = 0x30;

    // ChrIns / PlayerIns
    private const int SlotBackPointer = 0x18;  // ChrIns -> its slot entry
    private const int SlotPhysics = 0x28;      // slot entry -> SprjChrPhysicsModule
    private const int PhysicsAngle = 0x74;     // float, radians
    private const int PhysicsPosition = 0x80;  // 3 floats
    private const int PlayerGameData = 0x1FA0;
    private const int GameDataName = 0x88;     // wchar[16]
    private const int MaxNameChars = 16;

    private nint _worldChrManStatic;
    private string _signatureUsed = "";

    public GameId Game => GameId.DarkSouls3;
    public string ProcessName => "DarkSoulsIII";

    public bool TryInitialize(ProcessMemory memory, out string status)
    {
        var image = memory.ReadModuleImage();
        foreach (var sig in WorldChrManSignatures)
        {
            nint addr = sig.Resolve(image, memory.ModuleBase);
            if (addr != 0 && memory.IsInModule(addr))
            {
                _worldChrManStatic = addr;
                _signatureUsed = sig.Name;
                status = $"{sig.Name} at 0x{addr:X}";
                return true;
            }
        }

        if (memory.ModuleBase == PreferredImageBase)
        {
            _worldChrManStatic = unchecked((nint)WorldChrManStatic115);
            _signatureUsed = "1.15 static address";
            status = $"Signatures not found; using 1.15 static address 0x{WorldChrManStatic115:X}";
            return true;
        }

        status = "WorldChrMan signature not found (game still starting, or unsupported version)";
        return false;
    }

    public GameSnapshot Read(ProcessMemory m)
    {
        nint wcm = m.ReadPtr(_worldChrManStatic);
        nint local = wcm == 0 ? 0 : m.ReadPtr(wcm + MainPlayer);
        if (local == 0 || !TryReadPosition(m, local, out var pos))
            return GameSnapshot.NotInWorld(Game);

        nint localVtable = m.ReadPtr(local);
        var remotes = new List<RemotePlayer>();
        nint slots = m.ReadPtr(wcm + PlayerSlots);
        for (int i = 0; slots != 0 && i < MaxPlayerSlots; i++)
        {
            nint ins = m.ReadPtr(slots + i * PlayerSlotSize);
            if (ins == 0 || ins == local || m.ReadPtr(ins) != localVtable) continue;
            if (!TryReadPosition(m, ins, out var rpos)) continue;
            remotes.Add(new RemotePlayer(ReadName(m, ins), rpos));
        }

        return new GameSnapshot
        {
            Game = Game,
            InWorld = true,
            LocalName = ReadName(m, local),
            LocalPosition = pos,
            ListenerForward = ReadCameraForward(m, wcm) ?? ReadFacing(m, local),
            Remotes = remotes,
        };
    }

    private static nint Physics(ProcessMemory m, nint chr) => m.Chain(chr + SlotBackPointer, SlotPhysics, 0);

    private static bool TryReadPosition(ProcessMemory m, nint chr, out Vector3 pos)
    {
        pos = default;
        nint physics = Physics(m, chr);
        return physics != 0 && m.TryRead(physics + PhysicsPosition, out pos) && IsPlausible(pos);
    }

    private static string ReadName(ProcessMemory m, nint chr)
    {
        nint pgd = m.ReadPtr(chr + PlayerGameData);
        return pgd == 0 ? "" : (m.ReadUtf16(pgd + GameDataName, MaxNameChars) ?? "");
    }

    private static Vector2? ReadCameraForward(ProcessMemory m, nint wcm)
    {
        nint holder = m.ReadPtr(wcm + CameraHolder);
        if (holder == 0 || !m.TryRead(holder + CameraVector, out Vector3 v)) return null;
        // Only trust it if it looks like a unit direction vector.
        float len = v.Length();
        if (!float.IsFinite(len) || len < 0.9f || len > 1.1f) return null;
        return Horizontal(v);
    }

    private static Vector2? ReadFacing(ProcessMemory m, nint chr)
    {
        nint physics = Physics(m, chr);
        if (physics == 0 || !m.TryRead(physics + PhysicsAngle, out float angle) || !float.IsFinite(angle)) return null;
        return new Vector2(MathF.Sin(angle), MathF.Cos(angle));
    }

    internal static Vector2? Horizontal(Vector3 v)
    {
        var h = new Vector2(v.X, v.Z);
        float len = h.Length();
        return len < 1e-3f ? null : h / len;
    }

    internal static bool IsPlausible(Vector3 p) =>
        float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) &&
        MathF.Abs(p.X) < 100_000 && MathF.Abs(p.Y) < 100_000 && MathF.Abs(p.Z) < 100_000 &&
        p != Vector3.Zero;

    public IEnumerable<(string Key, string Value)> Diagnostics(ProcessMemory m)
    {
        yield return ("Signature", _signatureUsed);
        yield return ("WorldChrMan static", $"0x{_worldChrManStatic:X}");
        nint wcm = m.ReadPtr(_worldChrManStatic);
        yield return ("WorldChrMan", $"0x{wcm:X}");
        if (wcm == 0) yield break;

        nint local = m.ReadPtr(wcm + MainPlayer);
        yield return ("PlayerIns (local)", $"0x{local:X}  vtable 0x{m.ReadPtr(local):X}");
        if (local != 0)
        {
            nint physics = Physics(m, local);
            yield return ("Physics module", $"0x{physics:X}");
            if (physics != 0)
            {
                yield return ("Position", Fmt(m.Read<Vector3>(physics + PhysicsPosition)));
                yield return ("Facing angle (rad)", m.Read<float>(physics + PhysicsAngle).ToString("F3"));
            }
            yield return ("Name", $"'{ReadName(m, local)}'");
        }

        nint holder = m.ReadPtr(wcm + CameraHolder);
        if (holder != 0)
        {
            var cam = m.Read<Vector3>(holder + CameraVector);
            yield return ("Camera vector", $"{Fmt(cam)}  |len| {cam.Length():F3}");
        }

        nint slots = m.ReadPtr(wcm + PlayerSlots);
        yield return ("Player slots", $"0x{slots:X}");
        for (int i = 0; slots != 0 && i < MaxPlayerSlots; i++)
        {
            nint ins = m.ReadPtr(slots + i * PlayerSlotSize);
            if (ins == 0) { yield return ($"  slot {i}", "empty"); continue; }
            string who = ins == local ? " (local)" : "";
            string pos = TryReadPosition(m, ins, out var p) ? Fmt(p) : "?";
            yield return ($"  slot {i}", $"0x{ins:X}{who} vtable 0x{m.ReadPtr(ins):X} '{ReadName(m, ins)}' pos {pos}");
        }
    }

    private static string Fmt(Vector3 v) => $"({v.X:F2}, {v.Y:F2}, {v.Z:F2})";
}
