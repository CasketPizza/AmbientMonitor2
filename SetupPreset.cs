using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmbientMonitor;

public sealed class SetupPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled setup";
    public ShortcutDefinition Shortcut { get; set; } = new();
    public SetupSnapshot Setup { get; set; } = new();
    public ExecutableAutomation Automation { get; set; } = new();
    public override string ToString() => Name;
}

public sealed class ExecutableAutomation
{
    public bool Enabled { get; set; }
    public string ExecutablePath { get; set; } = "";
    public List<string> ExecutablePaths { get; set; } = new();
    public double ExitDelaySeconds { get; set; }
    [JsonIgnore] public IReadOnlyList<string> Paths => new[] { ExecutablePath }.Concat(ExecutablePaths ?? new())
        .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

public sealed class SetupSnapshot
{
    public CanvasTargetMode TargetMode { get; set; }
    public List<DisplayProfile> Displays { get; set; } = new();
    public List<DisplayProfile> Canvases { get; set; } = new();
    public SetupSnapshot Copy() => JsonSerializer.Deserialize<SetupSnapshot>(JsonSerializer.Serialize(this))!;
    public string Fingerprint() => JsonSerializer.Serialize(this);
}

internal static class SettingsStorage
{
    internal static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, path, overwrite: true); break; }
                catch (Exception ex) when (attempt < 3 &&
                    ((ex is IOException && (ex.HResult & 0xFFFF) is 32 or 33) || ex is UnauthorizedAccessException))
                {
                    Thread.Sleep(15 * (attempt + 1)); // brief sharing/lock contention from a reader or indexer
                }
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.TraceWarning("Settings temporary cleanup: {0}", ex.Message); }
        }
    }
}
