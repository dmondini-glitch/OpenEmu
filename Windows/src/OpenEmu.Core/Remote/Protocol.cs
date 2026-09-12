using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenEmu.Core.Remote;

/// <summary>JSON-lines protocol between the UI and the core host (named pipe).</summary>
public static class Protocol
{
    public static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public const string Start = "start", Pause = "pause", Resume = "resume", Reset = "reset", Stop = "stop", FastForward = "fastforward", Volume = "volume", Muted = "muted",
        SetOption = "setoption", Cheats = "cheats", Disc = "disc", Key = "key", SaveState = "savestate", LoadState = "loadstate", Ping = "ping", GetOptions = "options";
    public const string EvtState = "state", EvtMessage = "message", EvtLog = "log", EvtNotification = "notification", EvtFps = "fps", EvtDisc = "disc";
}

public sealed class Request { public long Id { get; set; } public string Cmd { get; set; } = ""; public JsonElement? Args { get; set; } }
public sealed class Response { public long Id { get; set; } public bool Ok { get; set; } public string? Error { get; set; } public JsonElement? Result { get; set; } }
public sealed class Event { public string Evt { get; set; } = ""; public JsonElement? Data { get; set; } }

/// <summary>Serializable form of <see cref="Emulation.SessionOptions"/> sent to the host.</summary>
public sealed class StartArgs
{
    public string SystemId { get; set; } = "";
    public string CoreId { get; set; } = "";
    public string CoreLibraryPath { get; set; } = "";
    public string? RomPath { get; set; }
    public string GameKey { get; set; } = "";
    public string BiosDir { get; set; } = "";
    public string SavesDir { get; set; } = "";
    public string StatesDir { get; set; } = "";
    public Dictionary<string, string> CoreOptions { get; set; } = new();
    public uint Language { get; set; }
    public string? BindingsJson { get; set; }
    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }
}

public sealed class StartResult
{
    public Emulation.EmulatorInfo? Info { get; set; }
    public List<Emulation.CoreOptionSnapshot> Options { get; set; } = new();
}
