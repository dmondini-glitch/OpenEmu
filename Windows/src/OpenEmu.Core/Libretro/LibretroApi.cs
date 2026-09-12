// libretro API v1 definitions (subset needed by the OpenEmu for Windows frontend).
// Reference: https://github.com/libretro/libretro-common/blob/master/include/libretro.h
using System.Runtime.InteropServices;

namespace OpenEmu.Core.Libretro;

public static class Retro
{
    public const uint ApiVersion = 1;

    // Devices
    public const uint DeviceNone = 0, DeviceJoypad = 1, DeviceMouse = 2, DeviceKeyboard = 3, DeviceLightgun = 4, DeviceAnalog = 5, DevicePointer = 6;

    // Joypad ids
    public const uint JoypadB = 0, JoypadY = 1, JoypadSelect = 2, JoypadStart = 3, JoypadUp = 4, JoypadDown = 5, JoypadLeft = 6, JoypadRight = 7,
        JoypadA = 8, JoypadX = 9, JoypadL = 10, JoypadR = 11, JoypadL2 = 12, JoypadR2 = 13, JoypadL3 = 14, JoypadR3 = 15, JoypadMask = 256;

    // Analog
    public const uint AnalogIndexLeft = 0, AnalogIndexRight = 1, AnalogIndexButton = 2, AnalogIdX = 0, AnalogIdY = 1;

    // Mouse / pointer / lightgun ids (subset)
    public const uint MouseX = 0, MouseY = 1, MouseLeft = 2, MouseRight = 3;
    public const uint PointerX = 0, PointerY = 1, PointerPressed = 2;

    // Regions / memory
    public const uint RegionNtsc = 0, RegionPal = 1;
    public const uint MemorySaveRam = 0, MemoryRtc = 1, MemorySystemRam = 2, MemoryVideoRam = 3;

    // Pixel formats
    public const int PixelFormat0Rgb1555 = 0, PixelFormatXrgb8888 = 1, PixelFormatRgb565 = 2;

    // HW render context types
    public const uint HwContextNone = 0, HwContextOpenGl = 1, HwContextOpenGlEs2 = 2, HwContextOpenGlCore = 3, HwContextOpenGlEs3 = 4,
        HwContextOpenGlEsVersion = 5, HwContextVulkan = 6, HwContextD3D11 = 7, HwContextD3D10 = 8, HwContextD3D12 = 9, HwContextD3D9 = 10;

    public static readonly IntPtr HwFrameBufferValid = new(-1);

    // Languages
    public const uint LanguageEnglish = 0, LanguagePortugueseBrazil = 12, LanguageSpanish = 3;

    // Log levels
    public const int LogDebug = 0, LogInfo = 1, LogWarn = 2, LogError = 3;

    // Environment commands
    public const uint EnvExperimental = 0x10000, EnvPrivate = 0x20000;
    public const uint EnvSetRotation = 1, EnvGetOverscan = 2, EnvGetCanDupe = 3, EnvSetMessage = 6, EnvShutdown = 7, EnvSetPerformanceLevel = 8,
        EnvGetSystemDirectory = 9, EnvSetPixelFormat = 10, EnvSetInputDescriptors = 11, EnvSetKeyboardCallback = 12, EnvSetDiskControlInterface = 13,
        EnvSetHwRender = 14, EnvGetVariable = 15, EnvSetVariables = 16, EnvGetVariableUpdate = 17, EnvSetSupportNoGame = 18, EnvGetLibretroPath = 19,
        EnvSetFrameTimeCallback = 21, EnvSetAudioCallback = 22, EnvGetRumbleInterface = 23, EnvGetInputDeviceCapabilities = 24,
        EnvGetSensorInterface = 25 | EnvExperimental, EnvGetCameraInterface = 26 | EnvExperimental, EnvGetLogInterface = 27, EnvGetPerfInterface = 28,
        EnvGetLocationInterface = 29, EnvGetCoreAssetsDirectory = 30, EnvGetSaveDirectory = 31, EnvSetSystemAvInfo = 32, EnvSetProcAddressCallback = 33,
        EnvSetSubsystemInfo = 34, EnvSetControllerInfo = 35, EnvSetMemoryMaps = 36 | EnvExperimental, EnvSetGeometry = 37, EnvGetUsername = 38,
        EnvGetLanguage = 39, EnvGetCurrentSoftwareFramebuffer = 40 | EnvExperimental, EnvGetHwRenderInterface = 41 | EnvExperimental,
        EnvSetSupportAchievements = 42 | EnvExperimental, EnvSetHwRenderContextNegotiationInterface = 43 | EnvExperimental,
        EnvSetSerializationQuirks = 44, EnvSetHwSharedContext = 44 | EnvExperimental, EnvGetVfsInterface = 45 | EnvExperimental,
        EnvGetLedInterface = 46 | EnvExperimental, EnvGetAudioVideoEnable = 47 | EnvExperimental, EnvGetMidiInterface = 48 | EnvExperimental,
        EnvGetFastForwarding = 49 | EnvExperimental, EnvGetTargetRefreshRate = 50 | EnvExperimental, EnvGetInputBitmasks = 51 | EnvExperimental,
        EnvGetCoreOptionsVersion = 52, EnvSetCoreOptions = 53, EnvSetCoreOptionsIntl = 54, EnvSetCoreOptionsDisplay = 55, EnvGetPreferredHwRender = 56,
        EnvGetDiskControlInterfaceVersion = 57, EnvSetDiskControlExtInterface = 58, EnvGetMessageInterfaceVersion = 59, EnvSetMessageExt = 60,
        EnvGetInputMaxUsers = 61, EnvSetAudioBufferStatusCallback = 62, EnvSetMinimumAudioLatency = 63, EnvSetFastForwardingOverride = 64,
        EnvSetContentInfoOverride = 65, EnvGetGameInfoExt = 66, EnvSetCoreOptionsV2 = 67, EnvSetCoreOptionsV2Intl = 68,
        EnvSetCoreOptionsUpdateDisplayCallback = 69, EnvSetVariable = 70, EnvGetThrottleState = 71 | EnvExperimental,
        EnvGetSavestateContext = 72 | EnvExperimental, EnvGetHwRenderContextNegotiationInterfaceSupport = 73 | EnvExperimental,
        EnvGetJitCapable = 74, EnvGetMicrophoneInterface = 75 | EnvExperimental, EnvGetDevicePower = 77 | EnvExperimental,
        EnvSetNetpacketInterface = 78, EnvGetPlaylistDirectory = 79, EnvGetFileBrowserStartDirectory = 80;

    public const uint InputDeviceCapJoypad = 1u << 1, InputDeviceCapAnalog = 1u << 5, InputDeviceCapKeyboard = 1u << 3, InputDeviceCapMouse = 1u << 2, InputDeviceCapPointer = 1u << 6;
    public const uint SerializationQuirkIncomplete = 1, SerializationQuirkMustInitialize = 2, SerializationQuirkCoreVariableSize = 4;
    public const int SavestateContextNormal = 0;
    public const int ThrottleNone = 0, ThrottleFrameStepping = 1, ThrottleFastForward = 2, ThrottleSlowMotion = 3, ThrottleRewinding = 4, ThrottleVsync = 5, ThrottleUnblocked = 6;
}

#pragma warning disable CS0649
[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_system_info
{
    public byte* library_name; public byte* library_version; public byte* valid_extensions;
    [MarshalAs(UnmanagedType.I1)] public bool need_fullpath; [MarshalAs(UnmanagedType.I1)] public bool block_extract;
}

[StructLayout(LayoutKind.Sequential)]
public struct retro_game_geometry { public uint base_width, base_height, max_width, max_height; public float aspect_ratio; }

[StructLayout(LayoutKind.Sequential)]
public struct retro_system_timing { public double fps; public double sample_rate; }

[StructLayout(LayoutKind.Sequential)]
public struct retro_system_av_info { public retro_game_geometry geometry; public retro_system_timing timing; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_game_info { public byte* path; public void* data; public nuint size; public byte* meta; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_variable { public byte* key; public byte* value; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_message { public byte* msg; public uint frames; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_message_ext { public byte* msg; public uint duration; public uint priority; public int level; public int target; public int type; public sbyte progress; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_input_descriptor { public uint port, device, index, id; public byte* description; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_log_callback { public delegate* unmanaged[Cdecl]<int, byte*, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> log; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_core_option_value { public byte* value; public byte* label; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_core_option_definition
{
    public byte* key; public byte* desc; public byte* info;
    public fixed byte values[128 * 16]; // retro_core_option_value[128] (2 pointers each on 64-bit)
    public byte* default_value;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_core_options_intl { public retro_core_option_definition* us; public retro_core_option_definition* local; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_core_option_v2_category { public byte* key; public byte* desc; public byte* info; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_core_option_v2_definition
{
    public byte* key; public byte* desc; public byte* desc_categorized; public byte* info; public byte* info_categorized; public byte* category_key;
    public fixed byte values[128 * 16];
    public byte* default_value;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_core_options_v2 { public retro_core_option_v2_category* categories; public retro_core_option_v2_definition* definitions; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_core_options_v2_intl { public retro_core_options_v2* us; public retro_core_options_v2* local; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_core_option_display { public byte* key; [MarshalAs(UnmanagedType.I1)] public bool visible; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_hw_render_callback
{
    public uint context_type;
    public delegate* unmanaged[Cdecl]<void> context_reset;
    public delegate* unmanaged[Cdecl]<nuint> get_current_framebuffer;
    public delegate* unmanaged[Cdecl]<byte*, IntPtr> get_proc_address;
    [MarshalAs(UnmanagedType.I1)] public bool depth;
    [MarshalAs(UnmanagedType.I1)] public bool stencil;
    [MarshalAs(UnmanagedType.I1)] public bool bottom_left_origin;
    public uint version_major, version_minor;
    [MarshalAs(UnmanagedType.I1)] public bool cache_context;
    public delegate* unmanaged[Cdecl]<void> context_destroy;
    [MarshalAs(UnmanagedType.I1)] public bool debug_context;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_controller_description { public byte* desc; public uint id; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_controller_info { public retro_controller_description* types; public uint num_types; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_disk_control_callback
{
    public delegate* unmanaged[Cdecl]<byte, byte> set_eject_state;
    public delegate* unmanaged[Cdecl]<byte> get_eject_state;
    public delegate* unmanaged[Cdecl]<uint> get_image_index;
    public delegate* unmanaged[Cdecl]<uint, byte> set_image_index;
    public delegate* unmanaged[Cdecl]<uint> get_num_images;
    public delegate* unmanaged[Cdecl]<uint, retro_game_info*, byte> replace_image_index;
    public delegate* unmanaged[Cdecl]<byte> add_image_index;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_disk_control_ext_callback
{
    public retro_disk_control_callback basic;
    public delegate* unmanaged[Cdecl]<uint, byte*, byte> set_initial_image;
    public delegate* unmanaged[Cdecl]<uint, byte*, nuint, byte> get_image_path;
    public delegate* unmanaged[Cdecl]<uint, byte*, nuint, byte> get_image_label;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_rumble_interface { public delegate* unmanaged[Cdecl]<uint, uint, ushort, byte> set_rumble_state; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_frame_time_callback { public delegate* unmanaged[Cdecl]<long, void> callback; public long reference; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_audio_callback { public delegate* unmanaged[Cdecl]<void> callback; public delegate* unmanaged[Cdecl]<byte, void> set_state; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_keyboard_callback { public delegate* unmanaged[Cdecl]<byte, uint, uint, ushort, void> callback; }

[StructLayout(LayoutKind.Sequential)]
public struct retro_fastforwarding_override
{
    public float ratio;
    [MarshalAs(UnmanagedType.I1)] public bool fastforward;
    [MarshalAs(UnmanagedType.I1)] public bool notification;
    [MarshalAs(UnmanagedType.I1)] public bool inhibit_toggle;
}

[StructLayout(LayoutKind.Sequential)]
public struct retro_throttle_state { public uint mode; public float rate; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_system_content_info_override
{
    public byte* extensions; [MarshalAs(UnmanagedType.I1)] public bool need_fullpath; [MarshalAs(UnmanagedType.I1)] public bool persistent_data;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct retro_perf_callback
{
    public delegate* unmanaged[Cdecl]<long> get_time_usec;
    public delegate* unmanaged[Cdecl]<ulong> get_cpu_features;
    public delegate* unmanaged[Cdecl]<ulong> get_perf_counter;
    public delegate* unmanaged[Cdecl]<void*, void> perf_register;
    public delegate* unmanaged[Cdecl]<void*, void> perf_start;
    public delegate* unmanaged[Cdecl]<void*, void> perf_stop;
    public delegate* unmanaged[Cdecl]<void> perf_log;
}
#pragma warning restore CS0649

/// <summary>Core option (a.k.a. "variable") exposed by a core.</summary>
public sealed class CoreOption
{
    public required string Key { get; init; }
    public string Description { get; set; } = "";
    public string? Info { get; set; }
    public string? Category { get; set; }
    public List<(string Value, string Label)> Values { get; } = new();
    public string? DefaultValue { get; set; }
    public string? CurrentValue { get; set; }
    public bool Visible { get; set; } = true;
    public string EffectiveValue => CurrentValue ?? DefaultValue ?? (Values.Count > 0 ? Values[0].Value : "");
}

public sealed record InputDescriptor(uint Port, uint Device, uint Index, uint Id, string Description);
public sealed record ControllerDescription(string Description, uint Id);
public sealed record CoreMessage(string Text, uint Frames, int Level = Retro.LogInfo);
