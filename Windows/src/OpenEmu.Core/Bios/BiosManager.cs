using OpenEmu.Core.Config;
using OpenEmu.Core.Cores;
using OpenEmu.Core.Systems;

namespace OpenEmu.Core.Bios;

public sealed record BiosStatus(FirmwareEntry Firmware, CoreDefinition Core, SystemDefinition System, bool Present, string ExpectedPath);

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
