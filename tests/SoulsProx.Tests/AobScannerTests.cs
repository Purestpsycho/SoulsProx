using SoulsProx.Memory;

namespace SoulsProx.Tests;

public class AobScannerTests
{
    [Fact]
    public void Finds_pattern_with_wildcards()
    {
        byte[] data = [0x00, 0x48, 0x8b, 0x0d, 0x11, 0x22, 0x33, 0x44, 0x45, 0x33, 0xc0, 0x90];
        var pattern = new AobPattern("48 8b 0d ? ? ?? ? 45 33 c0");
        Assert.Equal(1, AobScanner.Find(data, pattern));
    }

    [Fact]
    public void Returns_minus_one_when_absent()
    {
        byte[] data = [0x48, 0x8b, 0x0d, 0x00, 0x00, 0x00, 0x00, 0x45, 0x33, 0xc1];
        Assert.Equal(-1, AobScanner.Find(data, new AobPattern("48 8b 0d ? ? ? ? 45 33 c0")));
    }

    [Fact]
    public void Skips_partial_matches()
    {
        byte[] data = [0x48, 0x8b, 0x00, 0x48, 0x8b, 0x0d, 0x01];
        Assert.Equal(3, AobScanner.Find(data, new AobPattern("48 8b 0d ?")));
    }

    [Fact]
    public void Leading_wildcard_works()
    {
        byte[] data = [0x01, 0x02, 0xAA, 0xBB];
        Assert.Equal(1, AobScanner.Find(data, new AobPattern("? aa bb")));
    }

    [Fact]
    public void Resolves_rip_relative_operand()
    {
        // mov rcx, [rip + 0x100] at offset 0x10: 48 8b 0d 00 01 00 00 (7 bytes)
        var image = new byte[0x20];
        byte[] instr = [0x48, 0x8b, 0x0d, 0x00, 0x01, 0x00, 0x00];
        instr.CopyTo(image, 0x10);
        nint moduleBase = unchecked((nint)0x140000000L);
        nint resolved = AobScanner.ResolveRipRelative(image, moduleBase, 0x10, 3, 7);
        Assert.Equal(moduleBase + 0x10 + 7 + 0x100, resolved);
    }

    [Fact]
    public void RipSignature_resolves_negative_displacement()
    {
        var image = new byte[0x40];
        byte[] instr = [0x48, 0x8b, 0x05, 0xF0, 0xFF, 0xFF, 0xFF]; // disp -0x10
        instr.CopyTo(image, 0x20);
        var sig = new RipSignature("test", "48 8b 05 ? ? ? ?", 3, 7);
        Assert.Equal((nint)0x1000 + 0x20 + 7 - 0x10, sig.Resolve(image, 0x1000));
    }
}
