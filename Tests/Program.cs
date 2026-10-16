using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using AmbientMonitor;
using Vortice.Direct3D11;
using Vortice.DXGI;

// Opt-in hardware integration checks. Readback happens only in this test process.
internal static class Program
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Type ServiceType = typeof(MainWindow).Assembly.GetType("AmbientMonitor.GpuRenderService")!;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            for (int cycle = 0; cycle < 6; cycle++) CheckFreshService(cycle);
            if (args.Contains("--ui-restarts")) CheckStartStopButtons();
            Console.WriteLine("All GPU restart regression checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static object? Field(object value, string name) => value.GetType().GetField(name, Members)!.GetValue(value);
    private static object? Call(object value, string name, params object[] args) =>
        value.GetType().GetMethod(name, Members)!.Invoke(value, args);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void OnGpu(object service, Action action)
    {
        Exception? error = null;
        using var finished = new ManualResetEventSlim();
        Call(service, "Enqueue", (Action)(() =>
        {
            try { action(); } catch (Exception ex) { error = ex; }
            finally { finished.Set(); }
        }));
        Require(finished.Wait(TimeSpan.FromSeconds(10)), "GPU worker did not complete the test command.");
        if (error is not null) throw new InvalidOperationException("GPU assertion failed.", error);
    }

    private static void WaitForImage(object service, bool pumpUi = false)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            bool ready = false;
            OnGpu(service, () =>
            {
                var targets = (IDictionary)Field(service, "_targets")!;
                ready = Field(service, "_desktop") is not null && targets.Count > 0 &&
                    targets.Values.Cast<object>().All(t => Field(t, "First") is not null);
            });
            if (ready) return;
            if (pumpUi) Pump(50); else Thread.Sleep(50);
        }
        throw new InvalidOperationException($"No captured/rendered desktop image. Status: {Field(service, "_lastStatus")}");
    }

    private static void CheckFreshService(int cycle)
    {
        object service = Activator.CreateInstance(ServiceType)!;
        nint parent = CreateWindowEx(0x80020, "static", "GPU regression", 0x90000000,
            -30000, -30000, 128, 128, 0, 0, 0, 0);
        SetLayeredWindowAttributes(parent, 0, 255, 2);
        nint child = CreateWindowEx(0, "static", "GPU target", 0x50000000,
            0, 0, 128, 128, parent, 0, 0, 0);
        try
        {
            Require(parent != 0 && child != 0, "Test HWND creation failed.");
            Call(service, "Add", child, 128, 128, new OverlaySettings { Blur = 36 });
            WaitForImage(service);
            OnGpu(service, () =>
            {
                var device = (ID3D11Device)Field(service, "_device")!;
                var context = (ID3D11DeviceContext)Field(service, "_context")!;
                var target = ((IDictionary)Field(service, "_targets")!)[child]!;

                // A known-color GPU frame proves that the full image chain works;
                // merely checking for a non-null capture texture misses black output.
                using var fixture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
                    64, 64, 1, 1, bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource));
                using var fixtureView = device.CreateRenderTargetView(fixture);
                context.ClearRenderTargetView(fixtureView, new Vortice.Mathematics.Color4(0.8f, 0.4f, 0.2f, 1));
                Call(service, "ReleaseCapture");
                Call(service, "CopyDesktopImage", fixture);

                // Reproduce loss of persistent pass state across native presentation
                // lifecycle changes. Noise alone must never hide a missing sampler.
                if (cycle % 2 == 0) context.PSUnsetSampler(0);
                else context.ClearState();
                Call(service, "Render", target, true, 1f);
                var surface = Field(target, "First")!;
                var texture = (ID3D11Texture2D)surface.GetType().GetProperty("Texture")!.GetValue(surface)!;
                var pixel = ReadCenter(texture, device, context);
                Require(Math.Abs(pixel[2] - 204) <= 2 && Math.Abs(pixel[1] - 102) <= 2 && Math.Abs(pixel[0] - 51) <= 2,
                    $"Restart {cycle + 1}: the crop/blur image was black or incorrect ({pixel[2]}, {pixel[1]}, {pixel[0]}).");
                Call(service, "ReleaseCapture");
            });
            Call(service, "Remove", child);
            Console.WriteLine($"PASS: GPU start/stop cycle {cycle + 1}, known-color image after shader-state invalidation.");
        }
        finally
        {
            ((IDisposable)service).Dispose();
            if (child != 0) DestroyWindow(child);
            if (parent != 0) DestroyWindow(parent);
        }
    }

    private static byte[] ReadCenter(ID3D11Texture2D texture, ID3D11Device device, ID3D11DeviceContext context)
    {
        var description = texture.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;
        using var readback = device.CreateTexture2D(description);
        context.CopyResource(readback, texture);
        var mapped = context.Map(readback, 0, MapMode.Read);
        try
        {
            var pixel = new byte[4];
            Marshal.Copy(mapped.DataPointer + (int)(description.Height / 2 * mapped.RowPitch + description.Width / 2 * 4), pixel, 0, 4);
            return pixel;
        }
        finally { context.Unmap(readback, 0); }
    }

    private static void CheckStartStopButtons()
    {
        var primary = CaptureService.GetScreens().First(s => s.Primary).Bounds;
        var fixture = new System.Windows.Window
        {
            Left = primary.Left + primary.Width / 2d - 160, Top = primary.Top + primary.Height / 2d - 120,
            Width = 320, Height = 240, Background = System.Windows.Media.Brushes.Coral,
            WindowStyle = System.Windows.WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Topmost = true
        };
        var control = new MainWindow
        {
            Left = -30000, Top = -30000, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            ShowActivated = false
        };
        try
        {
            control.Show(); fixture.Show(); Pump(100);
            var profiles = (List<DisplayProfile>)Field(control, "_profiles")!;
            foreach (var profile in profiles)
            {
                profile.Enabled = profile.DisplayName != "Primary display";
                profile.Mode = OverlayMode.Ambient;
                profile.CropZoom = 4; profile.FocusX = 0.5; profile.FocusY = 0.5;
            }
            Require(profiles.Any(p => p.Enabled), "UI restart checks require at least one secondary display.");
            for (int cycle = 0; cycle < 20; cycle++)
            {
                Call(control, "StartButton_OnClick", control, new System.Windows.RoutedEventArgs());
                Pump(500);
                object service = Field(control, "_gpu")!;
                WaitForImage(service, pumpUi: true);
                OnGpu(service, () =>
                {
                    var device = (ID3D11Device)Field(service, "_device")!;
                    var context = (ID3D11DeviceContext)Field(service, "_context")!;
                    foreach (object target in ((IDictionary)Field(service, "_targets")!).Values)
                    {
                        var surface = Field(target, "First")!;
                        var texture = (ID3D11Texture2D)surface.GetType().GetProperty("Texture")!.GetValue(surface)!;
                        var pixel = ReadCenter(texture, device, context);
                        Require(pixel[2] > 20 && pixel[1] > 10, $"Start/Stop cycle {cycle + 1} lost the captured image.");
                    }
                });
                Call(control, "StartButton_OnClick", control, new System.Windows.RoutedEventArgs());
                Pump(50);
                Console.WriteLine($"PASS: actual Start/Stop buttons, cycle {cycle + 1}, captured image pixels verified.");
            }
        }
        finally { control.Close(); fixture.Close(); }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
}
