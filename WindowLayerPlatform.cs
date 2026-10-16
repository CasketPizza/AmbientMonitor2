using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AmbientMonitor;

internal interface IWindowLayerPlatform
{
    LayerSnapshot Snapshot();
    bool IsValid(nint window);
    bool IsTopmost(nint window);
    bool IsHiddenOrMinimized(nint window);
    void ShowWithoutActivation(nint window);
    void SetOrder(nint window, nint insertAfter);
    IDisposable Watch(Action changed);
}

internal sealed class WindowLayerPlatform : IWindowLayerPlatform
{
    public LayerSnapshot Snapshot()
    {
        var windows = new List<LayerWindow>();
        var className = new StringBuilder(128);
        EnumWindows((window, _) =>
        {
            bool visible = IsWindowVisible(window) && !IsIconic(window);
            if (visible && DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) visible = false;
            GetClassName(window, className, className.Capacity);
            bool shellClass = className.ToString() is "Progman" or "WorkerW";
            bool desktop = shellClass && FindWindowEx(window, 0, "SHELLDLL_DefView", null) != 0;
            windows.Add(new LayerWindow(window, IsTopmost(window), visible, desktop));
            return true;
        }, 0);
        var foreground = GetForegroundWindow();
        return new LayerSnapshot(windows, foreground == 0 ? 0 : GetAncestor(foreground, 2));
    }
    public bool IsValid(nint window) => IsWindow(window);
    public bool IsTopmost(nint window) => (GetWindowLongPtr(window, -20).ToInt64() & 0x8) != 0;
    public bool IsHiddenOrMinimized(nint window) => !IsWindowVisible(window) || IsIconic(window);
    public void ShowWithoutActivation(nint window) => ShowWindow(window, 4);
    public void SetOrder(nint window, nint insertAfter)
    {
        // Preserve geometry, owner ordering and keyboard focus.
        if (!SetWindowPos(window, insertAfter, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200 | 0x0400))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public IDisposable Watch(Action changed) => new WindowEvents(changed);

    private sealed class WindowEvents : IDisposable
    {
        private readonly WinEventProc _callback;
        private readonly nint _foregroundHook, _orderHook;
        private readonly HashSet<nint> _topLevels = new();
        public WindowEvents(Action changed)
        {
            EnumWindows((window, _) => { _topLevels.Add(window); return true; }, 0);
            _callback = (_, eventId, window, objectId, childId, _, _) =>
            {
                if (window == 0 || objectId != 0 || childId != 0) return;
                // Only top-level changes matter. Ignore child/control accessibility events.
                if (eventId == 0x8001)
                {
                    if (_topLevels.Remove(window)) changed();
                }
                else if (GetAncestor(window, 2) == window)
                {
                    _topLevels.Add(window);
                    changed();
                }
            };
            const uint flags = 2; // OUTOFCONTEXT | SKIPOWNPROCESS: no injected DLL or feedback loop.
            _foregroundHook = SetWinEventHook(3, 3, 0, _callback, 0, 0, flags);
            _orderHook = SetWinEventHook(0x8000, 0x8004, 0, _callback, 0, 0, flags);
            if (_foregroundHook == 0 || _orderHook == 0)
            {
                int error = Marshal.GetLastWin32Error();
                Dispose();
                throw new Win32Exception(error);
            }
        }
        public void Dispose()
        {
            if (_foregroundHook != 0) UnhookWinEvent(_foregroundHook);
            if (_orderHook != 0) UnhookWinEvent(_orderHook);
            GC.KeepAlive(_callback);
        }
    }
    private delegate bool EnumWindowProc(nint window, nint parameter);
    private delegate void WinEventProc(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
