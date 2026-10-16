using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace AmbientMonitor;

internal sealed record ImportedProfile(SetupPreset Preset, string AssetDirectory)
{
    internal void RemoveAssets() { if (Directory.Exists(AssetDirectory)) Directory.Delete(AssetDirectory, recursive: true); }
}
internal static class ProfilePackage
{
    private sealed class Manifest { public int Version { get; set; } = 1; public SetupPreset Preset { get; set; } = new(); }
    private const long MaxAsset = 64 * 1024 * 1024, MaxTotal = 256 * 1024 * 1024;
    private static IEnumerable<DisplayProfile> Targets(SetupPreset preset) => preset.Setup.Displays.Concat(preset.Setup.Canvases);
    internal static void Export(SetupPreset original, string path)
    {
        var preset = JsonSerializer.Deserialize<SetupPreset>(JsonSerializer.Serialize(original))!;
        var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contents = new Dictionary<string, byte[]>(); long total = 0;
        foreach (var target in Targets(preset).Where(t => !string.IsNullOrWhiteSpace(t.ImagePath)))
        {
            string image = Path.GetFullPath(target.ImagePath);
            if (!images.TryGetValue(image, out string? entry))
            {
                if (new FileInfo(image).Length > MaxAsset) throw new InvalidDataException("An image exceeds the portable package's 64 MB asset limit.");
                byte[] data = File.ReadAllBytes(image);
                ValidateImage(data);
                entry = "assets/" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() + Path.GetExtension(image).ToLowerInvariant();
                if (!contents.ContainsKey(entry)) { total += data.LongLength; contents[entry] = data; }
                if (total > MaxTotal || contents.Count > 256) throw new InvalidDataException("Package assets exceed 256 MB or 256 images.");
                images[image] = entry;
            }
            target.ImagePath = entry;
        }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temporary))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (var stream = zip.CreateEntry("profile.json").Open()) JsonSerializer.Serialize(stream, new Manifest { Preset = preset });
                foreach (var asset in contents)
                    using (var stream = zip.CreateEntry(asset.Key, CompressionLevel.Optimal).Open()) stream.Write(asset.Value);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static ImportedProfile Import(string path, string assetRoot)
    {
        using var file = File.OpenRead(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count > 257 || zip.Entries.Any(e => e.FullName.StartsWith('/') || e.FullName.Contains('\\') || e.FullName.Split('/').Any(p => p is ".." or ".")))
            throw new InvalidDataException("Invalid portable package entry names/count.");
        var manifestEntry = zip.GetEntry("profile.json") ?? throw new InvalidDataException("Package has no profile.json.");
        using var manifestData = ReadEntry(manifestEntry, 4 * 1024 * 1024);
        var manifest = JsonSerializer.Deserialize<Manifest>(manifestData) ?? throw new InvalidDataException("Invalid profile manifest.");
        if (manifest.Version != 1 || manifest.Preset?.Setup?.Displays is null || manifest.Preset.Setup.Canvases is null)
            throw new InvalidDataException("Unsupported or incomplete profile package.");
        var preset = manifest.Preset;
        if (preset.Setup.Displays.Count + preset.Setup.Canvases.Count > 512 || !Enum.IsDefined(preset.Setup.TargetMode) || preset.Setup.Canvases.Any(p => p.Canvas is not { IsValid: true }))
            throw new InvalidDataException("Invalid package targets.");
        preset.Id = Guid.NewGuid().ToString("N"); preset.Shortcut = new(); preset.Automation ??= new(); preset.Automation.Enabled = false;
        preset.Automation.ExecutablePaths ??= new();
        preset.Automation.ExitDelaySeconds = double.IsFinite(preset.Automation.ExitDelaySeconds) ? Math.Clamp(preset.Automation.ExitDelaySeconds, 0, 30) : 0;
        foreach (var canvas in preset.Setup.Canvases) canvas.DeviceName = "canvas-" + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(assetRoot, preset.Id);
        var imported = new ImportedProfile(preset, directory);
        var restored = new Dictionary<string, string>(); long total = 0;
        try
        {
            foreach (var target in Targets(preset).Where(t => !string.IsNullOrWhiteSpace(t.ImagePath)))
            {
                string entryName = target.ImagePath;
                if (!entryName.StartsWith("assets/", StringComparison.Ordinal) || entryName.Count(c => c == '/') != 1 || entryName.Contains('\\') || entryName.Contains(':') || entryName.Contains(".."))
                    throw new InvalidDataException("Image reference is not a bundled asset.");
                if (!restored.TryGetValue(entryName, out string? destination))
                {
                    var entry = zip.GetEntry(entryName) ?? throw new InvalidDataException("Referenced image is missing from the package.");
                    using var data = ReadEntry(entry, MaxAsset);
                    total += data.Length; if (total > MaxTotal) throw new InvalidDataException("Package assets exceed 256 MB.");
                    ValidateImage(data.ToArray());
                    Directory.CreateDirectory(directory);
                    destination = Path.Combine(directory, Path.GetFileName(entryName));
                    using (var output = File.Create(destination)) { data.Position = 0; data.CopyTo(output); }
                    restored[entryName] = destination;
                }
                target.ImagePath = destination;
            }
            return imported;
        }
        catch { imported.RemoveAssets(); throw; }
    }
    private static MemoryStream ReadEntry(ZipArchiveEntry entry, long limit)
    {
        if (entry.Length > limit) throw new InvalidDataException("Package entry exceeds its size limit.");
        var result = new MemoryStream();
        try
        {
            using var stream = entry.Open(); var buffer = new byte[81920]; int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                if (result.Length + count > limit) throw new InvalidDataException("Package entry exceeds its size limit.");
                result.Write(buffer, 0, count);
            }
            result.Position = 0; return result;
        }
        catch { result.Dispose(); throw; }
    }
    private static void ValidateImage(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth > 16384 || frame.PixelHeight > 16384 || (long)frame.PixelWidth * frame.PixelHeight > 64_000_000)
            throw new InvalidDataException("Portable images must be at most 16384 pixels per side and 64 million pixels.");
    }
}
