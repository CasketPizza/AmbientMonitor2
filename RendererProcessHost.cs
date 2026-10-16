using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AmbientMonitor;

internal sealed class RendererProcessHost : IAsyncDisposable
{
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly bool _offscreen;
    private Session? _current;
    private RendererCommand? _pendingUpdate;
    private bool _disposed;
    public event Action<RendererReply>? Message;
    public int? ProcessId => _current?.Process.Id;
    public RendererProcessHost(bool offscreen = false) => _offscreen = offscreen;

    public async Task ReplaceAsync(RenderingBackend backend, List<DisplayProfile> profiles, bool running, bool enableHdr = true)
    {
        await _transition.WaitAsync();
        try
        {
            if (_disposed) return;
            Interlocked.Exchange(ref _pendingUpdate, null);
            await StopCurrent();
            string pipeName = "AmbientMonitor-" + Guid.NewGuid().ToString("N");
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            string executable = Path.Combine(Path.GetDirectoryName(typeof(App).Assembly.Location)!, "AmbientMonitor.exe");
            if (File.Exists(executable)) start.FileName = executable;
            else { start.FileName = "dotnet"; start.ArgumentList.Add(typeof(App).Assembly.Location); }
            start.ArgumentList.Add("--renderer-worker");
            start.ArgumentList.Add(pipeName);
            if (_offscreen) start.ArgumentList.Add("--worker-offscreen-test");
            Process? process = null;
            try
            {
                process = Process.Start(start) ?? throw new InvalidOperationException("Could not start renderer worker.");
                var session = new Session(process, pipe, backend);
                _current = session;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await pipe.WaitForConnectionAsync(timeout.Token);
                session.InitializeIo();
                session.ReaderTask = ReadMessages(session);
                await session.Ready.Task.WaitAsync(timeout.Token);
                await session.Send(new RendererCommand { Backend = backend, Running = running, Profiles = profiles, EnableHdr = enableHdr });
            }
            catch
            {
                if (_current is not null) await StopCurrent();
                else { pipe.Dispose(); process?.Dispose(); }
                throw;
            }
        }
        finally { _transition.Release(); }
    }
    public async Task UpdateAsync(RenderingBackend backend, List<DisplayProfile> profiles, bool running, bool enableHdr = true)
    {
        Interlocked.Exchange(ref _pendingUpdate, new RendererCommand { Backend = backend, Profiles = profiles, Running = running, EnableHdr = enableHdr });
        await _transition.WaitAsync();
        try
        {
            var update = Interlocked.Exchange(ref _pendingUpdate, null);
            if (!_disposed && update is not null && _current is { } session && update.Backend == session.Backend)
                await session.Send(update);
        }
        finally { _transition.Release(); }
    }
    public async Task StopAsync()
    {
        await _transition.WaitAsync();
        try { await StopCurrent(); }
        finally { _transition.Release(); }
    }
    private async Task ReadMessages(Session session)
    {
        try
        {
            while (await session.Reader.ReadLineAsync() is { } line)
            {
                var reply = JsonSerializer.Deserialize<RendererReply>(line);
                if (reply is null) continue;
                if (reply.Kind == "ready") session.Ready.TrySetResult();
                if (ReferenceEquals(_current, session) && !session.Stopping) Message?.Invoke(reply);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { session.Ready.TrySetException(ex); }
        finally
        {
            if (ReferenceEquals(_current, session) && !session.Stopping)
                Message?.Invoke(new RendererReply { Kind = "error", Text = "Renderer worker stopped unexpectedly. Start overlay to create a fresh worker.", ProcessId = session.Process.Id });
        }
    }
    private async Task StopCurrent()
    {
        var session = _current;
        if (session is null) return;
        _current = null;
        session.Stopping = true;
        try
        {
            if (session.Pipe.IsConnected)
            {
                try { await session.Send(new RendererCommand { Operation = "shutdown" }); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await session.Process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                // Only our renderer child is terminated, never the UI/user apps.
                if (!session.Process.HasExited) session.Process.Kill(entireProcessTree: true);
                await session.Process.WaitForExitAsync();
            }
        }
        finally
        {
            try { session.Writer?.Dispose(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            session.Reader?.Dispose();
            session.Pipe.Dispose();
            session.Process.Dispose();
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _transition.WaitAsync();
        try { _disposed = true; await StopCurrent(); }
        finally { _transition.Release(); }
    }
    private sealed class Session
    {
        public readonly Process Process;
        public readonly RenderingBackend Backend;
        public readonly NamedPipeServerStream Pipe;
        public StreamReader Reader = null!;
        public StreamWriter Writer = null!;
        public Task? ReaderTask;
        public bool Stopping;
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Session(Process process, NamedPipeServerStream pipe, RenderingBackend backend)
        {
            Process = process; Pipe = pipe; Backend = backend;
        }
        public void InitializeIo()
        {
            Reader = new StreamReader(Pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            Writer = new StreamWriter(Pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        }
        public Task Send(RendererCommand command) => Writer.WriteLineAsync(JsonSerializer.Serialize(command));
    }
}
