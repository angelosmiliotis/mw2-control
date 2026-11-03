using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MW2Control.Core;

/// <summary>Windows-only external process discovery. No game launch or enumeration by guessed offsets.</summary>
internal sealed class WindowsProcessSource : IProcessSource
{
    public IProcessMemory? OpenTarget()
    {
        if (!OperatingSystem.IsWindows())
            throw new TargetProcessException(EngineState.Error, "Native game inspection is available only on Windows.");
        if (!Environment.Is64BitProcess)
            throw new TargetProcessException(EngineState.UnsupportedBuild, "Run the x64 build of MW2 Control.");
        Process[] processes;
        try { processes = Process.GetProcessesByName("iw4mp"); }
        catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
        { throw new TargetProcessException(EngineState.Error, "Could not enumerate iw4mp.exe: " + ex.Message, ex); }
        try
        {
            if (processes.Length == 0) return null;
            if (processes.Length != 1)
                throw new TargetProcessException(EngineState.Error, "Multiple iw4mp.exe processes are running. Close extra instances before inspection; no target was selected.");
            return WindowsProcessMemory.Open(processes[0]);
        }
        catch (TargetProcessException) { throw; }
        catch (Win32Exception ex)
        {
            throw new TargetProcessException(EngineState.Error,
                ex.NativeErrorCode == 5 ? "Access to iw4mp.exe was denied. No elevation or writes were attempted." : "Native process inspection failed: " + ex.Message, ex);
        }
        catch (Exception ex) when (TuningTransaction.IsExpectedFailure(ex))
        { throw new TargetProcessException(EngineState.Error, "Could not inspect iw4mp.exe: " + ex.Message, ex); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

internal sealed class WindowsProcessMemory : IProcessMemory
{
    private readonly SafeProcessHandle readHandle;
    private readonly FileStream executable;
    private readonly object gate = new();
    private bool disposed;
    public ProcessIdentity Identity { get; }

    private WindowsProcessMemory(SafeProcessHandle readHandle, FileStream executable, ProcessIdentity identity)
    { this.readHandle = readHandle; this.executable = executable; Identity = identity; }

    internal static WindowsProcessMemory Open(Process process)
    {
        SafeProcessHandle? handle = null;
        FileStream? file = null;
        try
        {
            // QUERY_INFORMATION is needed for VirtualQueryEx; no VM_WRITE/OPERATION at inspection.
            handle = Native.OpenProcess(Native.QueryInformation | Native.VmRead, false, process.Id);
            if (handle.IsInvalid) throw Native.Error("OpenProcess (read-only)");
            var path = Native.ImagePath(handle);
            if (!string.Equals(Path.GetFileName(path), "iw4mp.exe", StringComparison.OrdinalIgnoreCase))
                throw new TargetProcessException(EngineState.UnsupportedBuild, "The process image is not iw4mp.exe.");
            if (!Native.IsWow64Process2(handle, out var processMachine, out var nativeMachine))
                throw Native.Error("IsWow64Process2");
            var is64Bit = processMachine == 0 && nativeMachine == 0x8664;
            if (!is64Bit)
                throw new TargetProcessException(EngineState.UnsupportedBuild, "Only a native AMD64 (x64) iw4mp.exe process is supported.");
            var module = process.MainModule ?? throw new InvalidOperationException("The main module is unavailable.");
            var modulePath = Path.GetFullPath(module.FileName);
            if (!string.Equals(path, modulePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Process image path and main module path disagree.");
            var moduleBase = checked((ulong)module.BaseAddress.ToInt64());
            var moduleSize = checked((ulong)module.ModuleMemorySize);
            var created = Native.CreationTicks(handle);
            // Retain this read-only file handle for the session. FileShare.Read forbids
            // simultaneous writers/replacement, keeping the fingerprint's file stable.
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length < 512 || file.Length > 2L * 1024 * 1024 * 1024)
                throw new TargetProcessException(EngineState.UnsupportedBuild, "Executable size is outside the supported PE inspection bounds.");
            using (var pe = new PEReader(file, PEStreamOptions.LeaveOpen))
            {
                var headers = pe.PEHeaders;
                if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader?.Magic != PEMagic.PE32Plus ||
                    headers.PEHeader.SizeOfImage <= 0 || (ulong)headers.PEHeader.SizeOfImage != moduleSize)
                    throw new TargetProcessException(EngineState.UnsupportedBuild, "The executable is not an AMD64 PE32+ image matching the loaded module bounds.");
            }
            file.Position = 0;
            var hash = Convert.ToHexString(SHA256.HashData(file));
            var identity = new ProcessIdentity(process.Id, created, "iw4mp", path, moduleBase, moduleSize, hash, is64Bit);
            var result = new WindowsProcessMemory(handle, file, identity);
            if (!result.ValidateIdentity()) throw new InvalidOperationException("Process lifetime or main module changed during inspection.");
            handle = null;
            file = null;
            return result;
        }
        finally { handle?.Dispose(); file?.Dispose(); }
    }

    public bool ValidateIdentity()
    {
        lock (gate)
        {
            if (disposed || readHandle.IsInvalid || readHandle.IsClosed) return false;
            if (!Native.GetExitCodeProcess(readHandle, out var exitCode)) throw Native.Error("GetExitCodeProcess");
            if (exitCode != Native.StillActive || Native.CreationTicks(readHandle) != Identity.StartTimeUtcTicks ||
                Native.GetProcessId(readHandle) != Identity.ProcessId) return false;
            if (!string.Equals(Native.ImagePath(readHandle), Identity.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return false;
            using var process = Process.GetProcessById(Identity.ProcessId);
            var module = process.MainModule;
            if (module is null || checked((ulong)module.BaseAddress.ToInt64()) != Identity.ModuleBase ||
                checked((ulong)module.ModuleMemorySize) != Identity.ModuleSize ||
                !string.Equals(Path.GetFullPath(module.FileName), Identity.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return false;
            // Reject replaced/private mappings even when their addresses happen to match.
            // Short/unknown images are diagnostic-only; known images must cover both slots.
            if (!IsMainImageMapping(Identity.ModuleBase)) return false;
            if (string.Equals(Identity.Sha256, KnownBuild.Sha256, StringComparison.OrdinalIgnoreCase) &&
                Identity.ModuleSize > KnownBuild.MaxFpsPointerRva + 8 &&
                (!IsMainImageMapping(checked(Identity.ModuleBase + KnownBuild.FovPointerRva)) ||
                 !IsMainImageMapping(checked(Identity.ModuleBase + KnownBuild.MaxFpsPointerRva)))) return false;
            return executable.CanRead;
        }
    }

    private bool IsMainImageMapping(ulong address)
    {
        var region = QueryNative(address);
        return region.State == Native.MemCommit && region.Type == Native.MemImage &&
            unchecked((ulong)region.AllocationBase.ToInt64()) == Identity.ModuleBase;
    }

    public MemoryRegion QueryRegion(ulong address)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var info = QueryNative(address);
            // An optional cache/enum table must still be an image allocation of
            // this main module. Reject only that read; optional mapping failures
            // do not globally invalidate otherwise valid FOV/FPS mappings.
            var inMainModule = address >= Identity.ModuleBase && address - Identity.ModuleBase < Identity.ModuleSize;
            var correctMapping = !inMainModule || info.Type == Native.MemImage &&
                unchecked((ulong)info.AllocationBase.ToInt64()) == Identity.ModuleBase;
            return new(unchecked((ulong)info.BaseAddress.ToInt64()), (ulong)info.RegionSize,
                info.State == Native.MemCommit && correctMapping, (MemoryProtection)info.Protect);
        }
    }

    private Native.MemoryBasicInformation QueryNative(ulong address)
    {
        var result = Native.VirtualQueryEx(readHandle, Native.Pointer(address), out var info,
            (nuint)Marshal.SizeOf<Native.MemoryBasicInformation>());
        if (result != (nuint)Marshal.SizeOf<Native.MemoryBasicInformation>()) throw Native.Error("VirtualQueryEx");
        return info;
    }

    public byte[] ReadMemory(ulong address, int count)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (count <= 0 || count > 4096 || !TuningValidation.IsUserPointer(address))
                throw new ArgumentOutOfRangeException(nameof(count), "Only small validated data reads are supported.");
            var bytes = new byte[count];
            if (!Native.ReadProcessMemory(readHandle, Native.Pointer(address), bytes, (nuint)count, out var read))
                throw Native.Error("ReadProcessMemory");
            if (read != (nuint)count) throw new IOException("ReadProcessMemory returned a short read.");
            return bytes;
        }
    }

    public int WriteMemory(ulong address, byte[] data)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(data);
            if (data.Length is not (1 or 4) || address % (ulong)data.Length != 0 || !TuningValidation.IsUserPointer(address))
                throw new InvalidOperationException("Only a validated byte boolean or aligned four-byte data value is permitted.");
            var profile = TuningValidation.ValidateProfile(Identity);
            if (!profile.Success) throw new InvalidOperationException(profile.Message);
            if (!ValidateIdentity()) throw new InvalidOperationException("The target session changed before a write.");
            // Write permissions exist only for this explicit call, including rollback calls.
            using var writeHandle = Native.OpenProcess(Native.QueryLimitedInformation | Native.VmWrite | Native.VmOperation, false, Identity.ProcessId);
            if (writeHandle.IsInvalid) throw Native.Error("OpenProcess (explicit write)");
            if (Native.CreationTicks(writeHandle) != Identity.StartTimeUtcTicks ||
                Native.GetProcessId(writeHandle) != Identity.ProcessId ||
                !string.Equals(Native.ImagePath(writeHandle), Identity.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The write handle belongs to a different process lifetime.");
            // Each candidate is independent: an unavailable optional feature cannot
            // block tuning. Only exact validated names/types/domains enter this whitelist.
            if (!DvarWritePolicy.IsAllowed(this, address, data))
                throw new InvalidOperationException("The write does not target a validated supported dvar value.");
            if (!ValidateIdentity()) throw new InvalidOperationException("The target session changed before the native write.");
            // Refuse executable, guarded and copy-on-write pages even at the native boundary.
            var page = QueryRegion(address);
            if (!page.Committed || !page.Contains(address, data.Length) || page.Protection != MemoryProtection.ReadWrite)
                throw new InvalidOperationException("The destination is not an ordinary writable data page.");
            var success = Native.WriteProcessMemory(writeHandle, Native.Pointer(address), data, (nuint)data.Length, out var written);
            if (!success)
            {
                var error = Native.Error("WriteProcessMemory");
                throw new MemoryWriteException(error.Message, written <= int.MaxValue ? (int)written : -1, error);
            }
            if (written > int.MaxValue) throw new MemoryWriteException("WriteProcessMemory returned an invalid byte count.", -1);
            return (int)written;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            readHandle.Dispose();
            executable.Dispose();
        }
    }
}

internal static class Native
{
    internal const uint QueryInformation = 0x0400, QueryLimitedInformation = 0x1000;
    internal const uint VmRead = 0x0010, VmWrite = 0x0020, VmOperation = 0x0008;
    internal const uint MemCommit = 0x1000, MemImage = 0x1000000, StillActive = 259;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    internal static nint Pointer(ulong address) => checked((nint)address);
    internal static Win32Exception Error(string operation) => new(Marshal.GetLastWin32Error(), operation + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    internal static long CreationTicks(SafeProcessHandle handle)
    {
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _)) throw Error("GetProcessTimes");
        return DateTime.FromFileTimeUtc(creation).Ticks;
    }

    internal static string ImagePath(SafeProcessHandle handle)
    {
        var path = new StringBuilder(32768);
        var count = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref count)) throw Error("QueryFullProcessImageName");
        return Path.GetFullPath(path.ToString());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryBasicInformation info, nuint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, [Out] byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint written);
}
