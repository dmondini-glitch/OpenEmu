using OpenEmu.Core.Config;
using OpenEmu.Core.Cores;
using OpenEmu.Core.Systems;

namespace OpenEmu.Core.Bios;

public sealed record BiosStatus(FirmwareEntry Firmware, CoreDefinition Core, SystemDefinition System, bool Present, string ExpectedPath)
{
    /// <summary>The core boots without this file (HLE BIOS or built-in replacement).</summary>
    public bool CoreHasHle => FreeSystemFiles.CanRunWithoutBios(Core.Id);
    /// <summary>The file present is the open-source OpenBIOS replacement rather than a Sony dump.</summary>
    public bool IsOpenBios => Present && FreeSystemFiles.OpenBiosTargets.Contains(global::System.IO.Path.GetFileName(ExpectedPath), StringComparer.OrdinalIgnoreCase) && FreeSystemFiles.IsOpenBios(ExpectedPath);
}

/// <summary>Tracks BIOS / firmware files required by cores (the libretro "system directory").</summary>
public sealed class BiosManager
{
    public string BiosDir { get; }
    public BiosManager(string? biosDir = null) => BiosDir = biosDir ?? Paths.BiosDir;

    public IReadOnlyList<BiosStatus> Status(IEnumerable<SystemDefinition>? systems = null)
    {
        var list = new List<BiosStatus>();
        var seen = new HashSet<string>();
        foreach (var sys in systems ?? SystemCatalog.All)
            foreach (var coreId in sys.Cores)
            {
                var core = CoreManifest.Find(coreId);
                if (core == null) continue;
                foreach (var fw in core.Firmware)
                {
                    var key = sys.Id + "|" + fw.Path;
                    if (!seen.Add(key)) continue;
                    var path = Path.Combine(BiosDir, fw.Path.Replace('/', Path.DirectorySeparatorChar));
                    list.Add(new BiosStatus(fw, core, sys, File.Exists(path), path));
                }
            }
        return list;
    }

    /// <summary>Required files that are missing AND that the core cannot do without (HLE cores are never blocked).</summary>
    public IReadOnlyList<BiosStatus> Blocking(SystemDefinition system, CoreDefinition core)
        => FreeSystemFiles.CanRunWithoutBios(core.Id) ? Array.Empty<BiosStatus>() : MissingRequired(system, core);

    public IReadOnlyList<BiosStatus> MissingRequired(SystemDefinition system, CoreDefinition core)
        => core.RequiredFirmware.Select(fw =>
        {
            var path = Path.Combine(BiosDir, fw.Path.Replace('/', Path.DirectorySeparatorChar));
            return new BiosStatus(fw, core, system, File.Exists(path), path);
        }).Where(s => !s.Present).ToList();

    /// <summary>Copies a user-provided file into the BIOS folder if its name matches a known firmware entry.</summary>
    public BiosStatus? Import(string file)
    {
        var name = Path.GetFileName(file);
        foreach (var st in Status())
        {
            if (!string.Equals(Path.GetFileName(st.Firmware.Path), name, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(st.ExpectedPath)!);
            File.Copy(file, st.ExpectedPath, true);
            return st with { Present = true };
        }
        return null;
    }
}
