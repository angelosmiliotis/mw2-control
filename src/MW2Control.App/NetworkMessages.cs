using System.ComponentModel;

namespace MW2Control.App;

internal static class NetworkMessages
{
    internal static string Failure(Exception exception) => exception switch
    {
        DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or
            Win32Exception { NativeErrorCode: 126 or 127 or 193 } => "Npcap is missing or unavailable. Install Npcap and reopen MW2 Control.",
        UnauthorizedAccessException => "The network adapter could not be accessed. Check Npcap permissions.",
        _ when exception.Message.StartsWith("Join a lobby", StringComparison.Ordinal) => "Join a lobby in MW2, then click Scan lobby.",
        _ => "The network operation failed. Open Network options for details."
    };

    internal static string ScanSummary(bool capturing, int peers, string status, bool scanHasHadTime = false)
    {
        if (capturing && peers == 0 && scanHasHadTime)
            return "No traffic yet. Join a lobby or check the adapter in Network options.";
        if (capturing) return peers == 0 ? "Waiting for lobby traffic…" : $"{peers} connections observed";
        if (status.StartsWith("Stopped.", StringComparison.Ordinal)) return "Scan stopped.";
        if (status.StartsWith("Capture stopped:", StringComparison.Ordinal)) return "The game or connection changed. Join a lobby and scan again.";
        if (status.StartsWith("Capture unavailable:", StringComparison.Ordinal) || status.StartsWith("Network operation failed:", StringComparison.Ordinal))
            return "The scan could not start. Open Network options for details.";
        return status;
    }
}
