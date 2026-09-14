using Avalonia.Controls;
using OpenEmu.App.Views;
using OpenEmu.Core.Config;
using OpenEmu.Core.Cores;
using OpenEmu.Core.Emulation;
using OpenEmu.Core.Library;
using OpenEmu.Core.Libretro;
using OpenEmu.Core.Remote;
using OpenEmu.Core.Localization;
using OpenEmu.Core.Systems;

namespace OpenEmu.App.Services;

/// <summary>Resolves system → core → BIOS and opens a game window.</summary>
public sealed class GameLauncher
{
    private readonly AppServices _s;
    public List<GameWindow> OpenWindows { get; } = new();
    public GameLauncher(AppServices s) => _s = s;

    /// <summary>Cores run out-of-process when this process cannot load them (Windows ARM64) or when the user opted in.</summary>
    public bool UseCoreHost(CoreDefinition core) => CoreManifest.RequiresCoreHost || _s.Settings.RunCoresOutOfProcess;

    /// <summary>User options win; underneath them the open-source-firmware defaults (HLE BIOS, AltirraOS…) are applied.</summary>
    private Dictionary<string, string> MergeOptions(string coreId)
    {
        var o = Core.Bios.FreeSystemFiles.DefaultCoreOptions(coreId);
        if (_s.Settings.CoreOptions.TryGetValue(coreId, out var user)) foreach (var kv in user) o[kv.Key] = kv.Value;
        return o;
    }

    public async Task<GameWindow?> LaunchAsync(Game game, Window owner, string? coreId = null)
    {
        var system = SystemCatalog.Find(game.SystemId);
        if (system == null) { await Dialogs.Message(owner, L.T("common.error"), $"Unknown system {game.SystemId}"); return null; }
        if (!File.Exists(game.RomPath)) { await Dialogs.Message(owner, L.T("common.error"), $"{L.T("library.missing")}: {game.RomPath}"); return null; }

        var core = (coreId ?? game.CoreOverride) is { } cid ? CoreManifest.Find(cid) : null;
        core ??= _s.Cores.PreferredCore(system, _s.Settings);
        if (core == null) { await Dialogs.Message(owner, L.T("common.error"), $"No core available for {system.Name}"); return null; }

        if (UseCoreHost(core) && core.HwRender)
        {
            // OpenGL cores need an in-process GL context; offer a software alternative if the system has one.
            var alt = _s.Cores.CoresForSystem(system, _s.Settings).FirstOrDefault(c => !c.HwRender);
            if (alt == null || coreId != null) { await Dialogs.Message(owner, L.T("common.error"), L.T("play.hwCoreRemote", core.Title, CoreManifest.NativePlatformKey)); return null; }
            core = alt;
        }
        if (UseCoreHost(core) && RemoteSession.FindHost(CoreManifest.HostArchitecture(CoreManifest.PlatformKey)) == null)
        {
            await Dialogs.Message(owner, L.T("common.error"), L.T("play.hostMissing", CoreManifest.HostArchitecture(CoreManifest.PlatformKey)));
            return null;
        }
        var lib = _s.Cores.FindLibrary(core.Id);
        if (lib == null)
        {
            if (!await Dialogs.Confirm(owner, L.T("prefs.cores"), L.T("play.coreNotInstalled", core.Title))) return null;
            try { lib = await Dialogs.RunWithProgress(owner, L.T("notify.installing", core.Title), p => _s.Cores.InstallAsync(core.Id, p)); }
            catch (Exception ex) { await Dialogs.Message(owner, L.T("common.error"), L.T("notify.installFailed", core.Title, ex.Message)); return null; }
        }

        var missing = _s.Bios.Blocking(system, core);
        if (missing.Count > 0)
        {
            // Prefer a core of the same system that boots without the proprietary BIOS (HLE), like PCSX-ReARMed for PlayStation.
            var hle = coreId == null ? _s.Cores.CoresForSystem(system, _s.Settings).FirstOrDefault(c => Core.Bios.FreeSystemFiles.CanRunWithoutBios(c.Id) && _s.Cores.IsInstalled(c.Id) && !(UseCoreHost(c) && c.HwRender)) : null;
            if (hle != null) { core = hle; lib = _s.Cores.FindLibrary(core.Id)!; }
            else
            {
                var list = string.Join("\n", missing.Select(m => $"• {m.Firmware.Path} – {m.Firmware.Description}"));
                await Dialogs.Message(owner, L.T("prefs.bios"), L.T("play.missingBios", system.Name, list) + "\n\n" + L.T("prefs.bios.copyright"));
                return null;
            }
        }

        var opts = new SessionOptions
        {
            System = system, Core = core, CoreLibraryPath = lib, RomPath = game.RomPath, GameKey = game.GameKey,
            CoreOptions = MergeOptions(core.Id),
            Language = _s.Settings.Language == "pt-BR" ? Retro.LanguagePortugueseBrazil : Retro.LanguageEnglish,
        };
        var win = new GameWindow(game, opts);
        OpenWindows.Add(win);
        win.Closed += (_, _) => OpenWindows.Remove(win);
        win.Show();
        return win;
    }
}
