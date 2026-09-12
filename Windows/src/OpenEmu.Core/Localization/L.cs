using System.Reflection;
using System.Text.Json;

namespace OpenEmu.Core.Localization;

/// <summary>Tiny string table: L.T("key") with EN fallback. Languages: en, pt-BR.</summary>
public static class L
{
    private static Dictionary<string, string> s_en = Load("en");
    private static Dictionary<string, string> s_cur = s_en;
    public static string Language { get; private set; } = "en";
    public static IReadOnlyList<(string Code, string Name)> Available { get; } = new[] { ("en", "English"), ("pt-BR", "Português (Brasil)") };
    public static event Action? Changed;

    public static void SetLanguage(string code)
    {
        Language = code;
        s_cur = code == "en" ? s_en : Load(code);
        Changed?.Invoke();
    }

    private static Dictionary<string, string> Load(string code)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"strings.{code}.json");
        if (s == null) return new();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
    }

    public static string T(string key) => s_cur.TryGetValue(key, out var v) ? v : s_en.TryGetValue(key, out var e) ? e : key;
    public static string T(string key, params object[] args) => string.Format(T(key), args);
}
