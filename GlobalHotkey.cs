using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AmbientMonitor;

internal sealed class GlobalHotkey : IDisposable
{
    private readonly nint _window;
    private readonly HwndSource _source;
    private readonly Action _pressed;
    private int _registeredId;
    private int _key;
    private uint _modifiers;
    private static int _nextId = 0x1000;
    private readonly int _firstId = Interlocked.Increment(ref _nextId);
    private readonly int _secondId = Interlocked.Increment(ref _nextId);

    public GlobalHotkey(nint window, Action pressed)
    {
        _window = window;
        _pressed = pressed;
        _source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException("Shortcut window is not ready.");
        _source.AddHook(WindowMessage);
    }

    public bool TrySet(ShortcutDefinition shortcut, out string error)
    {
        error = "";
        if (!shortcut.IsValid) { error = "Use a modifier (Ctrl, Alt, Shift or Win) plus another key."; return false; }
        if (!shortcut.IsConfigured) { Suspend(); return true; }
        if (_registeredId != 0 && _key == shortcut.VirtualKey && _modifiers == shortcut.Modifiers) return true;
        int nextId = _registeredId == _firstId ? _secondId : _firstId;
        // Register the replacement first so a failed change doesn't discard the old shortcut.
        if (!RegisterHotKey(_window, nextId, shortcut.Modifiers | 0x4000, (uint)shortcut.VirtualKey))
        {
            int code = Marshal.GetLastWin32Error();
            error = code == 1409 ? "That shortcut is already in use. Try another combination." : $"Could not register shortcut: {new Win32Exception(code).Message}";
            return false;
        }
        Suspend();
        _registeredId = nextId;
        _key = shortcut.VirtualKey;
        _modifiers = shortcut.Modifiers;
        return true;
    }

    public void Suspend()
    {
        if (_registeredId != 0) UnregisterHotKey(_window, _registeredId);
        _registeredId = 0;
    }
    private nint WindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && (int)wParam == _registeredId && _registeredId != 0)
        {
            handled = true;
            _pressed();
        }
        return 0;
    }
    public void Dispose() { Suspend(); if (!_source.IsDisposed) _source.RemoveHook(WindowMessage); }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
