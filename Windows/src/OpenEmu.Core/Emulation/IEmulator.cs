using OpenEmu.Core.Libretro;
using OpenEmu.Core.Saves;
using OpenEmu.Core.Video;

namespace OpenEmu.Core.Emulation;

/// <summary>Static facts about a running core, valid after start.</summary>
public sealed record EmulatorInfo(
    string CoreName, string CoreVersion, double Fps, double SampleRate, uint BaseWidth, uint BaseHeight, float AspectRatio,
    uint Rotation, bool HasDiskControl, uint DiskImageCount, IReadOnlyList<string> DiskLabels, bool HasKeyboardCallback, bool HwRender, bool IsRemote);

/// <summary>Snapshot of a core option for UIs (serializable, unlike <see cref="CoreOption"/> which is live).</summary>
public sealed record CoreOptionSnapshot(string Key, string Description, string? Info, string? Category, IReadOnlyList<string> Values, IReadOnlyList<string> Labels, string Current)
{
    public static CoreOptionSnapshot From(CoreOption o) => new(o.Key, o.Description, o.Info, o.Category, o.Values.Select(v => v.Value).ToList(), o.Values.Select(v => v.Label).ToList(), o.EffectiveValue);
}

/// <summary>
/// What a game window needs from an emulator, whether the core runs in this process (<see cref="EmulationSession"/>)
/// or in the out-of-process core host (<see cref="Remote.RemoteSession"/>, used on Windows ARM64).
/// </summary>
public interface IEmulator : IDisposable
{
    SessionOptions Options { get; }
    FrameBuffer Frame { get; }
    SessionState State { get; }
    EmulatorInfo? Info { get; }
    IReadOnlyList<CoreOptionSnapshot> CoreOptions { get; }
    Exception? Error { get; }
    bool IsPaused { get; }
    bool FastForward { get; set; }
    int CurrentSlot { get; set; }
    double MeasuredFps { get; }
    uint DiskImageIndex { get; }
    float Volume { get; set; }
    bool Muted { get; set; }
    SaveStateManager States { get; }

    event Action? FrameRendered;
    event Action<SessionState>? StateChanged;
    event Action<CoreMessage>? Message;
    event Action<int, string>? Log;
    event Action<string>? Notification;

    Task StartAsync();
    void Pause();
    void Resume();
    void Stop();
    Task Reset();
    Task<SaveStateInfo?> SaveState(string name, int? slot = null);
    Task<bool> LoadState(SaveStateInfo state);
    Task<SaveStateInfo?> QuickSave();
    Task<bool> QuickLoad();
    Task<SaveStateInfo?> AutoSave();
    SaveStateInfo? FindAutoSave();
    IReadOnlyList<SaveStateInfo> ListStates();
    byte[] Screenshot();
    string SaveScreenshot(string screenshotsDir, string title);
    Task SetOption(string key, string value);
    Task ApplyCheats(IEnumerable<(bool Enabled, string Code)> cheats);
    Task SetDiskImage(uint index);
    Task SendKeyboardEvent(bool down, uint retroKey, uint character, ushort modifiers);
    /// <summary>Keyboard state feed (HID usage) — in-process this is the shared InputManager, remote it is written to shared memory.</summary>
    void SetKey(int hidUsage, bool down);
    void ClearKeys();
}
