using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MW2Control.App;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    internal string PipeName { get; }
    private readonly CancellationTokenSource _lifetime = new();
    private readonly bool _ownsMutex;
    private Task? _listener;
    internal bool IsPrimary => _ownsMutex;

    internal SingleInstance(string scope)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        PipeName = "MW2Control.Instance." + key;
        _mutex = new Mutex(false, @"Local\" + PipeName);
        try { _ownsMutex = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
    }

    internal static SingleInstance ForCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        return new(identity.User!.Value + ":" + process.SessionId);
    }

    internal void Listen(Action activate)
    {
        if (!IsPrimary || _listener is not null) return;
        _listener = ListenAsync(activate);
    }

    private async Task ListenAsync(Action activate)
    {
        var token = _lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = LocalPipe.CreateServer(PipeName);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await LocalPipe.WriteAsync(server, Environment.ProcessId, timeout.Token).ConfigureAwait(false);
                if (await LocalPipe.ReadAsync<string>(server, timeout.Token).ConfigureAwait(false) == "activate") activate();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or
                Win32Exception or JsonException or InvalidOperationException)
            {
                // A disconnected second launch must not stop the primary instance.
                if (!token.IsCancellationRequested)
                    try { await Task.Delay(100, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    internal async Task ActivatePrimaryAsync(bool allowForeground = true)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        var owner = await LocalPipe.ReadAsync<int>(client, timeout.Token);
        if (owner <= 0 || LocalPipe.ServerProcess(client) != owner) throw new IOException("The existing app could not be verified.");
        if (allowForeground) _ = AllowSetForegroundWindow((uint)owner);
        await LocalPipe.WriteAsync(client, "activate", timeout.Token);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _listener?.GetAwaiter().GetResult();
        if (_ownsMutex) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _lifetime.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
