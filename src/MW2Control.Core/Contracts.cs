namespace MW2Control.Core;

public enum EngineState { GameNotRunning, UnsupportedBuild, ReadOnlyReady, Error }
public sealed record FeatureReading(string Id, string Label, bool Supported, double? ObservedValue, string Detail);
public sealed record EngineSnapshot(EngineState State, string ProcessName, int? ProcessId, string GamePath, string Fingerprint, string Detail, IReadOnlyList<FeatureReading> Features);
public sealed record TuningRequest(int MaxFps, double Fov);
public sealed record AdvancedSettings(bool MuteStreams, bool Fullbright, bool NoFog, bool NoGlow, int? SpecularMode, bool NoDetailOverlays, bool NumericPing = false);
public sealed record OperationResult(bool Success, string Message, EngineSnapshot Snapshot)
{
    public bool CanMaintainOwned { get; init; }
}

/// <summary>All fields form the session identity; a PID alone is never sufficient.</summary>
public sealed record ProcessIdentity(int ProcessId, long StartTimeUtcTicks, string ProcessName,
    string ExecutablePath, ulong ModuleBase, ulong ModuleSize, string Sha256, bool Is64Bit);

[Flags]
public enum MemoryProtection : uint
{
    NoAccess = 0x01, ReadOnly = 0x02, ReadWrite = 0x04, WriteCopy = 0x08,
    Execute = 0x10, ExecuteRead = 0x20, ExecuteReadWrite = 0x40, ExecuteWriteCopy = 0x80,
    Guard = 0x100
}

public sealed record MemoryRegion(ulong BaseAddress, ulong Size, bool Committed, MemoryProtection Protection)
{
    public bool Contains(ulong address, int count) => count > 0 && address >= BaseAddress &&
        (ulong)count <= Size && address - BaseAddress <= Size - (ulong)count;
}

/// <summary>
/// Fake-adapter boundary. ReadMemory may return short data; validation rejects it.
/// WriteMemory returns the number of contiguous prefix bytes written (0..data.Length).
/// Implementations must never change protection, inject, or execute remote code.
/// ValidateIdentity must check process lifetime AND the current main module mapping.
/// </summary>
public interface IProcessMemory : IDisposable
{
    ProcessIdentity Identity { get; }
    bool ValidateIdentity();
    MemoryRegion QueryRegion(ulong address);
    byte[] ReadMemory(ulong address, int count);
    int WriteMemory(ulong address, byte[] data);
}

/// <summary>Return null for no process. Throw TargetProcessException for ambiguous/inaccessible targets.</summary>
public interface IProcessSource
{
    IProcessMemory? OpenTarget();
}

public sealed class TargetProcessException : Exception
{
    public EngineState State { get; }
    public TargetProcessException(EngineState state, string message, Exception? inner = null) : base(message, inner) => State = state;
}

/// <summary>A failed write with a known contiguous byte count, if the OS supplied one.</summary>
public sealed class MemoryWriteException : IOException
{
    public int BytesWritten { get; }
    public MemoryWriteException(string message, int bytesWritten, Exception? inner = null) : base(message, inner) => BytesWritten = bytesWritten;
}

public enum TuningFeature { Fov, MaxFps }
public sealed record ValidationResult(bool Success, string Message);
public sealed record TransactionResult(bool Success, string Message);
public sealed record ValidatedFeature(TuningFeature Feature, ulong Address, double Value, byte[] RawValue);

public enum AdvancedDvar { StreamEnabled, Fullbright, Fog, Glow, SpecularMap, Detail, DetailMap, PingText, PingGraph }
public sealed record ValidatedAdvancedDvar(AdvancedDvar Feature, ulong Address, int Value, byte[] RawValue);
