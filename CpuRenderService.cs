using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media.Imaging;

namespace AmbientMonitor;

internal sealed class CpuRenderService : IDisposable
{
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly Dictionary<Guid, Target> _targets = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;
    private Bitmap? _capture;
    private string? _lastStatus;
    public event Action<string>? StatusChanged;

    public CpuRenderService()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Ambient Monitor CPU" };
        _thread.Start();
    }

    public Guid Add(int width, int height, OverlaySettings settings, Action<BitmapSource> present)
    {
        var id = Guid.NewGuid();
        var snapshot = settings.Snapshot();
        Enqueue(() => _targets.Add(id, new Target(width, height, snapshot, present)));
        return id;
    }
    public void Update(Guid id, OverlaySettings settings)
    {
        var snapshot = settings.Snapshot();
        Enqueue(() => { if (_targets.TryGetValue(id, out var target)) { target.Settings = snapshot; target.Dirty = true; } });
    }
    public void Remove(Guid id)
    {
        using var done = new ManualResetEventSlim();
        Enqueue(() => { try { if (_targets.Remove(id, out var target)) target.Processor.Dispose(); } finally { done.Set(); } });
        done.Wait();
    }
    private void Enqueue(Action action) { _commands.Enqueue(action); _wake.Set(); }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        try
        {
            while (!_stopping)
            {
                var started = clock.Elapsed;
                while (_commands.TryDequeue(out var command)) command();
                bool ambient = _targets.Values.Any(t => t.Settings.Enabled && t.Settings.Mode == OverlayMode.Ambient);
                try
                {
                    if (ambient)
                    {
                        var screen = System.Windows.Forms.Screen.PrimaryScreen ?? throw new InvalidOperationException("No primary display is available.");
                        if (_capture is null || _capture.Width != screen.Bounds.Width || _capture.Height != screen.Bounds.Height)
                        {
                            _capture?.Dispose();
                            // Screen DC alpha is undefined. Treat capture as opaque
                            // RGB so GDI+ cannot turn a valid screenshot transparent.
                            _capture = new Bitmap(screen.Bounds.Width, screen.Bounds.Height, PixelFormat.Format32bppRgb);
                        }
                        using var graphics = Graphics.FromImage(_capture);
                        graphics.CopyFromScreen(screen.Bounds.Left, screen.Bounds.Top, 0, 0, screen.Bounds.Size, CopyPixelOperation.SourceCopy);
                    }
                    else { _capture?.Dispose(); _capture = null; }
                    foreach (var target in _targets.Values)
                    {
                        if (!target.Settings.Enabled || (!target.Dirty && target.Settings.Mode == OverlayMode.Veil && !target.HasNoise)) continue;
                        var frame = target.Processor.Process(_capture, target.Settings, target.Width, target.Height, (uint)(clock.Elapsed.TotalSeconds * 30 % 65536));
                        target.Present(frame);
                        target.Dirty = false;
                    }
                    if (_targets.Count > 0) SetStatus("CPU running · GDI capture / software effects");
                }
                catch (Exception ex)
                {
                    SetStatus($"CPU paused; retrying: {ex.Message}");
                    _wake.WaitOne(1000);
                    continue;
                }
                if (!ambient && !_targets.Values.Any(t => t.Settings.Enabled && (t.HasNoise || t.Dirty))) _wake.WaitOne();
                else _wake.WaitOne(Math.Max(1, (int)(33.333 - (clock.Elapsed - started).TotalMilliseconds)));
            }
        }
        finally
        {
            while (_commands.TryDequeue(out var command)) command();
            _capture?.Dispose();
            foreach (var target in _targets.Values) target.Processor.Dispose();
            _targets.Clear();
        }
    }
    private void SetStatus(string status) { if (_lastStatus != status) { _lastStatus = status; StatusChanged?.Invoke(status); } }
    public void Dispose() { _stopping = true; _wake.Set(); _thread.Join(); _wake.Dispose(); }

    private sealed class Target(int width, int height, OverlaySettings settings, Action<BitmapSource> present)
    {
        public int Width = width, Height = height;
        public OverlaySettings Settings = settings;
        public Action<BitmapSource> Present = present;
        public CpuFrameProcessor Processor = new();
        public bool Dirty = true;
        public bool HasNoise => Settings.NoiseEnabled && Settings.Noise > 0;
    }
}
