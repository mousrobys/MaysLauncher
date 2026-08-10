using System.IO;
using System.IO.Compression;
using System.Text.Json;
using MCLauncher.Models;

namespace MCLauncher.Services;

public class InstanceExportImport
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string ExportInstance(GameInstance instance, string outputPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mca-export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var manifest = new InstanceManifest
            {
                Name = instance.Name,
                McVersion = instance.McVersion,
                Loader = instance.Loader,
                LoaderVersion = instance.LoaderVersion,
                MaxMemoryMb = instance.MaxMemoryMb,
                ExtraJvmArgs = instance.ExtraJvmArgs,
                Notes = instance.Notes,
                ExportedAt = DateTime.UtcNow
            };

            File.WriteAllText(Path.Combine(tempDir, "manifest.json"), JsonSerializer.Serialize(manifest, Opts));

            var modsDir = Path.Combine(tempDir, "mods");
            var srcModsDir = InstanceService.ModsDir(instance);
            if (Directory.Exists(srcModsDir))
            {
                Directory.CreateDirectory(modsDir);
                foreach (var file in Directory.GetFiles(srcModsDir, "*.jar"))
                    File.Copy(file, Path.Combine(modsDir, Path.GetFileName(file)));
            }

            if (File.Exists(outputPath)) File.Delete(outputPath);
            ZipFile.CreateFromDirectory(tempDir, outputPath, CompressionLevel.Optimal, false);

            return outputPath;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    public static InstanceManifest? ReadManifest(string archivePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var entry = zip.GetEntry("manifest.json");
            if (entry == null) return null;
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<InstanceManifest>(stream);
        }
        catch { return null; }
    }

    public static bool ImportInstance(string archivePath, string instanceId)
    {
        try
        {
            var manifest = ReadManifest(archivePath);
            if (manifest == null) return false;

            var tempDir = Path.Combine(Path.GetTempPath(), "mca-import-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);

            try
            {
                ZipFile.ExtractToDirectory(archivePath, tempDir);

                var instanceDir = Path.Combine(InstanceService.InstancesRoot, instanceId);
                Directory.CreateDirectory(instanceDir);

                var modsSrc = Path.Combine(tempDir, "mods");
                var modsDst = Path.Combine(instanceDir, "mods");
                if (Directory.Exists(modsSrc))
                {
                    Directory.CreateDirectory(modsDst);
                    foreach (var file in Directory.GetFiles(modsSrc, "*.jar"))
                        File.Copy(file, Path.Combine(modsDst, Path.GetFileName(file)), true);
                }

                return true;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        catch { return false; }
    }
}

public class InstanceManifest
{
    public string Name { get; set; } = "";
    public string McVersion { get; set; } = "";
    public LoaderKind Loader { get; set; }
    public string LoaderVersion { get; set; } = "";
    public int MaxMemoryMb { get; set; }
    public string ExtraJvmArgs { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime ExportedAt { get; set; }
}
