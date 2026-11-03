using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MW2Control.Network;

public sealed class WindowsEndpointSource : IGameEndpointSource
{
    public GameSession? Read()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows UDP ownership inspection is required.");
        var processes = Process.GetProcessesByName("iw4mp");
        try
        {
            if (processes.Length == 0) return null;
            if (processes.Length != 1) throw new InvalidOperationException("Multiple iw4mp.exe processes found; no target selected.");
            var process = processes[0];
            var ticks = process.StartTime.ToUniversalTime().Ticks;
            var path = process.MainModule?.FileName ?? throw new InvalidOperationException("Cannot read game executable path.");
            if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFileName(path), "iw4mp.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Process executable identity is ambiguous.");
            var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToHashSet();
            var rows = ReadRows();
            var owned = rows.Where(r => r.Pid == process.Id && r.Endpoint.Port != 0)
                .Where(r => !rows.Any(other => other.Pid != process.Id && other.Endpoint.Port == r.Endpoint.Port &&
                    (other.Endpoint.Address.Equals(r.Endpoint.Address) || other.Endpoint.Address.Equals(IPAddress.Any) || r.Endpoint.Address.Equals(IPAddress.Any))))
                .Select(r => r.Endpoint).Distinct().ToArray();
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != ticks) return null;
            return new(process.Id, ticks, path, owned, addresses);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static IReadOnlyList<(int Pid, LocalEndpoint Endpoint)> ReadRows()
    {
        uint length = 0;
        var result = GetExtendedUdpTable(0, ref length, false, 2, 1, 0);
        if (result != 122 && result != 0) throw new Win32Exception((int)result, "Cannot inspect UDP ownership.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (length < 4 || length > 4 * 1024 * 1024) throw new InvalidOperationException("Invalid UDP table size.");
            var allocated = length;
            var buffer = Marshal.AllocHGlobal(checked((int)allocated));
            try
            {
                result = GetExtendedUdpTable(buffer, ref length, false, 2, 1, 0);
                if (result == 122) continue;
                if (result != 0) throw new Win32Exception((int)result, "Cannot inspect UDP ownership.");
                var count = Marshal.ReadInt32(buffer);
                if (count < 0 || (long)count * 12 + 4 > allocated) throw new InvalidOperationException("Truncated UDP table.");
                var rows = new List<(int, LocalEndpoint)>();
                for (var i = 0; i < count; i++)
                {
                    var row = buffer + 4 + i * 12;
                    var address = new byte[4]; Marshal.Copy(row, address, 0, 4);
                    var portBytes = new byte[2]; Marshal.Copy(row + 4, portBytes, 0, 2);
                    rows.Add((Marshal.ReadInt32(row + 8), new(new IPAddress(address), BinaryPrimitives.ReadUInt16BigEndian(portBytes))));
                }
                return rows;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("UDP ownership changed repeatedly; refresh and try again.");
    }
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedUdpTable(nint table, ref uint length, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
}
