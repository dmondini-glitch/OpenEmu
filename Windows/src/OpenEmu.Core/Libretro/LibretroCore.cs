using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenEmu.Core.Libretro;

public delegate void AudioSamplesHandler(ReadOnlySpan<short> interleavedStereo);
public delegate void VideoFrameHandler(IntPtr data, uint width, uint height, nuint pitch, int pixelFormat);

/// <summary>Source of input state queried by the core (RetroPad, analog, keyboard, pointer...).</summary>
public interface IInputSource
{
    void Poll();
    short GetState(uint port, uint device, uint index, uint id);
}

/// <summary>Host side of the libretro hardware-render (OpenGL) contract.</summary>
public interface IHwRenderHost
{
    /// <summary>Returns the FBO the core must render into (valid on the GL thread).</summary>
    nuint CurrentFramebuffer { get; }
    IntPtr GetProcAddress(string symbol);
}

/// <summary>
/// Loads a libretro core ("driver plugin") and wires its callbacks to managed events.
/// All core entry points must be invoked from the same thread (the emulation thread).
/// </summary>
public sealed unsafe class LibretroCore : IDisposable
{
    [ThreadStatic] private static LibretroCore? t_current;
    private static LibretroCore? s_current;
    private static LibretroCore Current => t_current ?? s_current ?? throw new InvalidOperationException("No active libretro core");

    private IntPtr _lib;
    private readonly List<IntPtr> _nativeStrings = new();
    private readonly Dictionary<string, IntPtr> _variableValues = new();
    private byte[]? _gameData;
    private GCHandle _gameDataHandle;
    private bool _disposed;

    // ---- entry points
    private delegate* unmanaged[Cdecl]<IntPtr, void> _setEnvironment, _setVideoRefresh, _setAudioSample, _setAudioSampleBatch, _setInputPoll, _setInputState;
    private delegate* unmanaged[Cdecl]<void> _init, _deinit, _run, _reset, _cheatReset, _unloadGame;
    private delegate* unmanaged[Cdecl]<uint> _apiVersion, _getRegion;
    private delegate* unmanaged[Cdecl]<retro_system_info*, void> _getSystemInfo;
    private delegate* unmanaged[Cdecl]<retro_system_av_info*, void> _getSystemAvInfo;
    private delegate* unmanaged[Cdecl]<uint, uint, void> _setControllerPortDevice;
    private delegate* unmanaged[Cdecl]<nuint> _serializeSize;
    private delegate* unmanaged[Cdecl]<void*, nuint, byte> _serialize, _unserialize;
    private delegate* unmanaged[Cdecl]<uint, byte, byte*, void> _cheatSet;
    private delegate* unmanaged[Cdecl]<retro_game_info*, byte> _loadGame;
    private delegate* unmanaged[Cdecl]<uint, retro_game_info*, nuint, byte> _loadGameSpecial;
    private delegate* unmanaged[Cdecl]<uint, void*> _getMemoryData;
    private delegate* unmanaged[Cdecl]<uint, nuint> _getMemorySize;

    // ---- public state
    public string LibraryPath { get; }
    public string LibraryName { get; private set; } = "";
    public string LibraryVersion { get; private set; } = "";
    public string[] ValidExtensions { get; private set; } = Array.Empty<string>();
    public bool NeedFullPath { get; private set; }
    public bool BlockExtract { get; private set; }
    public bool SupportsNoGame { get; private set; }
    public int PixelFormat { get; private set; } = Retro.PixelFormat0Rgb1555;
    public uint Rotation { get; private set; }
    public retro_system_av_info AvInfo { get; private set; }
    public bool GameLoaded { get; private set; }
    public bool Initialized { get; private set; }
    public bool ShutdownRequested { get; private set; }
    public uint PerformanceLevel { get; private set; }
    public uint SerializationQuirks { get; private set; }
    public bool FastForwarding { get; set; }
    public double TargetRefreshRate { get; set; } = 60.0;
    public uint Language { get; set; } = Retro.LanguageEnglish;
    public string SystemDirectory { get; set; } = "";
    public string SaveDirectory { get; set; } = "";
    public string CoreAssetsDirectory { get; set; } = "";
    public string Username { get; set; } = "";
    public bool VariablesUpdated { get; private set; }
    public Dictionary<string, CoreOption> Options { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> OptionOverrides { get; } = new(StringComparer.Ordinal);
    public List<InputDescriptor> InputDescriptors { get; } = new();
    public List<List<ControllerDescription>> ControllerInfo { get; } = new();
    public List<string> SubsystemIds { get; } = new();
    public retro_hw_render_callback HwRender;
    public bool UsesHwRender { get; private set; }
    public IHwRenderHost? HwRenderHost { get; set; }
    public retro_disk_control_ext_callback DiskControl;
    public bool HasDiskControl { get; private set; }
    public retro_fastforwarding_override? FastForwardOverride { get; private set; }
    public uint MinimumAudioLatencyMs { get; private set; }
    private retro_frame_time_callback _frameTime;
    private bool _hasFrameTime;
    private retro_keyboard_callback _keyboard;
    public bool HasKeyboardCallback { get; private set; }
    private readonly HashSet<uint> _unknownEnv = new();

    // ---- host callbacks
    public IInputSource? InputSource { get; set; }
    public event VideoFrameHandler? VideoFrame;
    public event Action? VideoFrameDuplicated;
    public event AudioSamplesHandler? AudioSamples;
    public event Action<int, string>? Log;
    public event Action<CoreMessage>? Message;
    public event Action? GeometryChanged;
    public event Action? AvInfoChanged;
    public event Action? OptionsChanged;
    public event Action<uint, uint, ushort>? Rumble;
    public event Action? HwContextReset;
    public event Action? HwContextDestroy;

    public LibretroCore(string libraryPath)
    {
        LibraryPath = libraryPath;
        _lib = NativeLibrary.Load(libraryPath);
        _setEnvironment = (delegate* unmanaged[Cdecl]<IntPtr, void>)Sym("retro_set_environment");
        _setVideoRefresh = (delegate* unmanaged[Cdecl]<IntPtr, void>)Sym("retro_set_video_refresh");
        _setAudioSample = (delegate* unmanaged[Cdecl]<IntPtr, void>)Sym("retro_set_audio_sample");
        _setAudioSampleBatch = (delegate* unmanaged[Cdecl]<IntPtr, void>)Sym("retro_set_audio_sample_batch");
        _setInputPoll = (delegate* unmanaged[Cdecl]<IntPtr, void>)Sym("retro_set_input_poll");
        _setInputState = (delegate* unmanaged[Cdecl]<IntPtr, void>)Sym("retro_set_input_state");
        _init = (delegate* unmanaged[Cdecl]<void>)Sym("retro_init");
        _deinit = (delegate* unmanaged[Cdecl]<void>)Sym("retro_deinit");
        _apiVersion = (delegate* unmanaged[Cdecl]<uint>)Sym("retro_api_version");
        _getSystemInfo = (delegate* unmanaged[Cdecl]<retro_system_info*, void>)Sym("retro_get_system_info");
        _getSystemAvInfo = (delegate* unmanaged[Cdecl]<retro_system_av_info*, void>)Sym("retro_get_system_av_info");
        _setControllerPortDevice = (delegate* unmanaged[Cdecl]<uint, uint, void>)Sym("retro_set_controller_port_device");
        _reset = (delegate* unmanaged[Cdecl]<void>)Sym("retro_reset");
        _run = (delegate* unmanaged[Cdecl]<void>)Sym("retro_run");
        _serializeSize = (delegate* unmanaged[Cdecl]<nuint>)Sym("retro_serialize_size");
        _serialize = (delegate* unmanaged[Cdecl]<void*, nuint, byte>)Sym("retro_serialize");
        _unserialize = (delegate* unmanaged[Cdecl]<void*, nuint, byte>)Sym("retro_unserialize");
        _cheatReset = (delegate* unmanaged[Cdecl]<void>)Sym("retro_cheat_reset");
        _cheatSet = (delegate* unmanaged[Cdecl]<uint, byte, byte*, void>)Sym("retro_cheat_set");
        _loadGame = (delegate* unmanaged[Cdecl]<retro_game_info*, byte>)Sym("retro_load_game");
        _loadGameSpecial = (delegate* unmanaged[Cdecl]<uint, retro_game_info*, nuint, byte>)Sym("retro_load_game_special");
        _unloadGame = (delegate* unmanaged[Cdecl]<void>)Sym("retro_unload_game");
        _getRegion = (delegate* unmanaged[Cdecl]<uint>)Sym("retro_get_region");
        _getMemoryData = (delegate* unmanaged[Cdecl]<uint, void*>)Sym("retro_get_memory_data");
        _getMemorySize = (delegate* unmanaged[Cdecl]<uint, nuint>)Sym("retro_get_memory_size");

        var v = _apiVersion();
        if (v != Retro.ApiVersion) throw new InvalidOperationException($"Unsupported libretro API version {v}");
        ReadSystemInfo();
    }

    private IntPtr Sym(string name) => NativeLibrary.GetExport(_lib, name);

    private void ReadSystemInfo()
    {
        retro_system_info info = default;
        _getSystemInfo(&info);
        LibraryName = S(info.library_name) ?? Path.GetFileNameWithoutExtension(LibraryPath);
        LibraryVersion = S(info.library_version) ?? "";
        ValidExtensions = (S(info.valid_extensions) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        NeedFullPath = info.need_fullpath;
        BlockExtract = info.block_extract;
    }

    private void MakeCurrent() { t_current = this; s_current = this; }

    // ------------------------------------------------------------------ lifecycle
    public void Init()
    {
        MakeCurrent();
        _setEnvironment((IntPtr)(delegate* unmanaged[Cdecl]<uint, void*, byte>)&EnvironmentCb);
        _setVideoRefresh((IntPtr)(delegate* unmanaged[Cdecl]<void*, uint, uint, nuint, void>)&VideoRefreshCb);
        _setAudioSample((IntPtr)(delegate* unmanaged[Cdecl]<short, short, void>)&AudioSampleCb);
        _setAudioSampleBatch((IntPtr)(delegate* unmanaged[Cdecl]<short*, nuint, nuint>)&AudioSampleBatchCb);
        _setInputPoll((IntPtr)(delegate* unmanaged[Cdecl]<void>)&InputPollCb);
        _setInputState((IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, uint, uint, short>)&InputStateCb);
        _init();
        Initialized = true;
    }

    public bool LoadGame(string? path, byte[]? data = null, string? meta = null)
    {
        MakeCurrent();
        retro_game_info gi = default;
        if (path != null) gi.path = (byte*)Alloc(path);
        if (path == null && !SupportsNoGame) throw new ArgumentException("Core requires content");
        if (path != null && !NeedFullPath)
        {
            _gameData = data ?? File.ReadAllBytes(path);
            _gameDataHandle = GCHandle.Alloc(_gameData, GCHandleType.Pinned);
            gi.data = (void*)_gameDataHandle.AddrOfPinnedObject();
            gi.size = (nuint)_gameData.Length;
        }
        if (meta != null) gi.meta = (byte*)Alloc(meta);
        var ok = _loadGame(path == null ? null : &gi) != 0;
        if (ok)
        {
            GameLoaded = true;
            RefreshAvInfo();
        }
        else FreeGameData();
        return ok;
    }

    public void RefreshAvInfo()
    {
        retro_system_av_info av = default;
        _getSystemAvInfo(&av);
        AvInfo = av;
    }

    public void Run()
    {
        MakeCurrent();
        if (_hasFrameTime) _frameTime.callback(_frameTime.reference);
        _run();
    }

    public void Reset() { MakeCurrent(); _reset(); }
    public uint Region { get { MakeCurrent(); return _getRegion(); } }

    public void SetControllerPortDevice(uint port, uint device) { MakeCurrent(); _setControllerPortDevice(port, device); }

    public void UnloadGame()
    {
        if (!GameLoaded) return;
        MakeCurrent();
        _unloadGame();
        GameLoaded = false;
        FreeGameData();
    }

    private void FreeGameData()
    {
        if (_gameDataHandle.IsAllocated) _gameDataHandle.Free();
        _gameData = null;
    }

    public void Deinit()
    {
        if (!Initialized) return;
        MakeCurrent();
        _deinit();
        Initialized = false;
    }

    // ------------------------------------------------------------------ save states / memory / cheats
    public nuint SerializeSize { get { MakeCurrent(); return _serializeSize(); } }

    public byte[]? Serialize()
    {
        MakeCurrent();
        var size = _serializeSize();
        if (size == 0) return null;
        var buf = new byte[size];
        fixed (byte* p = buf)
            if (_serialize(p, size) == 0) return null;
        return buf;
    }

    public bool Unserialize(ReadOnlySpan<byte> state)
    {
        MakeCurrent();
        fixed (byte* p = state) return _unserialize(p, (nuint)state.Length) != 0;
    }

    public void CheatReset() { MakeCurrent(); _cheatReset(); }

    public void CheatSet(uint index, bool enabled, string code)
    {
        MakeCurrent();
        var ptr = Marshal.StringToCoTaskMemUTF8(code);
        try { _cheatSet(index, (byte)(enabled ? 1 : 0), (byte*)ptr); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    public (IntPtr Data, nuint Size) GetMemory(uint type)
    {
        MakeCurrent();
        return ((IntPtr)_getMemoryData(type), _getMemorySize(type));
    }

    public byte[]? ReadMemory(uint type)
    {
        var (data, size) = GetMemory(type);
        if (data == IntPtr.Zero || size == 0) return null;
        var buf = new byte[size];
        Marshal.Copy(data, buf, 0, (int)size);
        return buf;
    }

    public bool WriteMemory(uint type, ReadOnlySpan<byte> bytes)
    {
        var (data, size) = GetMemory(type);
        if (data == IntPtr.Zero || size == 0) return false;
        var n = (int)Math.Min((ulong)size, (ulong)bytes.Length);
        bytes[..n].CopyTo(new Span<byte>((void*)data, n));
        return true;
    }

    // ------------------------------------------------------------------ options
    public void SetOption(string key, string value)
    {
        if (Options.TryGetValue(key, out var opt)) opt.CurrentValue = value;
        else Options[key] = new CoreOption { Key = key, CurrentValue = value };
        OptionOverrides[key] = value;
        VariablesUpdated = true;
    }

    // ------------------------------------------------------------------ disk control
    public uint DiskImageCount => HasDiskControl ? DiskControl.basic.get_num_images() : 0;
    public uint DiskImageIndex => HasDiskControl ? DiskControl.basic.get_image_index() : 0;
    public bool DiskEjected => HasDiskControl && DiskControl.basic.get_eject_state() != 0;
    public bool SetDiskEjected(bool ejected) => HasDiskControl && DiskControl.basic.set_eject_state((byte)(ejected ? 1 : 0)) != 0;
    public bool SetDiskImageIndex(uint index) => HasDiskControl && DiskControl.basic.set_image_index(index) != 0;
    public string? GetDiskImageLabel(uint index)
    {
        if (!HasDiskControl || DiskControl.get_image_label == null) return null;
        var buf = stackalloc byte[512];
        return DiskControl.get_image_label(index, buf, 512) != 0 ? S(buf) : null;
    }

    // ------------------------------------------------------------------ keyboard events (computers)
    public void SendKeyboardEvent(bool down, uint retroKey, uint character, ushort modifiers)
    {
        if (!HasKeyboardCallback) return;
        MakeCurrent();
        _keyboard.callback((byte)(down ? 1 : 0), retroKey, character, modifiers);
    }

    // ------------------------------------------------------------------ helpers
    private static string? S(byte* p) => p == null ? null : Marshal.PtrToStringUTF8((IntPtr)p);

    private IntPtr Alloc(string s)
    {
        var p = Marshal.StringToCoTaskMemUTF8(s);
        _nativeStrings.Add(p);
        return p;
    }

    private IntPtr VariableValuePtr(string key, string value)
    {
        // Cores are allowed to keep the returned pointer, so never free values while the core is alive.
        var k = key + "\0" + value;
        if (!_variableValues.TryGetValue(k, out var p)) { p = Alloc(value); _variableValues[k] = p; }
        return p;
    }

    private void Emit(int level, string msg) => Log?.Invoke(level, msg);

    // ------------------------------------------------------------------ native callbacks
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte EnvironmentCb(uint cmd, void* data)
    {
        try { return Current.HandleEnvironment(cmd, data) ? (byte)1 : (byte)0; }
        catch (Exception ex) { Current.Emit(Retro.LogError, $"environment({cmd}) failed: {ex}"); return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void VideoRefreshCb(void* data, uint width, uint height, nuint pitch)
    {
        var c = Current;
        if (data == null) { c.VideoFrameDuplicated?.Invoke(); return; }
        c.VideoFrame?.Invoke((IntPtr)data, width, height, pitch, c.PixelFormat);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void AudioSampleCb(short left, short right)
    {
        var pair = stackalloc short[2];
        pair[0] = left; pair[1] = right;
        Current.AudioSamples?.Invoke(new ReadOnlySpan<short>(pair, 2));
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nuint AudioSampleBatchCb(short* data, nuint frames)
    {
        Current.AudioSamples?.Invoke(new ReadOnlySpan<short>(data, (int)frames * 2));
        return frames;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void InputPollCb() => Current.InputSource?.Poll();

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static short InputStateCb(uint port, uint device, uint index, uint id)
    {
        var src = Current.InputSource;
        if (src == null) return 0;
        if (device == Retro.DeviceJoypad && id == Retro.JoypadMask)
        {
            short mask = 0;
            for (uint i = 0; i < 16; i++) if (src.GetState(port, device, index, i) != 0) mask |= (short)(1 << (int)i);
            return mask;
        }
        return src.GetState(port, device, index, id);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void LogCb(int level, byte* fmt, IntPtr a1, IntPtr a2, IntPtr a3, IntPtr a4, IntPtr a5, IntPtr a6)
    {
        try
        {
            var f = S(fmt) ?? "";
            var msg = OperatingSystem.IsWindows() ? PrintfLite.Format(f, a1, a2, a3, a4, a5, a6) : PrintfLite.Strip(f);
            Current.Emit(level, msg.TrimEnd('\n', '\r'));
        }
        catch { /* never throw into native code */ }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte RumbleCb(uint port, uint effect, ushort strength)
    {
        Current.Rumble?.Invoke(port, effect, strength);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nuint GetCurrentFramebufferCb() => Current.HwRenderHost?.CurrentFramebuffer ?? 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetProcAddressCb(byte* sym)
    {
        var host = Current.HwRenderHost;
        var name = S(sym);
        return host == null || name == null ? IntPtr.Zero : host.GetProcAddress(name);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static long PerfGetTimeUsec() => DateTime.UtcNow.Ticks / 10;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static ulong PerfGetCpuFeatures() => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static ulong PerfGetCounter() => (ulong)System.Diagnostics.Stopwatch.GetTimestamp();
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void PerfNoop(void* counter) { }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void PerfLogNoop() { }

    public void FireHwContextReset() { if (HwRender.context_reset != null) { MakeCurrent(); HwRender.context_reset(); } HwContextReset?.Invoke(); }
    public void FireHwContextDestroy() { if (HwRender.context_destroy != null) { MakeCurrent(); HwRender.context_destroy(); } HwContextDestroy?.Invoke(); }

    // ------------------------------------------------------------------ environment
    private bool HandleEnvironment(uint cmd, void* data)
    {
        switch (cmd)
        {
            case Retro.EnvSetRotation: Rotation = *(uint*)data; return true;
            case Retro.EnvGetOverscan: *(byte*)data = 0; return true;
            case Retro.EnvGetCanDupe: *(byte*)data = 1; return true;
            case Retro.EnvSetMessage: { var m = (retro_message*)data; Message?.Invoke(new CoreMessage(S(m->msg) ?? "", m->frames)); return true; }
            case Retro.EnvShutdown: ShutdownRequested = true; return true;
            case Retro.EnvSetPerformanceLevel: PerformanceLevel = *(uint*)data; return true;
            case Retro.EnvGetSystemDirectory: *(byte**)data = (byte*)Alloc(SystemDirectory); return true;
            case Retro.EnvSetPixelFormat:
            {
                var fmt = *(int*)data;
                if (fmt < 0 || fmt > 2) return false;
                PixelFormat = fmt; return true;
            }
            case Retro.EnvSetInputDescriptors:
            {
                InputDescriptors.Clear();
                for (var d = (retro_input_descriptor*)data; d->description != null; d++)
                    InputDescriptors.Add(new InputDescriptor(d->port, d->device, d->index, d->id, S(d->description)!));
                return true;
            }
            case Retro.EnvSetKeyboardCallback: _keyboard = *(retro_keyboard_callback*)data; HasKeyboardCallback = true; return true;
            case Retro.EnvSetDiskControlInterface:
                DiskControl = default; DiskControl.basic = *(retro_disk_control_callback*)data; HasDiskControl = true; return true;
            case Retro.EnvSetDiskControlExtInterface: DiskControl = *(retro_disk_control_ext_callback*)data; HasDiskControl = true; return true;
            case Retro.EnvGetDiskControlInterfaceVersion: *(uint*)data = 1; return true;
            case Retro.EnvSetHwRender:
            {
                if (HwRenderHost == null) { Emit(Retro.LogWarn, "Core requested hardware rendering but no GL host is available"); return false; }
                var cb = (retro_hw_render_callback*)data;
                if (cb->context_type != Retro.HwContextOpenGl && cb->context_type != Retro.HwContextOpenGlCore) { Emit(Retro.LogWarn, $"Unsupported HW context type {cb->context_type}"); return false; }
                cb->get_current_framebuffer = &GetCurrentFramebufferCb;
                cb->get_proc_address = &GetProcAddressCb;
                HwRender = *cb; UsesHwRender = true; return true;
            }
            case Retro.EnvGetPreferredHwRender: *(uint*)data = HwRenderHost != null ? Retro.HwContextOpenGl : Retro.HwContextNone; return true;
            case Retro.EnvGetVariable:
            {
                var v = (retro_variable*)data;
                var key = S(v->key);
                if (key != null && Options.TryGetValue(key, out var opt)) { v->value = (byte*)VariableValuePtr(key, opt.EffectiveValue); return true; }
                if (key != null && OptionOverrides.TryGetValue(key, out var ov)) { v->value = (byte*)VariableValuePtr(key, ov); return true; }
                v->value = null; return false;
            }
            case Retro.EnvSetVariables:
            {
                for (var v = (retro_variable*)data; v->key != null; v++)
                {
                    var key = S(v->key)!; var raw = S(v->value) ?? "";
                    var opt = GetOrAddOption(key);
                    var semi = raw.IndexOf(';');
                    opt.Description = semi >= 0 ? raw[..semi].Trim() : key;
                    opt.Values.Clear();
                    var list = semi >= 0 ? raw[(semi + 1)..] : raw;
                    foreach (var val in list.Split('|')) { var t = val.Trim(); if (t.Length > 0) opt.Values.Add((t, t)); }
                    opt.DefaultValue = opt.Values.Count > 0 ? opt.Values[0].Value : null;
                }
                OptionsChanged?.Invoke(); return true;
            }
            case Retro.EnvGetVariableUpdate: { *(byte*)data = (byte)(VariablesUpdated ? 1 : 0); VariablesUpdated = false; return true; }
            case Retro.EnvSetSupportNoGame: SupportsNoGame = *(byte*)data != 0; return true;
            case Retro.EnvGetLibretroPath: *(byte**)data = (byte*)Alloc(LibraryPath); return true;
            case Retro.EnvSetFrameTimeCallback: _frameTime = *(retro_frame_time_callback*)data; _hasFrameTime = true; return true;
            case Retro.EnvSetAudioCallback: return false;
            case Retro.EnvGetRumbleInterface: ((retro_rumble_interface*)data)->set_rumble_state = &RumbleCb; return true;
            case Retro.EnvGetInputDeviceCapabilities: *(ulong*)data = Retro.InputDeviceCapJoypad | Retro.InputDeviceCapAnalog | Retro.InputDeviceCapKeyboard | Retro.InputDeviceCapMouse | Retro.InputDeviceCapPointer; return true;
            case Retro.EnvGetLogInterface: ((retro_log_callback*)data)->log = &LogCb; return true;
            case Retro.EnvGetPerfInterface:
            {
                var p = (retro_perf_callback*)data;
                p->get_time_usec = &PerfGetTimeUsec; p->get_cpu_features = &PerfGetCpuFeatures; p->get_perf_counter = &PerfGetCounter;
                p->perf_register = &PerfNoop; p->perf_start = &PerfNoop; p->perf_stop = &PerfNoop; p->perf_log = &PerfLogNoop;
                return true;
            }
            case Retro.EnvGetCoreAssetsDirectory: *(byte**)data = (byte*)Alloc(CoreAssetsDirectory.Length > 0 ? CoreAssetsDirectory : SystemDirectory); return true;
            case Retro.EnvGetSaveDirectory: *(byte**)data = (byte*)Alloc(SaveDirectory.Length > 0 ? SaveDirectory : SystemDirectory); return true;
            case Retro.EnvSetSystemAvInfo: AvInfo = *(retro_system_av_info*)data; AvInfoChanged?.Invoke(); return true;
            case Retro.EnvSetProcAddressCallback: return false;
            case Retro.EnvSetSubsystemInfo: return true;
            case Retro.EnvSetControllerInfo:
            {
                ControllerInfo.Clear();
                for (var ci = (retro_controller_info*)data; ci->types != null; ci++)
                {
                    var list = new List<ControllerDescription>();
                    for (uint i = 0; i < ci->num_types; i++) list.Add(new ControllerDescription(S(ci->types[i].desc) ?? "", ci->types[i].id));
                    ControllerInfo.Add(list);
                }
                return true;
            }
            case Retro.EnvSetMemoryMaps: return true;
            case Retro.EnvSetGeometry:
            {
                var av = AvInfo; av.geometry = *(retro_game_geometry*)data; AvInfo = av; GeometryChanged?.Invoke(); return true;
            }
            case Retro.EnvGetUsername: if (Username.Length == 0) return false; *(byte**)data = (byte*)Alloc(Username); return true;
            case Retro.EnvGetLanguage: *(uint*)data = Language; return true;
            case Retro.EnvGetCurrentSoftwareFramebuffer: return false;
            case Retro.EnvGetHwRenderInterface: return false;
            case Retro.EnvSetSupportAchievements: return true;
            case Retro.EnvSetHwRenderContextNegotiationInterface: return false;
            case Retro.EnvSetSerializationQuirks: SerializationQuirks = *(ulong*)data > uint.MaxValue ? uint.MaxValue : (uint)*(ulong*)data; return true;
            case Retro.EnvSetHwSharedContext: return true;
            case Retro.EnvGetVfsInterface: return false;
            case Retro.EnvGetLedInterface: return false;
            case Retro.EnvGetAudioVideoEnable: *(int*)data = 3; return true;
            case Retro.EnvGetMidiInterface: return false;
            case Retro.EnvGetFastForwarding: *(byte*)data = (byte)(FastForwarding ? 1 : 0); return true;
            case Retro.EnvGetTargetRefreshRate: *(float*)data = (float)TargetRefreshRate; return true;
            case Retro.EnvGetInputBitmasks: return true;
            case Retro.EnvGetCoreOptionsVersion: *(uint*)data = 2; return true;
            case Retro.EnvSetCoreOptions: ParseOptionsV1((retro_core_option_definition*)data); return true;
            case Retro.EnvSetCoreOptionsIntl: ParseOptionsV1(((retro_core_options_intl*)data)->us); return true;
            case Retro.EnvSetCoreOptionsV2: ParseOptionsV2((retro_core_options_v2*)data); return true;
            case Retro.EnvSetCoreOptionsV2Intl: ParseOptionsV2(((retro_core_options_v2_intl*)data)->us); return true;
            case Retro.EnvSetCoreOptionsDisplay:
            {
                var d = (retro_core_option_display*)data; var key = S(d->key);
                if (key != null && Options.TryGetValue(key, out var o)) o.Visible = d->visible;
                return true;
            }
            case Retro.EnvSetCoreOptionsUpdateDisplayCallback: return false;
            case Retro.EnvSetVariable:
            {
                if (data == null) return true; // query for support
                var v = (retro_variable*)data; var key = S(v->key); var val = S(v->value);
                if (key == null || val == null) return false;
                GetOrAddOption(key).CurrentValue = val; return true;
            }
            case Retro.EnvGetMessageInterfaceVersion: *(uint*)data = 1; return true;
            case Retro.EnvSetMessageExt:
            {
                var m = (retro_message_ext*)data;
                Message?.Invoke(new CoreMessage(S(m->msg) ?? "", (uint)(m->duration / 16), m->level)); return true;
            }
            case Retro.EnvGetInputMaxUsers: *(uint*)data = 8; return true;
            case Retro.EnvSetAudioBufferStatusCallback: return false;
            case Retro.EnvSetMinimumAudioLatency: MinimumAudioLatencyMs = *(uint*)data; return true;
            case Retro.EnvSetFastForwardingOverride: FastForwardOverride = *(retro_fastforwarding_override*)data; return true;
            case Retro.EnvSetContentInfoOverride: return true;
            case Retro.EnvGetGameInfoExt: return false;
            case Retro.EnvGetThrottleState:
            {
                var t = (retro_throttle_state*)data;
                t->mode = (uint)(FastForwarding ? Retro.ThrottleFastForward : Retro.ThrottleNone);
                t->rate = (float)(FastForwarding ? 0 : AvInfo.timing.fps); return true;
            }
            case Retro.EnvGetSavestateContext: *(int*)data = Retro.SavestateContextNormal; return true;
            case Retro.EnvGetHwRenderContextNegotiationInterfaceSupport: return false;
            case Retro.EnvGetJitCapable: *(byte*)data = 1; return true;
            case Retro.EnvGetMicrophoneInterface: return false;
            case Retro.EnvGetDevicePower: return false;
            case Retro.EnvSetNetpacketInterface: return false;
            case Retro.EnvGetPlaylistDirectory: return false;
            case Retro.EnvGetFileBrowserStartDirectory: return false;
            default:
                if (_unknownEnv.Add(cmd)) Emit(Retro.LogDebug, $"Unhandled environment command {cmd & 0xFFFF}{((cmd & Retro.EnvExperimental) != 0 ? " (experimental)" : "")}");
                return false;
        }
    }

    private CoreOption GetOrAddOption(string key)
    {
        if (!Options.TryGetValue(key, out var opt))
        {
            opt = new CoreOption { Key = key, Description = key };
            if (OptionOverrides.TryGetValue(key, out var ov)) opt.CurrentValue = ov;
            Options[key] = opt;
        }
        return opt;
    }

    // retro_core_option_definition / _v2_definition contain a fixed array of 128 {value,label} pointer pairs, so their
    // layout depends on the pointer size (x86 = 4 bytes, x64/ARM64 = 8). They are walked manually to support all three.
    private static readonly int PtrSize = IntPtr.Size;
    private static byte* PtrAt(byte* basePtr, int index) => *(byte**)(basePtr + index * PtrSize);

    private void ParseOptionsV1(retro_core_option_definition* defsTyped)
    {
        // layout: key, desc, info, values[128]{value,label}, default_value
        var stride = (3 + 256 + 1) * PtrSize;
        for (var d = (byte*)defsTyped; d != null && PtrAt(d, 0) != null; d += stride)
        {
            var opt = GetOrAddOption(S(PtrAt(d, 0))!);
            opt.Description = S(PtrAt(d, 1)) ?? opt.Key; opt.Info = S(PtrAt(d, 2));
            opt.Values.Clear();
            for (var i = 0; i < 128 && PtrAt(d, 3 + i * 2) != null; i++) opt.Values.Add((S(PtrAt(d, 3 + i * 2))!, S(PtrAt(d, 4 + i * 2)) ?? S(PtrAt(d, 3 + i * 2))!));
            opt.DefaultValue = S(PtrAt(d, 3 + 256)) ?? (opt.Values.Count > 0 ? opt.Values[0].Value : null);
        }
        OptionsChanged?.Invoke();
    }

    private void ParseOptionsV2(retro_core_options_v2* v2)
    {
        var cats = new Dictionary<string, string>();
        // category: key, desc, info
        for (var c = (byte*)v2->categories; c != null && PtrAt(c, 0) != null; c += 3 * PtrSize) cats[S(PtrAt(c, 0))!] = S(PtrAt(c, 1)) ?? S(PtrAt(c, 0))!;
        // definition: key, desc, desc_categorized, info, info_categorized, category_key, values[128], default_value
        var stride = (6 + 256 + 1) * PtrSize;
        for (var d = (byte*)v2->definitions; d != null && PtrAt(d, 0) != null; d += stride)
        {
            var opt = GetOrAddOption(S(PtrAt(d, 0))!);
            var catKey = S(PtrAt(d, 5));
            opt.Category = catKey != null && cats.TryGetValue(catKey, out var cn) ? cn : catKey;
            opt.Description = (catKey != null ? S(PtrAt(d, 2)) : null) ?? S(PtrAt(d, 1)) ?? opt.Key;
            opt.Info = (catKey != null ? S(PtrAt(d, 4)) : null) ?? S(PtrAt(d, 3));
            opt.Values.Clear();
            for (var i = 0; i < 128 && PtrAt(d, 6 + i * 2) != null; i++) opt.Values.Add((S(PtrAt(d, 6 + i * 2))!, S(PtrAt(d, 7 + i * 2)) ?? S(PtrAt(d, 6 + i * 2))!));
            opt.DefaultValue = S(PtrAt(d, 6 + 256)) ?? (opt.Values.Count > 0 ? opt.Values[0].Value : null);
        }
        OptionsChanged?.Invoke();
    }

    // ------------------------------------------------------------------ dispose
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { UnloadGame(); Deinit(); } catch { }
        foreach (var p in _nativeStrings) Marshal.FreeCoTaskMem(p);
        _nativeStrings.Clear();
        if (_lib != IntPtr.Zero) { NativeLibrary.Free(_lib); _lib = IntPtr.Zero; }
        if (t_current == this) t_current = null;
        if (s_current == this) s_current = null;
    }
}

/// <summary>Tiny printf formatter used for the libretro log callback (integer / string / pointer arguments only).</summary>
public static class PrintfLite
{
    public static string Strip(string fmt) => System.Text.RegularExpressions.Regex.Replace(fmt, "%[-+ 0#]*\\d*(\\.\\d+)?(hh|h|ll|l|z|j|t|L)?[diuoxXfFeEgGcspn%]", m => m.Value == "%%" ? "%" : "?");

    public static unsafe string Format(string fmt, params IntPtr[] args)
    {
        var sb = new StringBuilder(fmt.Length + 32);
        var ai = 0;
        for (var i = 0; i < fmt.Length; i++)
        {
            var ch = fmt[i];
            if (ch != '%') { sb.Append(ch); continue; }
            var j = i + 1;
            while (j < fmt.Length && "-+ 0#.0123456789".IndexOf(fmt[j]) >= 0) j++;
            while (j < fmt.Length && "hlzjtL".IndexOf(fmt[j]) >= 0) j++;
            if (j >= fmt.Length) break;
            var conv = fmt[j];
            i = j;
            if (conv == '%') { sb.Append('%'); continue; }
            var arg = ai < args.Length ? args[ai++] : IntPtr.Zero;
            switch (conv)
            {
                case 's': sb.Append(arg == IntPtr.Zero ? "(null)" : Marshal.PtrToStringUTF8(arg)); break;
                case 'c': sb.Append((char)(byte)arg); break;
                case 'd': case 'i': sb.Append(fmt[i - 1] is 'l' or 'z' or 'j' ? (long)arg : (int)(long)arg); break;
                case 'u': sb.Append(fmt[i - 1] is 'l' or 'z' or 'j' ? (ulong)(long)arg : (uint)(long)arg); break;
                case 'x': sb.Append(((ulong)(long)arg & (fmt[i - 1] is 'l' or 'z' or 'j' ? ulong.MaxValue : 0xFFFFFFFF)).ToString("x")); break;
                case 'X': sb.Append(((ulong)(long)arg & (fmt[i - 1] is 'l' or 'z' or 'j' ? ulong.MaxValue : 0xFFFFFFFF)).ToString("X")); break;
                case 'p': sb.Append("0x").Append(((long)arg).ToString("x")); break;
                case 'f': case 'F': case 'g': case 'G': case 'e': case 'E':
                {
                    long bits = (long)arg;
                    if (IntPtr.Size == 4) { var hi = ai < args.Length ? args[ai++] : IntPtr.Zero; bits = (uint)(long)arg | ((long)(uint)(long)hi << 32); }
                    sb.Append(BitConverter.Int64BitsToDouble(bits).ToString("0.###")); break;
                }
                default: sb.Append('?'); break;
            }
        }
        return sb.ToString();
    }
}
