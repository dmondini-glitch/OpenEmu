namespace OpenEmu.Core.Cheats;

public sealed class Cheat
{
    public long Id { get; set; }
    public long GameId { get; set; }
    public string Description { get; set; } = "";
    /// <summary>Raw code(s); multiple lines are joined with '+' when sent to the core (libretro convention).</summary>
    public string Code { get; set; } = "";
    public bool Enabled { get; set; }
    public string? Type { get; set; }

    /// <summary>Normalizes user input (one code per line, spaces, commas) into the '+'-joined libretro form.</summary>
    public static string NormalizeCode(string raw)
    {
        var parts = raw.Split(new[] { '\n', '\r', ',', ';', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("+", parts);
    }

    public static bool LooksValid(string code)
    {
        var n = NormalizeCode(code);
        return n.Length > 0 && n.All(c => char.IsLetterOrDigit(c) || c is '+' or ':' or '-' or '?');
    }
}
