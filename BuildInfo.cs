using System.Reflection;

namespace AmbientMonitor;

internal static class BuildInfo
{
    private static readonly Assembly ProductAssembly = typeof(BuildInfo).Assembly;
    public static string Version { get; } = ProductAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? ProductAssembly.GetName().Version?.ToString(3) ?? "2.0.0";
    public static string Number { get; } = Metadata("BuildNumber");
    public static string Configuration { get; } = Metadata("BuildConfiguration");
    public static string DisplayLabel => $"v{Version} · build {Number}";
    public static string Description => $"Ambient Monitor {Version}\nBuild {Number} · {Configuration}";
    private static string Metadata(string key) => ProductAssembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? "unknown";
}
