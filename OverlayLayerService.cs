using System.Windows.Threading;

namespace AmbientMonitor;

internal sealed class OverlayLayerService : IDisposable
{
    private readonly IWindowLayerPlatform _platform;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refresh;
    private readonly Dictionary<nint, Target> _targets = new();
    private IDisposable? _events;
    private int _queued;
    private volatile bool _disposed;
    public event Action<nint, string?>? NoticeChanged;

    public OverlayLayerService(Dispatcher dispatcher, IWindowLayerPlatform? platform = null)
    {
        _dispatcher = dispatcher;
        _platform = platform ?? new WindowLayerPlatform();
        _refresh = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _refresh.Tick += (_, _) => { _refresh.Stop(); Refresh(); };
    }
    public void Register(nint window, OverlayLayer layer, bool enabled)
    {
        _targets[window] = new Target(layer, enabled);
        Apply(window, _targets[window], _platform.Snapshot());
        UpdateEvents();
    }
    public void Reapply(nint window)
    {
        if (!_disposed && _targets.TryGetValue(window, out var target) && target.Enabled)
            Apply(window, target, _platform.Snapshot());
    }
    public void Update(nint window, OverlayLayer layer, bool enabled)
    {
        if (!_targets.TryGetValue(window, out var target)) return;
        bool changed = target.Layer != layer || target.Enabled != enabled;
        target.Layer = layer; target.Enabled = enabled;
        if (changed && enabled) Apply(window, target, _platform.Snapshot());
        UpdateEvents();
    }
    public void Unregister(nint window) { _targets.Remove(window); UpdateEvents(); }
    private void UpdateEvents()
    {
        bool watch = _targets.Values.Any(t => t.Enabled && t.Layer != OverlayLayer.Normal);
        if (watch && _events is null)
        {
            try { _events = _platform.Watch(Schedule); }
            catch (Exception ex)
            {
                foreach (var item in _targets) SetNotice(item.Key, item.Value, $"Window-order monitoring unavailable: {ex.Message}");
            }
        }
        else if (!watch)
        {
            _events?.Dispose(); _events = null;
            _refresh.Stop();
        }
    }
    private void Schedule()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _queued, 1) != 0) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            if (!_disposed && _events is not null && !_refresh.IsEnabled) _refresh.Start();
        }));
    }
    internal void Refresh()
    {
        if (_disposed || _targets.Count == 0) return;
        var snapshot = _platform.Snapshot();
        foreach (var item in _targets)
            if (item.Value.Enabled && item.Value.Layer != OverlayLayer.Normal) Apply(item.Key, item.Value, snapshot);
    }
    private void Apply(nint window, Target target, LayerSnapshot snapshot)
    {
        if (!target.Enabled || !_platform.IsValid(window)) return;
        try
        {
            var overlays = _targets.Keys.ToHashSet();
            var plan = LayerPlacementPolicy.Decide(target.Layer, snapshot, overlays);
            bool restored = plan.RestoreDesktop && _platform.IsHiddenOrMinimized(window);
            if (restored) _platform.ShowWithoutActivation(window);
            bool currentlyTopmost = _platform.IsTopmost(window);
            if (currentlyTopmost != plan.Topmost) _platform.SetOrder(window, plan.Topmost ? -1 : -2);
            // Transient topmost windows (menus/tooltips) can change bands between
            // enumeration and placement. Never demote the overlay through a stale anchor.
            if (plan.InsertAfter > 1 && (!_platform.IsValid(plan.InsertAfter) || _platform.IsTopmost(plan.InsertAfter) != plan.Topmost))
            {
                snapshot = _platform.Snapshot();
                plan = LayerPlacementPolicy.Decide(target.Layer, snapshot, overlays);
            }
            // Avoid repeated SetWindowPos calls when the target already occupies its
            // intended place. This also avoids fighting other widgets' own policies.
            int current = snapshot.Windows.ToList().FindIndex(w => w.Handle == window);
            bool alreadyPlaced = current >= 0 && (plan.InsertAfter == -1
                ? !snapshot.Windows.Take(current).Any(w => w.Visible && !overlays.Contains(w.Handle))
                : plan.InsertAfter > 1 && snapshot.Windows.Take(current).LastOrDefault(w => w.Visible && !overlays.Contains(w.Handle))?.Handle == plan.InsertAfter);
            if (restored || !alreadyPlaced || currentlyTopmost != plan.Topmost || target.Layer == OverlayLayer.Normal)
                _platform.SetOrder(window, plan.InsertAfter);
            if (_platform.IsTopmost(window) != plan.Topmost)
            {
                _platform.SetOrder(window, plan.Topmost ? -1 : -2);
                Schedule();
            }
            SetNotice(window, target, plan.Notice);
        }
        catch (Exception ex) { SetNotice(window, target, $"Could not apply layer: {ex.Message}"); }
    }
    private void SetNotice(nint window, Target target, string? notice)
    {
        if (target.Notice == notice) return;
        target.Notice = notice;
        NoticeChanged?.Invoke(window, notice);
    }
    public string? GetNotice(nint window) => _targets.TryGetValue(window, out var target) ? target.Notice : null;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _events?.Dispose(); _events = null;
        _refresh.Stop();
        _targets.Clear();
    }
    private sealed class Target(OverlayLayer layer, bool enabled)
    {
        public OverlayLayer Layer = layer;
        public bool Enabled = enabled;
        public string? Notice;
    }
}
