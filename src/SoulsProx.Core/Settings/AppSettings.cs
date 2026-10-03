using System.Text.Json;
using System.Text.Json.Serialization;
using SoulsProx.Audio;
using SoulsProx.Input;
using SoulsProx.Net;
using SoulsProx.Proximity;

namespace SoulsProx.Settings;

/// <summary>Everything the user can change, saved as JSON in %AppData%\SoulsProx.</summary>
public sealed record AppSettings
{
    public const int DefaultPort = 47800;

    // Devices
    public string? MicDeviceId { get; init; }
    public string? OutputDeviceId { get; init; }
    /// <summary>Remove background noise from the mic (WebRTC noise suppressor).</summary>
    public bool NoiseSuppression { get; init; } = true;
    /// <summary>Remove the friend's voice from the mic if it leaks out of headphones/speakers (WebRTC AEC3).</summary>
    public bool EchoCancellation { get; init; } = true;

    // Talking
    public TalkMode TalkMode { get; init; } = TalkMode.VoiceActivation;
    public float VoiceThresholdDb { get; init; } = -45f;
    public InputBinding? PushToTalkKey { get; init; }
    public InputBinding? RadioKey { get; init; }

    // Hearing
    public float FriendVolume { get; init; } = 1f;
    public float NearDistance { get; init; } = 5f;
    public float FarDistance { get; init; } = 35f;
    public float OutOfRangeVolume { get; init; }
    public bool SwapLeftRight { get; init; }
    public bool RadioEffect { get; init; } = true;
    public bool FullVolumeOutsideWorld { get; init; } = true;

    // Connection
    public int Port { get; init; } = DefaultPort;
    /// <summary>Base64 secret that goes into my connection code. Kept between runs so the code stays the same.</summary>
    public string Secret { get; init; } = Convert.ToBase64String(ConnectionCode.NewSecret());
    public string? FriendCode { get; init; }
    public string? FriendAddressOverride { get; init; }
    public int Bitrate { get; init; } = 32_000;

    [JsonIgnore]
    public byte[] SecretBytes
    {
        get
        {
            try
            {
                var bytes = Convert.FromBase64String(Secret);
                if (bytes.Length == ConnectionCode.SecretLength) return bytes;
            }
            catch (FormatException) { }
            return ConnectionCode.NewSecret();
        }
    }

    public ProximitySettings ToProximity() => new()
    {
        NearDistance = NearDistance,
        FarDistance = MathF.Max(FarDistance, NearDistance + 1f),
        OutOfRangeVolume = OutOfRangeVolume,
        SwapLeftRight = SwapLeftRight,
        FullVolumeOutsideWorld = FullVolumeOutsideWorld,
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoulsProx");

    public static string FilePath(string? profile) =>
        Path.Combine(Directory, string.IsNullOrEmpty(profile) ? "settings.json" : $"settings-{profile}.json");

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
                if (loaded is not null) return loaded with { Secret = Convert.ToBase64String(loaded.SecretBytes) };
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            Log.Error("Settings file unreadable, using defaults", ex);
        }
        var fresh = new AppSettings();
        fresh.Save(path);
        return fresh;
    }

    public void Save(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }
}
