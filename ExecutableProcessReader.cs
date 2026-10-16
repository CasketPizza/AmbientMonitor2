using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AmbientMonitor;

internal sealed record RunningExecutable(int ProcessId, string Path);
internal static class ExecutableProcessReader
{
    internal static IReadOnlyList<RunningExecutable> Read(IEnumerable<string> executablePaths)
    {
        var results = new List<RunningExecutable>();
        foreach (var name in executablePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        int id = process.Id;
                        string? path = QueryPath(id);
                        if (path is not null) results.Add(new RunningExecutable(id, path));
                    }
                    catch (InvalidOperationException) { } // process exited while enumerating
                }
            }
        }
        return results;
    }
    internal static string? QueryPath(int processId)
    {
        nint process = OpenProcess(0x1000, false, processId); // query limited information; no module enumeration
        if (process == 0) return null;
        try
        {
            var path = new StringBuilder(32768); int length = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
        }
        finally { CloseHandle(process); }
    }
    internal static bool Matches(string executable, string running)
    {
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(running)) return false;
        try { return string.Equals(Path.GetFullPath(executable), Path.GetFullPath(running), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
