using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MW2Control.Network;

/// <summary>Loads only the installed System32 Npcap library and its trusted dependencies, on demand.</summary>
public sealed class NpcapBackend : ICaptureBackend
{
    private static readonly Lazy<Api> Native = new(() => new Api());
    public IReadOnlyList<CaptureAdapter> ListAdapters()
    {
        var api = Native.Value;
        var error = new StringBuilder(256);
        if (api.Find(out var head, error) != 0) throw new InvalidOperationException(error.ToString());
        try
        {
            var adapters = new List<CaptureAdapter>();
            var next = head;
            while (next != 0 && adapters.Count < 256)
            {
                var item = Marshal.PtrToStructure<PcapInterface>(next);
                var name = Marshal.PtrToStringAnsi(item.Name);
                if (!string.IsNullOrWhiteSpace(name)) adapters.Add(new(name, Marshal.PtrToStringAnsi(item.Description) ?? name));
                next = item.Next;
            }
            return adapters;
        }
        finally { if (head != 0) api.FreeDevices(head); }
    }
    public ICaptureDevice Open(string adapterId)
    {
        if (!ListAdapters().Any(a => a.Id == adapterId)) throw new InvalidOperationException("Unknown capture adapter.");
        var api = Native.Value;
        var error = new StringBuilder(256);
        var handle = api.Open(adapterId, 256, 0, 100, error);
        if (handle == 0) throw new InvalidOperationException("Npcap open failed (check installed driver/access): " + error);
        try
        {
            var dlt = api.LinkType(handle);
            if (!PacketMetadata.SupportsLinkType(dlt)) throw new InvalidOperationException($"Unsupported capture link type {dlt}. IPv4 Ethernet/raw/loopback only.");
            // A timeout alone does not guarantee pcap_next_ex returns on an idle adapter.
            if (api.NonBlock(handle, 1, error) != 0) throw new InvalidOperationException("Npcap nonblocking mode failed: " + error);
            return new Device(api, handle, dlt);
        }
        catch { api.Close(handle); throw; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PcapInterface { public nint Next, Name, Description, Addresses; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Header { public int Seconds, Microseconds; public uint Captured, Wire; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Program { public uint Length; public nint Instructions; }

    private sealed class Device(Api api, nint handle, int dlt) : ICaptureDevice
    {
        private nint handle = handle;
        public unsafe UdpMetadata? ReadPacket()
        {
            ObjectDisposedException.ThrowIf(handle == 0, this);
            var result = api.Next(handle, out var headerPointer, out var packetPointer);
            if (result == 0) return null;
            if (result < 0) throw new InvalidOperationException("Npcap capture ended: " + Marshal.PtrToStringAnsi(api.Error(handle)));
            if (headerPointer == 0 || packetPointer == 0) throw new InvalidOperationException("Npcap returned null packet pointers.");
            var header = Marshal.PtrToStructure<Header>(headerPointer);
            if (header.Captured > 256 || header.Captured > header.Wire || header.Wire > 1024 * 1024) return null;
            var bytes = new ReadOnlySpan<byte>((void*)packetPointer, (int)header.Captured);
            return PacketMetadata.TryParse(bytes, dlt, (int)header.Wire, out var metadata) ? metadata : null;
        }
        public void SetPorts(IReadOnlyList<ushort> ports)
        {
            ObjectDisposedException.ThrowIf(handle == 0, this);
            if (ports.Count == 0 || ports.Count > 1024 || ports.Any(p => p == 0)) throw new InvalidOperationException("Invalid game UDP port set.");
            // Fixed numeric grammar; no user filter expressions or packet dumping.
            var filter = "ip and udp and (" + string.Join(" or ", ports.Distinct().Select(p => "port " + p.ToString(System.Globalization.CultureInfo.InvariantCulture))) + ")";
            if (api.Compile(handle, out var program, filter, 1, uint.MaxValue) != 0) throw new InvalidOperationException("Npcap filter compile failed.");
            try { if (api.SetFilter(handle, ref program) != 0) throw new InvalidOperationException("Npcap filter installation failed."); }
            finally { api.FreeProgram(ref program); }
        }
        public void Dispose() { if (handle != 0) { api.Close(handle); handle = 0; } }
    }
    private sealed class Api
    {
        // Deliberately retained for process lifetime so delegates cannot outlive the module.
        private readonly nint module;
        public readonly FindDelegate Find; public readonly FreeDelegate FreeDevices; public readonly OpenDelegate Open;
        public readonly IntDelegate LinkType; public readonly NonblockDelegate NonBlock; public readonly NextDelegate Next;
        public readonly FreeDelegate Close; public readonly ErrorDelegate Error; public readonly CompileDelegate Compile;
        public readonly FilterDelegate SetFilter; public readonly FreeProgramDelegate FreeProgram;
        public Api()
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("Npcap requires this app's Windows x64 build.");
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap", "wpcap.dll");
            // DLL_LOAD_DIR + SYSTEM32 excludes application directory and current directory.
            module = LoadLibraryEx(path, 0, 0x100 | 0x800);
            if (module == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Installed Npcap is unavailable at " + path);
            Find = Load<FindDelegate>("pcap_findalldevs"); FreeDevices = Load<FreeDelegate>("pcap_freealldevs"); Open = Load<OpenDelegate>("pcap_open_live");
            LinkType = Load<IntDelegate>("pcap_datalink"); NonBlock = Load<NonblockDelegate>("pcap_setnonblock"); Next = Load<NextDelegate>("pcap_next_ex");
            Close = Load<FreeDelegate>("pcap_close"); Error = Load<ErrorDelegate>("pcap_geterr"); Compile = Load<CompileDelegate>("pcap_compile");
            SetFilter = Load<FilterDelegate>("pcap_setfilter"); FreeProgram = Load<FreeProgramDelegate>("pcap_freecode");
        }
        private T Load<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
    }
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int FindDelegate(out nint head, StringBuilder error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeDelegate(nint pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate nint OpenDelegate(string name, int snapLength, int promiscuous, int timeout, StringBuilder error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntDelegate(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NonblockDelegate(nint handle, int value, StringBuilder error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NextDelegate(nint handle, out nint header, out nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ErrorDelegate(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int CompileDelegate(nint handle, out Program program, string filter, int optimize, uint netmask);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FilterDelegate(nint handle, ref Program program);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeProgramDelegate(ref Program program);
}
