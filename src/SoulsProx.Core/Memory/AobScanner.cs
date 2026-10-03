using System.Globalization;

namespace SoulsProx.Memory;

/// <summary>
/// An "array of bytes" signature such as "48 8b 0d ? ? ? ? 45 33 c0", where ? (or ??) matches any byte.
/// </summary>
public sealed class AobPattern
{
    public byte[] Bytes { get; }
    public bool[] Mask { get; }
    public string Text { get; }

    public AobPattern(string text)
    {
        Text = text;
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Bytes = new byte[tokens.Length];
        Mask = new bool[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Trim('?').Length == 0) continue;
            Bytes[i] = byte.Parse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            Mask[i] = true;
        }
        if (!Mask.Contains(true)) throw new ArgumentException("Pattern must contain at least one fixed byte", nameof(text));
    }

    public int Length => Bytes.Length;

    public override string ToString() => Text;
}

public static class AobScanner
{
    /// <summary>Returns the offset of the first match at or after <paramref name="start"/>, or -1.</summary>
    public static int Find(ReadOnlySpan<byte> data, AobPattern pattern, int start = 0)
    {
        // Anchor on the first fixed byte so we can use the vectorized IndexOf to skip ahead.
        int anchor = Array.IndexOf(pattern.Mask, true);
        byte anchorByte = pattern.Bytes[anchor];
        int last = data.Length - pattern.Length;

        int i = start;
        while (i <= last)
        {
            int hit = data.Slice(i + anchor, last - i + 1).IndexOf(anchorByte);
            if (hit < 0) return -1;
            i += hit;
            if (Matches(data, i, pattern)) return i;
            i++;
        }
        return -1;
    }

    private static bool Matches(ReadOnlySpan<byte> data, int at, AobPattern pattern)
    {
        for (int j = 0; j < pattern.Length; j++)
        {
            if (pattern.Mask[j] && data[at + j] != pattern.Bytes[j]) return false;
        }
        return true;
    }

    /// <summary>
    /// Resolves an x64 RIP-relative operand. For an instruction at <paramref name="instructionOffset"/> whose
    /// 32-bit displacement sits at +<paramref name="displacementOffset"/> and which is
    /// <paramref name="instructionLength"/> bytes long, returns the absolute address it references.
    /// </summary>
    public static nint ResolveRipRelative(ReadOnlySpan<byte> image, nint moduleBase, int instructionOffset, int displacementOffset, int instructionLength)
    {
        int disp = BitConverter.ToInt32(image.Slice(instructionOffset + displacementOffset, 4));
        return moduleBase + instructionOffset + instructionLength + disp;
    }
}

/// <summary>A signature for an instruction that references a static global through a RIP-relative operand.</summary>
public sealed record RipSignature(string Name, string Pattern, int DisplacementOffset, int InstructionLength)
{
    private readonly AobPattern _compiled = new(Pattern);

    /// <summary>Scans the image and returns the absolute address of the referenced global, or 0.</summary>
    public nint Resolve(ReadOnlySpan<byte> image, nint moduleBase)
    {
        int at = AobScanner.Find(image, _compiled);
        return at < 0 ? 0 : AobScanner.ResolveRipRelative(image, moduleBase, at, DisplacementOffset, InstructionLength);
    }
}
