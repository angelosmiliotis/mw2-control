using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using MW2Control.Network;

namespace MW2Control.App;

internal sealed record FirewallRequest(string Action, string? Address = null, string? GamePath = null, string? BlockId = null);

internal sealed class FirewallActions(FirewallBlockService firewall, Func<bool> isAdministrator,
    Func<FirewallRequest, Task<NetworkResult>> elevate)
{
    internal FirewallActions() : this(new FirewallBlockService(), IsAdministrator, ElevatedFirewall.RunAsync) { }

    internal Task<NetworkResult> RunAsync(FirewallRequest request, bool confirmed)
    {
        if (!confirmed) return Task.FromResult(new NetworkResult(false, "Firewall change cancelled."));
        Validate(request);
        return isAdministrator() ? Task.Run(() => Execute(request, firewall)) : elevate(request);
    }

    internal static void Validate(FirewallRequest request)
    {
        if (request.Action == "block" && request.BlockId is null)
        {
            FirewallValidation.PublicAddress(request.Address ?? string.Empty);
            var path = request.GamePath ?? string.Empty;
            if (path.Length > 1024) throw new ArgumentException("Game path is too long.");
            FirewallValidation.GamePath(path, _ => true);
            return;
        }
        var id = request.BlockId ?? string.Empty;
        if (request.Action == "unblock" && request.Address is null && request.GamePath is null &&
            id.StartsWith(FirewallBlockService.Prefix, StringComparison.Ordinal) &&
            id.Length == FirewallBlockService.Prefix.Length + 24 && id[FirewallBlockService.Prefix.Length..].All(Uri.IsHexDigit)) return;
        throw new ArgumentException("Invalid firewall request.");
    }

    internal static NetworkResult Execute(FirewallRequest request, FirewallBlockService service)
    {
        Validate(request);
        return request.Action == "block" ? service.Block(request.Address!, request.GamePath!, true) : service.Unblock(request.BlockId!, true);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

internal static class ElevatedFirewall
{
    private const string Prefix = "MW2Control.Firewall.";

    internal static async Task<NetworkResult> RunAsync(FirewallRequest request)
    {
        try
        {
            FirewallActions.Validate(request);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var token = timeout.Token;
            var name = Prefix + Guid.NewGuid().ToString("N");
            using var pipe = LocalPipe.CreateServer(name);
            var executable = Environment.ProcessPath ?? throw new IOException("App path is unavailable.");
            if (string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
                return new(false, "Open MW2Control.exe to approve firewall changes.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("--firewall");
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            using var helper = await Task.Run(() => Process.Start(start), token).WaitAsync(token)
                ?? throw new IOException("The firewall helper could not start.");
            await pipe.WaitForConnectionAsync(token);
            if (LocalPipe.ClientProcess(pipe) != helper.Id || helper.HasExited)
                throw new IOException("The firewall helper identity could not be verified.");
            await LocalPipe.WriteAsync(pipe, request, token);
            return await LocalPipe.ReadAsync<NetworkResult>(pipe, token);
        }
        catch (Exception ex) { return Failure(ex); }
    }

    internal static NetworkResult Failure(Exception exception) => exception switch
    {
        Win32Exception { NativeErrorCode: 1223 } => new(false, "Administrator request cancelled. No firewall change was requested."),
        OperationCanceledException => new(false, "The administrator request timed out. Check blocked addresses before trying again."),
        _ => new(false, "The firewall change could not be confirmed. Check blocked addresses before retrying. " + exception.Message)
    };

    internal static async Task<int> ServeAsync(string[] args)
    {
        try
        {
            if (args.Length != 3 || args[0] != "--firewall" || !args[1].StartsWith(Prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(args[1][Prefix.Length..], "N", out _) ||
                !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parentId) || parentId <= 0)
                return 2;
            using var parent = Process.GetProcessById(parentId);
            if (parent.HasExited || !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 2;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            if (LocalPipe.ServerProcess(pipe) != parentId || parent.HasExited) return 2;
            var request = await LocalPipe.ReadAsync<FirewallRequest>(pipe, timeout.Token);
            var result = FirewallActions.Execute(request, new FirewallBlockService());
            await LocalPipe.WriteAsync(pipe, result, timeout.Token);
            return result.Success ? 0 : 1;
        }
        catch { return 2; }
    }
}
