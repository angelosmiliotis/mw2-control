using System.Buffers.Binary;
using System.Text;

namespace MW2Control.Core;

public static class KnownBuild
{
    public const string Sha256 = "926F8A47959F2B24420898D07F599883E0866050D42D5289F94EEF546A4A3492";
    public const ulong FovPointerRva = 0x809C78;
    public const ulong MaxFpsPointerRva = 0x1D260A8;
    public const ulong ValueOffset = 0x10;
}

/// <summary>Pure input/profile checks. The one shipped fingerprint is the only accepted profile.</summary>
public static class TuningValidation
{
    public static ValidationResult ValidateRequest(TuningRequest? request)
    {
        if (request is null) return new(false, "A tuning request is required.");
        if (!double.IsFinite(request.Fov) || request.Fov < 65 || request.Fov > 130)
            return new(false, "FOV must be finite and between 65 and 130 inclusive.");
        if (request.MaxFps != 0 && (request.MaxFps < 30 || request.MaxFps > 500))
            return new(false, "FPS must be 0 (explicit uncapped) or between 30 and 500 inclusive.");
        return new(true, "Request is valid.");
    }

    public static ValidationResult ValidateProfile(ProcessIdentity identity)
    {
        if (!identity.Is64Bit) return new(false, "Only an AMD64 (x64) game executable is supported.");
        if (!string.Equals(identity.ProcessName, "iw4mp", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(identity.ProcessName, "iw4mp.exe", StringComparison.OrdinalIgnoreCase))
            return new(false, "The target must be iw4mp.exe.");
        // Both slash forms and Windows absolute paths are understood on a non-Windows
        // test host. Native discovery itself always supplies a normalized absolute path.
        var path = identity.ExecutablePath?.Replace('\\', '/') ?? "";
        var absolute = path.StartsWith('/') || path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/';
        var fileName = path.Split('/').LastOrDefault();
        if (!absolute || path.Contains('\0') || path.Split('/').Any(part => part is "." or ".."))
            return new(false, "The executable path must be absolute and unambiguous.");
        if (!string.Equals(fileName, "iw4mp.exe", StringComparison.OrdinalIgnoreCase))
            return new(false, "The executable path does not identify iw4mp.exe.");
        if (identity.ProcessId <= 0 || identity.StartTimeUtcTicks <= 0 ||
            !IsUserPointer(identity.ModuleBase, 0x1000) || identity.ModuleSize < KnownBuild.MaxFpsPointerRva + 8 ||
            identity.ModuleSize > 0x80000000UL || identity.ModuleBase > MaximumUserAddress - identity.ModuleSize)
            return new(false, "The process lifetime or main module bounds are invalid.");
        if (!string.Equals(identity.Sha256, KnownBuild.Sha256, StringComparison.OrdinalIgnoreCase))
            return new(false, "Unknown executable fingerprint. No offsets will be resolved or written.");
        return new(true, "Known executable fingerprint.");
    }

    public const ulong MaximumUserAddress = 0x00007FFFFFFEFFFF;
    public static bool IsUserPointer(ulong address, ulong alignment = 1) =>
        alignment > 0 && address >= 0x10000 && address <= MaximumUserAddress && address % alignment == 0;

    public static bool IsSensibleValue(TuningFeature feature, double value) => double.IsFinite(value) &&
        (feature == TuningFeature.Fov ? value >= 1 && value <= 179 : value >= 0 && value <= 1000 && value == Math.Truncate(value));
}

/// <summary>Read-only pointer/name/value validation; usable directly against an in-memory fake.</summary>
public static class DvarValidator
{
    public static ValidatedFeature Resolve(IProcessMemory memory, TuningFeature feature, bool requireWritable = false)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var profile = TuningValidation.ValidateProfile(memory.Identity);
        Require(profile.Success, profile.Message);
        Require(memory.ValidateIdentity(), "Process lifetime or module mapping changed.");
        var rva = feature switch
        {
            TuningFeature.Fov => KnownBuild.FovPointerRva,
            TuningFeature.MaxFps => KnownBuild.MaxFpsPointerRva,
            _ => throw new ArgumentOutOfRangeException(nameof(feature))
        };
        var slot = checked(memory.Identity.ModuleBase + rva);
        Require(rva <= memory.Identity.ModuleSize - 8, "Pointer slot lies outside the main module.");
        var pointer = BinaryPrimitives.ReadUInt64LittleEndian(ReadExact(memory, slot, 8));
        Require(TuningValidation.IsUserPointer(pointer, 8), "Dvar pointer is null, unaligned or outside user memory.");
        var namePointer = BinaryPrimitives.ReadUInt64LittleEndian(ReadExact(memory, pointer, 8));
        Require(TuningValidation.IsUserPointer(namePointer), "Dvar name pointer is invalid.");
        var expectedName = feature == TuningFeature.Fov ? "cg_fov" : "com_maxfps";
        var expectedNameBytes = Encoding.ASCII.GetBytes(expectedName + "\0");
        Require(ReadExact(memory, namePointer, expectedNameBytes.Length).AsSpan().SequenceEqual(expectedNameBytes),
            $"Dvar name does not exactly match {expectedName}.");
        var valueAddress = checked(pointer + KnownBuild.ValueOffset);
        var raw = ReadExact(memory, valueAddress, 4, requireWritable);
        double value = feature == TuningFeature.Fov
            ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(raw))
            : BinaryPrimitives.ReadInt32LittleEndian(raw);
        Require(TuningValidation.IsSensibleValue(feature, value), $"{expectedName} contains a nonfinite or implausible value.");
        return new(feature, valueAddress, value, raw);
    }

    public static byte[] ReadExact(IProcessMemory memory, ulong address, int count, bool requireWritable = false)
    {
        Require(TuningValidation.IsUserPointer(address) && count > 0 &&
            address <= TuningValidation.MaximumUserAddress - (ulong)(count - 1), "Memory range is invalid.");
        var region = memory.QueryRegion(address);
        Require(region.Committed && region.Contains(address, count), "Memory range is not wholly committed in one region.");
        var protection = region.Protection;
        var basic = (MemoryProtection)((uint)protection & 0xff);
        Require((protection & MemoryProtection.Guard) == 0 && basic is
            MemoryProtection.ReadOnly or MemoryProtection.ReadWrite or MemoryProtection.WriteCopy or
            MemoryProtection.ExecuteRead or MemoryProtection.ExecuteReadWrite or MemoryProtection.ExecuteWriteCopy,
            "Memory range is guarded or unreadable.");
        // Writing to any executable or copy-on-write page is deliberately unsupported.
        Require(!requireWritable || basic == MemoryProtection.ReadWrite, "Value memory is not writable, nonexecutable data.");
        var result = memory.ReadMemory(address, count);
        Require(result is not null && result.Length == count, "ReadProcessMemory returned a short read.");
        return result!;
    }

    internal static byte[] Encode(TuningFeature feature, TuningRequest request)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, feature == TuningFeature.Fov
            ? BitConverter.SingleToInt32Bits((float)request.Fov) : request.MaxFps);
        return bytes;
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
