using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SoulsProx.Memory;

/// <summary>
/// Read-only access to another process's memory. SoulsProx never writes to the game.
/// </summary>
public sealed class ProcessMemory : IDisposable
{
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint STILL_ACTIVE = 259;

    private nint _handle;

    public Process Process { get; }
    public nint ModuleBase { get; }
    public int ModuleSize { get; }

    private ProcessMemory(Process process, nint handle, nint moduleBase, int moduleSize)
    {
        Process = process;
        _handle = handle;
        ModuleBase = moduleBase;
        ModuleSize = moduleSize;
    }

    /// <summary>Opens the process for reading. Returns null if access is denied or the process is gone.</summary>
    public static ProcessMemory? TryOpen(Process process, out string? error)
    {
        error = null;
        nint handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
        if (handle == 0)
        {
            error = $"OpenProcess failed (Win32 error {Marshal.GetLastWin32Error()})";
            return null;
        }

        try
        {
            var module = process.MainModule;
            if (module is null)
            {
                CloseHandle(handle);
                error = "Main module not available yet";
                return null;
            }
            return new ProcessMemory(process, handle, module.BaseAddress, module.ModuleMemorySize);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            CloseHandle(handle);
            error = $"Could not read main module: {ex.Message}";
            return null;
        }
    }

    public bool IsAlive
    {
        get
        {
            if (_handle == 0) return false;
            return GetExitCodeProcess(_handle, out uint code) && code == STILL_ACTIVE;
        }
    }

    public bool TryReadBytes(nint address, Span<byte> buffer)
    {
        if (address == 0 || _handle == 0) return false;
        unsafe
        {
            fixed (byte* p = buffer)
            {
                return ReadProcessMemory(_handle, address, p, (nint)buffer.Length, out nint read) && read == buffer.Length;
            }
        }
    }

    public bool TryRead<T>(nint address, out T value) where T : unmanaged
    {
        value = default;
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<T>()];
        if (!TryReadBytes(address, bytes)) return false;
        value = MemoryMarshal.Read<T>(bytes);
        return true;
    }

    public T Read<T>(nint address) where T : unmanaged => TryRead<T>(address, out var v) ? v : default;

    public nint ReadPtr(nint address) => TryRead<long>(address, out var v) ? (nint)v : 0;

    /// <summary>
    /// Follows a pointer chain: start at <paramref name="address"/>, then for each offset
    /// dereference the current pointer and add the offset. Returns 0 if any link is null.
    /// Example: Chain(a, 0x40, 0x28) == [[a] + 0x40] + 0x28.
    /// </summary>
    public nint Chain(nint address, params ReadOnlySpan<int> offsets)
    {
        nint p = address;
        foreach (int off in offsets)
        {
            p = ReadPtr(p);
            if (p == 0) return 0;
            p += off;
        }
        return p;
    }

    /// <summary>Reads a null-terminated UTF-16 string of at most <paramref name="maxChars"/> characters.</summary>
    public string? ReadUtf16(nint address, int maxChars)
    {
        Span<byte> bytes = stackalloc byte[maxChars * 2];
        if (!TryReadBytes(address, bytes)) return null;
        var chars = MemoryMarshal.Cast<byte, char>(bytes);
        int len = chars.IndexOf('\0');
        if (len < 0) len = chars.Length;
        return new string(chars[..len]);
    }

    /// <summary>
    /// Copies the game's main module image so it can be pattern-scanned. Unreadable pages are left zeroed.
    /// </summary>
    public byte[] ReadModuleImage()
    {
        var image = new byte[ModuleSize];
        const int chunk = 1 << 20;
        const int page = 4096;
        for (int offset = 0; offset < ModuleSize; offset += chunk)
        {
            int len = Math.Min(chunk, ModuleSize - offset);
            if (TryReadBytes(ModuleBase + offset, image.AsSpan(offset, len))) continue;
            // A protected page somewhere in this chunk; fall back to reading page by page.
            for (int p = 0; p < len; p += page)
            {
                int plen = Math.Min(page, len - p);
                TryReadBytes(ModuleBase + offset + p, image.AsSpan(offset + p, plen));
            }
        }
        return image;
    }

    public bool IsInModule(nint address) => address >= ModuleBase && address < ModuleBase + ModuleSize;

    public void Dispose()
    {
        if (_handle != 0)
        {
            CloseHandle(_handle);
            _handle = 0;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool ReadProcessMemory(nint process, nint baseAddress, byte* buffer, nint size, out nint bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);
}
