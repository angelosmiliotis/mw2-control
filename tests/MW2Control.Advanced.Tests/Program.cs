using System.Buffers.Binary;
using System.Text;
using MW2Control.Core;
using MW2Control.Advanced.Tests;

var passed = 0;
var failed = 0;
var none = new AdvancedSettings(false, false, false, false, null, false);
var all = new AdvancedSettings(true, true, true, true, 0, true);

Test("confirmation and invalid-mode gates never write", () =>
{
    var memory = FakeMemory.Create();
    var transaction = new AdvancedTransaction(memory);
    False(transaction.Apply(all, false).Success);
    False(transaction.Restore(false).Success);
    False(transaction.Apply(none with { SpecularMode = -1 }, true).Success);
    False(transaction.Apply(none with { SpecularMode = 3 }, true).Success);
    False(AdvancedValidation.ValidateSettings(null).Success);
    Equal(0, memory.WriteCalls);
});

Test("unknown fingerprint rejects every advanced operation and whitelist", () =>
{
    var memory = FakeMemory.Create();
    memory.Identity = memory.Identity with { Sha256 = new string('0', 64) };
    False(new AdvancedTransaction(memory).Apply(all, true).Success);
    False(DvarWritePolicy.IsAllowed(memory, FakeMemory.Value(AdvancedDvar.StreamEnabled), [0]));
    using var engine = new ControlEngine(new FakeSource(memory));
    var result = engine.ApplyAdvanced(all, true);
    False(result.Success);
    Equal(EngineState.UnsupportedBuild, result.Snapshot.State);
    Equal(0, memory.ReadCalls);
    Equal(0, memory.WriteCalls);
});

Test("inspection independently reports optional missing feature and never writes", () =>
{
    var memory = FakeMemory.Create();
    memory.Missing(AdvancedDvar.StreamEnabled);
    using var engine = new ControlEngine(new FakeSource(memory));
    var snapshot = engine.Inspect();
    Equal(EngineState.ReadOnlyReady, snapshot.State);
    Equal(9, snapshot.Features.Count);
    False(snapshot.Features.Single(f => f.Id == "music").Supported);
    True(snapshot.Features.Where(f => f.Id != "music").All(f => f.Supported));
    Equal(0, memory.WriteCalls);
    True(engine.Apply(new(165, 95), true).Success);
    True(DvarWritePolicy.IsAllowed(memory, 0x210010, BitConverter.GetBytes(94f)));
});

Test("small module keeps tuning and in-bounds numeric ping available", () =>
{
    var memory = FakeMemory.Create();
    memory.Identity = memory.Identity with { ModuleSize = 0x1D27000 };
    using var engine = new ControlEngine(new FakeSource(memory));
    var snapshot = engine.Inspect();
    Equal(EngineState.ReadOnlyReady, snapshot.State);
    True(snapshot.Features.Take(2).All(feature => feature.Supported));
    True(snapshot.Features.Single(feature => feature.Id == "ping").Supported);
    True(snapshot.Features.Skip(2).Where(feature => feature.Id != "ping").All(feature => !feature.Supported));
    True(engine.Apply(new(165, 95), true).Success);
});

Test("unsupported requested feature fails full preflight before earlier feature writes", () =>
{
    var memory = FakeMemory.Create();
    memory.Missing(AdvancedDvar.Fullbright);
    False(new AdvancedTransaction(memory).Apply(none with { MuteStreams = true, Fullbright = true }, true).Success);
    Equal(1, memory.Current(AdvancedDvar.StreamEnabled));
    Equal(0, memory.WriteCalls);
});

Test("all-false unowned settings ignore optional missing controls", () =>
{
    var memory = FakeMemory.Create();
    foreach (var feature in Enum.GetValues<AdvancedDvar>()) memory.Missing(feature);
    True(new AdvancedTransaction(memory).Apply(none, true).Success);
    Equal(0, memory.WriteCalls);
    Equal(0, memory.ReadCalls);
});

Test("byte boolean writes preserve adjacent union, flags, latched and default bytes", () =>
{
    var memory = FakeMemory.Create();
    var beforeHeader = memory.Raw(FakeMemory.Dvar(AdvancedDvar.Fullbright) + 0x0C, 1);
    True(new AdvancedTransaction(memory).Apply(none with { Fullbright = true }, true).Success);
    Equal(1, memory.Writes.Count);
    Equal(1, memory.Writes[0].Bytes.Length);
    Bytes([1, 0xA5, 0xA5, 0xA5], memory.Raw(FakeMemory.Value(AdvancedDvar.Fullbright), 4));
    Bytes(beforeHeader, memory.Raw(FakeMemory.Dvar(AdvancedDvar.Fullbright) + 0x0C, 1));
    Bytes([0x31, 0x32, 0x33, 0x34], memory.Raw(FakeMemory.Dvar(AdvancedDvar.Fullbright) + 0x20, 4));
    Bytes([0x41, 0x42, 0x43, 0x44], memory.Raw(FakeMemory.Dvar(AdvancedDvar.Fullbright) + 0x30, 4));
});

Test("native whitelist accepts only supported exact addresses, lengths and domains", () =>
{
    var memory = FakeMemory.Create();
    True(DvarWritePolicy.IsAllowed(memory, FakeMemory.Value(AdvancedDvar.Fullbright), [1]));
    False(DvarWritePolicy.IsAllowed(memory, FakeMemory.Value(AdvancedDvar.Fullbright), [1, 0, 0, 0]));
    False(DvarWritePolicy.IsAllowed(memory, FakeMemory.Value(AdvancedDvar.Fullbright), [2]));
    False(DvarWritePolicy.IsAllowed(memory, FakeMemory.Value(AdvancedDvar.Fullbright) + 0x10, [1]));
    False(DvarWritePolicy.IsAllowed(memory, 0x210010, [1]));
    True(DvarWritePolicy.IsAllowed(memory, FakeMemory.Value(AdvancedDvar.SpecularMap), BitConverter.GetBytes(3)));
    False(DvarWritePolicy.IsAllowed(memory, FakeMemory.Value(AdvancedDvar.SpecularMap), BitConverter.GetBytes(4)));
    memory.Missing(AdvancedDvar.Fullbright);
    False(DvarWritePolicy.IsAllowed(memory, FakeMemory.Value(AdvancedDvar.Fullbright), [1]));
    True(DvarWritePolicy.IsAllowed(memory, 0x210110, BitConverter.GetBytes(0)));
});

Test("every advanced control applies atomically then false/null restores captured originals", () =>
{
    var memory = FakeMemory.Create();
    var transaction = new AdvancedTransaction(memory);
    var originals = Enum.GetValues<AdvancedDvar>().ToDictionary(feature => feature, memory.Current);
    True(transaction.Apply(all, true).Success);
    Equal(7, memory.WriteCalls);
    Equal(0, memory.Current(AdvancedDvar.StreamEnabled));
    Equal(1, memory.Current(AdvancedDvar.Fullbright));
    Equal(0, memory.Current(AdvancedDvar.DetailMap));
    True(transaction.Apply(none, true).Success);
    foreach (var feature in Enum.GetValues<AdvancedDvar>()) Equal(originals[feature], memory.Current(feature));
    Equal(14, memory.WriteCalls);
    True(transaction.Restore(true).Success);
    Equal(14, memory.WriteCalls);
});

Test("false preserves unowned changes from another writer", () =>
{
    var memory = FakeMemory.Create();
    var transaction = new AdvancedTransaction(memory);
    True(transaction.Apply(none with { MuteStreams = true }, true).Success);
    memory.SetCurrent(AdvancedDvar.Fog, 0);
    True(transaction.Apply(none, true).Success);
    Equal(1, memory.Current(AdvancedDvar.StreamEnabled));
    Equal(0, memory.Current(AdvancedDvar.Fog));
    Equal(2, memory.WriteCalls);
});

Test("already-target baselines are never claimed or forced to defaults", () =>
{
    var memory = FakeMemory.Create();
    foreach (var feature in Enum.GetValues<AdvancedDvar>()) memory.SetCurrent(feature, feature == AdvancedDvar.Fullbright ? 1 : 0);
    var transaction = new AdvancedTransaction(memory);
    True(transaction.Apply(all, true).Success);
    True(transaction.Apply(none, true).Success);
    True(transaction.Restore(true).Success);
    Equal(0, memory.WriteCalls);
    Equal(1, memory.Current(AdvancedDvar.Fullbright));
    Equal(0, memory.Current(AdvancedDvar.StreamEnabled));
});

Test("enum preflight rejects wrong type, count, table, pointer, label or terminator", () =>
{
    foreach (var corrupt in new Action<FakeMemory>[]
    {
        m => m.Put(FakeMemory.Dvar(AdvancedDvar.SpecularMap) + 0x0C, [0]),
        m => m.Put32(FakeMemory.Dvar(AdvancedDvar.SpecularMap) + 0x40, 3),
        m => m.Put64(FakeMemory.Dvar(AdvancedDvar.SpecularMap) + 0x48, 0x400000),
        m => m.Put64(FakeMemory.SupportedIdentity.ModuleBase + AdvancedBuild.SpecularEnumTableRva, 0x400000),
        m => m.Put(FakeMemory.SupportedIdentity.ModuleBase + 0x3C2F14, Encoding.ASCII.GetBytes("Other\0")),
        m => m.Put64(FakeMemory.SupportedIdentity.ModuleBase + AdvancedBuild.SpecularEnumTableRva + 32, 0x400000),
        m => m.SetCurrent(AdvancedDvar.SpecularMap, 4)
    })
    {
        var memory = FakeMemory.Create();
        corrupt(memory);
        False(new AdvancedTransaction(memory).Apply(none with { MuteStreams = true, SpecularMode = 0 }, true).Success);
        Equal(0, memory.WriteCalls);
    }
});

Test("wrong exact name, boolean type, pointer alignment or current value is rejected", () =>
{
    foreach (var corrupt in new Action<FakeMemory>[]
    {
        m => m.Put(FakeMemory.Name(AdvancedDvar.StreamEnabled), Encoding.ASCII.GetBytes("snd_enableStream_extra\0")),
        m => m.Put(FakeMemory.Dvar(AdvancedDvar.StreamEnabled) + 0x0C, [6]),
        m => m.Put64(FakeMemory.SupportedIdentity.ModuleBase + AdvancedBuild.StreamPointerRva, 0x200003),
        m => m.SetCurrent(AdvancedDvar.StreamEnabled, 2)
    })
    {
        var memory = FakeMemory.Create();
        corrupt(memory);
        False(new AdvancedTransaction(memory).Apply(none with { MuteStreams = true }, true).Success);
        Equal(0, memory.WriteCalls);
    }
});

Test("detail compound rejects either missing half without partial writes", () =>
{
    foreach (var feature in new[] { AdvancedDvar.Detail, AdvancedDvar.DetailMap })
    {
        var memory = FakeMemory.Create();
        memory.Missing(feature);
        var transaction = new AdvancedTransaction(memory);
        False(transaction.InspectFeatures().Single(reading => reading.Id == "detail").Supported);
        False(transaction.Apply(none with { NoDetailOverlays = true }, true).Success);
        Equal(0, memory.WriteCalls);
    }
});

Test("short exact reads and protected optional data fail before any writes", () =>
{
    foreach (var corrupt in new Action<FakeMemory>[]
    {
        m => m.ShortRead = FakeMemory.Name(AdvancedDvar.StreamEnabled),
        m => m.Protections[FakeMemory.Value(AdvancedDvar.StreamEnabled)] = MemoryProtection.ReadOnly,
        m => m.Protections[FakeMemory.Value(AdvancedDvar.StreamEnabled)] = MemoryProtection.ReadWrite | MemoryProtection.Guard,
        m => m.Protections[FakeMemory.Value(AdvancedDvar.StreamEnabled)] = MemoryProtection.ExecuteReadWrite
    })
    {
        var memory = FakeMemory.Create();
        corrupt(memory);
        False(new AdvancedTransaction(memory).Apply(none with { MuteStreams = true }, true).Success);
        Equal(0, memory.WriteCalls);
        True(new TuningTransaction(memory).Apply(new(165, 95), true).Success);
    }
});

Test("short enum write rolls back its known prefix and earlier boolean", () =>
{
    foreach (var useException in new[] { false, true })
    {
        var memory = FakeMemory.Create();
        memory.SetCurrent(AdvancedDvar.SpecularMap, 3);
        memory.WriteCounts[2] = 2;
        memory.FailPartialWithException = useException;
        False(new AdvancedTransaction(memory).Apply(none with { MuteStreams = true, SpecularMode = 0 }, true).Success);
        Equal(1, memory.Current(AdvancedDvar.StreamEnabled));
        Equal(3, memory.Current(AdvancedDvar.SpecularMap));
        Equal(4, memory.WriteCalls);
    }
});

Test("failed readback rolls back earlier writes", () =>
{
    var memory = FakeMemory.Create();
    memory.IgnoreWrites.Add(2);
    False(new AdvancedTransaction(memory).Apply(none with { MuteStreams = true, Fullbright = true }, true).Success);
    Equal(1, memory.Current(AdvancedDvar.StreamEnabled));
    Equal(0, memory.Current(AdvancedDvar.Fullbright));
    Equal(3, memory.WriteCalls);
});

Test("rollback preserves externally changed bytes and reports incomplete ownership", () =>
{
    var memory = FakeMemory.Create();
    memory.AfterWrite = (call, _, _) => { if (call == 2) memory.SetCurrent(AdvancedDvar.SpecularMap, 2); };
    var transaction = new AdvancedTransaction(memory);
    var result = transaction.Apply(none with { MuteStreams = true, SpecularMode = 0 }, true);
    False(result.Success);
    True(result.Message.Contains("Rollback incomplete", StringComparison.Ordinal));
    Equal(1, memory.Current(AdvancedDvar.StreamEnabled));
    Equal(2, memory.Current(AdvancedDvar.SpecularMap));
    Equal(3, memory.WriteCalls);
    False(transaction.Restore(true).Success);
    Equal(3, memory.WriteCalls);
});

Test("restore preflight preserves another writer's changes and writes nothing", () =>
{
    var memory = FakeMemory.Create();
    var transaction = new AdvancedTransaction(memory);
    True(transaction.Apply(none with { MuteStreams = true, SpecularMode = 0 }, true).Success);
    memory.SetCurrent(AdvancedDvar.SpecularMap, 2);
    False(transaction.Restore(true).Success);
    Equal(2, memory.WriteCalls);
    Equal(0, memory.Current(AdvancedDvar.StreamEnabled));
});

Test("dvar pointer transition refuses restore into a replacement address", () =>
{
    var memory = FakeMemory.Create();
    var transaction = new AdvancedTransaction(memory);
    True(transaction.Apply(none with { MuteStreams = true }, true).Success);
    memory.Put64(FakeMemory.SupportedIdentity.ModuleBase + AdvancedBuild.StreamPointerRva, 0x211000);
    memory.Put64(0x211000, FakeMemory.Name(AdvancedDvar.StreamEnabled));
    memory.Put(0x21100C, [0]);
    memory.Put(0x211010, [0]);
    False(transaction.Restore(true).Success);
    Equal(1, memory.WriteCalls);
});

Test("session transition refuses both direct and facade restore after refresh", () =>
{
    var memory = FakeMemory.Create();
    var transaction = new AdvancedTransaction(memory);
    True(transaction.Apply(none with { MuteStreams = true }, true).Success);
    memory.Identity = memory.Identity with { StartTimeUtcTicks = memory.Identity.StartTimeUtcTicks + 1 };
    False(transaction.Restore(true).Success);
    Equal(1, memory.WriteCalls);

    var first = FakeMemory.Create();
    var source = new FakeSource(first);
    using var engine = new ControlEngine(source);
    True(engine.ApplyAdvanced(none with { Fullbright = true }, true).Success);
    var second = FakeMemory.Create();
    second.Identity = second.Identity with { ProcessId = second.Identity.ProcessId + 1 };
    source.Memory = second;
    Equal(EngineState.ReadOnlyReady, engine.Inspect().State);
    False(engine.RestoreAdvanced(true).Success);
    Equal(0, second.WriteCalls);
});

Test("failed mixed restore/apply rolls back to previous owned tuning", () =>
{
    var memory = FakeMemory.Create();
    var transaction = new AdvancedTransaction(memory);
    True(transaction.Apply(none with { MuteStreams = true }, true).Success);
    memory.WriteCounts[3] = 0;
    False(transaction.Apply(none with { NoFog = true }, true).Success);
    Equal(0, memory.Current(AdvancedDvar.StreamEnabled));
    Equal(1, memory.Current(AdvancedDvar.Fog));
    True(transaction.Restore(true).Success);
    Equal(1, memory.Current(AdvancedDvar.StreamEnabled));
});

Test("incomplete rollback retains known ownership for a later guarded restore", () =>
{
    var memory = FakeMemory.Create();
    memory.BeforeWrite = (call, _, _) =>
    {
        if (call != 2) return;
        memory.IdentityValid = false;
        throw new IOException("Synthetic process-map transition before write.");
    };
    var transaction = new AdvancedTransaction(memory);
    False(transaction.Apply(none with { MuteStreams = true, Fullbright = true }, true).Success);
    Equal(0, memory.Current(AdvancedDvar.StreamEnabled));
    memory.IdentityValid = true;
    memory.BeforeWrite = null;
    True(transaction.Restore(true).Success);
    Equal(1, memory.Current(AdvancedDvar.StreamEnabled));
});

Test("specular accepts only requested 0 1 2 and restores actual Gray baseline", () =>
{
    foreach (var mode in new[] { 0, 1, 2 })
    {
        var memory = FakeMemory.Create();
        memory.SetCurrent(AdvancedDvar.SpecularMap, 3);
        var transaction = new AdvancedTransaction(memory);
        True(transaction.Apply(none with { SpecularMode = mode }, true).Success);
        Equal(mode, memory.Current(AdvancedDvar.SpecularMap));
        True(transaction.Apply(none, true).Success);
        Equal(3, memory.Current(AdvancedDvar.SpecularMap));
    }
});

Test("feature readings are independent active-state values with explicit scope", () =>
{
    var memory = FakeMemory.Create();
    var transaction = new AdvancedTransaction(memory);
    var baseline = transaction.InspectFeatures();
    Equal(7, baseline.Count);
    Equal(0d, baseline.Single(f => f.Id == "music").ObservedValue);
    True(transaction.Apply(all, true).Success);
    var active = transaction.InspectFeatures();
    True(active.Where(f => f.Id is not ("specular" or "ping")).All(f => f.ObservedValue == 1));
    True(active.Single(f => f.Id == "music").Detail.Contains("bomb timer remained audible", StringComparison.Ordinal));
    True(active.Single(f => f.Id == "detail").Detail.Contains("not guaranteed No Camos", StringComparison.Ordinal));
});

Test("facade FOV/FPS and advanced restore ledgers are independent", () =>
{
    var memory = FakeMemory.Create();
    using var engine = new ControlEngine(new FakeSource(memory));
    True(engine.Apply(new(165, 95), true).Success);
    True(engine.ApplyAdvanced(all, true).Success);
    True(engine.RestoreAdvanced(true).Success);
    Equal(165, BinaryPrimitives.ReadInt32LittleEndian(memory.Raw(0x210110, 4)));
    True(engine.Restore(true).Success);
    Equal(144, BinaryPrimitives.ReadInt32LittleEndian(memory.Raw(0x210110, 4)));
});

Test("explicit FPS zero is valid observed writable and restorable", () =>
{
    var memory = FakeMemory.Create();
    memory.Put32(0x210110, 0);
    True(TuningValidation.ValidateRequest(new(0, 90)).Success);
    False(TuningValidation.ValidateRequest(new(1, 90)).Success);
    True(TuningValidation.IsSensibleValue(TuningFeature.MaxFps, 0));
    var transaction = new TuningTransaction(memory);
    True(transaction.Apply(new(165, 95), true).Success);
    True(transaction.Restore(true).Success);
    Equal(0, BinaryPrimitives.ReadInt32LittleEndian(memory.Raw(0x210110, 4)));
    True(transaction.Apply(new(0, 95), true).Success);
    Equal(0, BinaryPrimitives.ReadInt32LittleEndian(memory.Raw(0x210110, 4)));
});

Test("explicit ResetSession forgets both histories and writes nothing", () =>
{
    var first = FakeMemory.Create();
    var source = new FakeSource(first);
    using var engine = new ControlEngine(source);
    True(engine.Apply(new(165, 95), true).Success);
    True(engine.ApplyAdvanced(none with { Fullbright = true }, true).Success);
    var replacementAccessor = FakeMemory.Create();
    replacementAccessor.Put32(0x210010, BitConverter.SingleToInt32Bits(88));
    replacementAccessor.Put32(0x210110, 200);
    replacementAccessor.SetCurrent(AdvancedDvar.Fullbright, 1);
    source.Memory = replacementAccessor;
    Equal(EngineState.ReadOnlyReady, engine.ResetSession().State);
    True(first.Disposed);
    Equal(0, replacementAccessor.WriteCalls);
    True(engine.Restore(true).Success);
    True(engine.RestoreAdvanced(true).Success);
    Equal(0, replacementAccessor.WriteCalls);
    True(engine.Apply(new(165, 95), true).Success);
    True(engine.ApplyAdvanced(none with { NoFog = true }, true).Success);
    True(engine.Restore(true).Success);
    True(engine.RestoreAdvanced(true).Success);
    Equal(200, BinaryPrimitives.ReadInt32LittleEndian(replacementAccessor.Raw(0x210110, 4)));
    Equal(88f, BitConverter.ToSingle(replacementAccessor.Raw(0x210010, 4)));
    Equal(1, replacementAccessor.Current(AdvancedDvar.Fullbright));
});

Test("ResetSession also clears prior-lifetime markers without writes", () =>
{
    var first = FakeMemory.Create();
    var source = new FakeSource(first);
    using var engine = new ControlEngine(source);
    True(engine.Apply(new(165, 95), true).Success);
    True(engine.ApplyAdvanced(none with { Fullbright = true }, true).Success);
    var next = FakeMemory.Create();
    next.Identity = next.Identity with { StartTimeUtcTicks = next.Identity.StartTimeUtcTicks + 1 };
    source.Memory = next;
    Equal(EngineState.ReadOnlyReady, engine.ResetSession().State);
    True(engine.Restore(true).Success);
    True(engine.RestoreAdvanced(true).Success);
    Equal(0, next.WriteCalls);
});

Test("manual tuning retry and Restore accept exact captured reset", () =>
{
    var memory = FakeMemory.Create();
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(65));
    memory.Put32(0x210110, 85);
    var transaction = new TuningTransaction(memory);
    True(transaction.Apply(new(0, 100), true).Success);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(65));
    memory.Put32(0x210110, 85);
    var reading = transaction.InspectFeatures().Single(feature => feature.Id == "fov");
    True(reading.Detail.Contains("cg_fov (FOV): observed 65; last owned 100", StringComparison.Ordinal));
    True(reading.Detail.Contains("The game itself may reset", StringComparison.Ordinal));
    True(reading.Detail.Contains("Restore can clear ownership", StringComparison.Ordinal));
    True(transaction.Restore(true).Success);
    False(transaction.ReapplyOwned().Success);
    Equal(2, memory.WriteCalls);
    False(transaction.Apply(new(0, 100), false).Success);
    Equal(2, memory.WriteCalls);
    True(transaction.Apply(new(0, 100), true).Success);
    Equal(4, memory.WriteCalls);
    True(transaction.Restore(true).Success);
    Equal(65f, BitConverter.ToSingle(memory.Raw(0x210010, 4)));
    Equal(85, BinaryPrimitives.ReadInt32LittleEndian(memory.Raw(0x210110, 4)));
});

Test("third tuning values still fail with named observed and last-owned diagnostics", () =>
{
    var memory = FakeMemory.Create();
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(65));
    memory.Put32(0x210110, 85);
    var transaction = new TuningTransaction(memory);
    True(transaction.Apply(new(0, 100), true).Success);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(70));
    var fovConflict = transaction.Apply(new(0, 100), true);
    False(fovConflict.Success);
    True(fovConflict.Message.Contains("cg_fov (FOV): observed 70; last owned 100", StringComparison.Ordinal));
    True(fovConflict.Message.Contains("restart the game", StringComparison.Ordinal));
    True(fovConflict.Message.Contains("The game itself may reset", StringComparison.Ordinal));
    Equal(2, memory.WriteCalls);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(100));
    memory.Put32(0x210110, 90);
    var fpsConflict = transaction.Apply(new(0, 100), true);
    False(fpsConflict.Success);
    True(fpsConflict.Message.Contains("com_maxfps (FPS cap): observed 90; last owned 0 (uncapped)", StringComparison.Ordinal));
    False(transaction.Restore(true).Success);
    Equal(2, memory.WriteCalls);
});

Test("failed manual tuning retry rolls back to reset baseline and retains originals", () =>
{
    var memory = FakeMemory.Create();
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(65));
    memory.Put32(0x210110, 85);
    var transaction = new TuningTransaction(memory);
    True(transaction.Apply(new(0, 100), true).Success);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(65));
    memory.Put32(0x210110, 85);
    memory.WriteCounts[4] = 0;
    False(transaction.Apply(new(0, 100), true).Success);
    Equal(65f, BitConverter.ToSingle(memory.Raw(0x210010, 4)));
    Equal(85, BinaryPrimitives.ReadInt32LittleEndian(memory.Raw(0x210110, 4)));
    True(transaction.Restore(true).Success);
    Equal(5, memory.WriteCalls);
    True(transaction.Apply(new(0, 100), true).Success);
    True(transaction.Restore(true).Success);
    Equal(65f, BitConverter.ToSingle(memory.Raw(0x210010, 4)));
    Equal(85, BinaryPrimitives.ReadInt32LittleEndian(memory.Raw(0x210110, 4)));
});

Test("advanced false and null restore clear exact baseline ownership without writes", () =>
{
    var memory = FakeMemory.Create();
    var transaction = new AdvancedTransaction(memory);
    var requested = none with { MuteStreams = true, SpecularMode = 0 };
    True(transaction.Apply(requested, true).Success);
    memory.SetCurrent(AdvancedDvar.StreamEnabled, 1);
    memory.SetCurrent(AdvancedDvar.SpecularMap, 1);
    var reading = transaction.InspectFeatures().Single(feature => feature.Id == "music");
    True(reading.Detail.Contains("snd_enableStream (Mute streamed sounds): observed 1; last owned 0", StringComparison.Ordinal));
    True(reading.Detail.Contains("Restore can clear ownership", StringComparison.Ordinal));
    True(transaction.Apply(none, true).Success);
    False(transaction.ReapplyOwned().Success);
    Equal(2, memory.WriteCalls);
    True(transaction.Apply(requested, true).Success);
    True(transaction.Restore(true).Success);
    Equal(1, memory.Current(AdvancedDvar.StreamEnabled));
    Equal(1, memory.Current(AdvancedDvar.SpecularMap));
});

Test("advanced third enum value fails atomic retry after another feature resets", () =>
{
    var memory = FakeMemory.Create();
    var transaction = new AdvancedTransaction(memory);
    var requested = none with { MuteStreams = true, SpecularMode = 0 };
    True(transaction.Apply(requested, true).Success);
    memory.SetCurrent(AdvancedDvar.StreamEnabled, 1);
    memory.SetCurrent(AdvancedDvar.SpecularMap, 2);
    var result = transaction.Apply(requested, true);
    False(result.Success);
    True(result.Message.Contains("r_specularMap (Specular mode): observed 2; last owned 0", StringComparison.Ordinal));
    True(result.Message.Contains("restart the game", StringComparison.Ordinal));
    Equal(2, memory.WriteCalls);
    Equal(1, memory.Current(AdvancedDvar.StreamEnabled));
    Equal(2, memory.Current(AdvancedDvar.SpecularMap));
});

Test("partial advanced retry rolls back to baseline and preserves restore ownership guard", () =>
{
    var memory = FakeMemory.Create();
    var transaction = new AdvancedTransaction(memory);
    var requested = none with { MuteStreams = true, SpecularMode = 0 };
    True(transaction.Apply(requested, true).Success);
    memory.SetCurrent(AdvancedDvar.StreamEnabled, 1);
    memory.SetCurrent(AdvancedDvar.SpecularMap, 1);
    memory.WriteCounts[4] = 2;
    False(transaction.Apply(requested, true).Success);
    Equal(1, memory.Current(AdvancedDvar.StreamEnabled));
    Equal(1, memory.Current(AdvancedDvar.SpecularMap));
    Equal(6, memory.WriteCalls);
    True(transaction.Restore(true).Success);
    Equal(6, memory.WriteCalls);
    True(transaction.Apply(requested, true).Success);
    True(transaction.Restore(true).Success);
    Equal(1, memory.Current(AdvancedDvar.StreamEnabled));
    Equal(1, memory.Current(AdvancedDvar.SpecularMap));
});

Test("exact baseline retry still rejects changed mapping or process lifetime", () =>
{
    var memory = FakeMemory.Create();
    var transaction = new TuningTransaction(memory);
    True(transaction.Apply(new(0, 100), true).Success);
    memory.Put64(FakeMemory.SupportedIdentity.ModuleBase + KnownBuild.FovPointerRva, 0x211000);
    memory.Put64(0x211000, 0x310000);
    memory.Put32(0x211010, BitConverter.SingleToInt32Bits(90));
    False(transaction.Apply(new(0, 100), true).Success);
    Equal(2, memory.WriteCalls);
    memory.Put64(FakeMemory.SupportedIdentity.ModuleBase + KnownBuild.FovPointerRva, 0x210000);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(90));
    memory.Put32(0x210110, 144);
    memory.Identity = memory.Identity with { StartTimeUtcTicks = memory.Identity.StartTimeUtcTicks + 1 };
    False(transaction.Apply(new(0, 100), true).Success);
    Equal(2, memory.WriteCalls);

    var advancedMemory = FakeMemory.Create();
    var advanced = new AdvancedTransaction(advancedMemory);
    True(advanced.Apply(none with { MuteStreams = true }, true).Success);
    advancedMemory.SetCurrent(AdvancedDvar.StreamEnabled, 1);
    advancedMemory.Identity = advancedMemory.Identity with { ProcessId = advancedMemory.Identity.ProcessId + 1 };
    False(advanced.Apply(none with { MuteStreams = true }, true).Success);
    Equal(1, advancedMemory.WriteCalls);
});

Test("MaintainOwned never discovers a target and requires an owned successful Apply", () =>
{
    var memory = FakeMemory.Create();
    var source = new FakeSource(memory);
    using var engine = new ControlEngine(source);
    False(engine.MaintainOwned(true).Success);
    Equal(0, source.OpenCalls);
    Equal(0, memory.WriteCalls);
    Equal(EngineState.ReadOnlyReady, engine.Inspect().State);
    False(engine.MaintainOwned(true).Success);
    Equal(1, source.OpenCalls);
    Equal(0, memory.WriteCalls);
});

Test("MaintainOwned confirmation revocation stops until successful manual Apply", () =>
{
    var memory = FakeMemory.Create();
    var source = new FakeSource(memory);
    using var engine = new ControlEngine(source);
    True(engine.Apply(new(0, 100), true).Success);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(90));
    memory.Put32(0x210110, 144);
    False(engine.MaintainOwned(false).Success);
    False(engine.MaintainOwned(true).Success);
    Equal(2, memory.WriteCalls);
    Equal(1, source.OpenCalls);
    True(engine.Apply(new(0, 100), true).Success);
    True(engine.MaintainOwned(true).Success);
    Equal(4, memory.WriteCalls);
    Equal(2, source.OpenCalls);
});

Test("repeated held-session maintenance restores captured resets without reopening source", () =>
{
    var memory = FakeMemory.Create();
    var source = new FakeSource(memory);
    using var engine = new ControlEngine(source);
    True(engine.Apply(new(0, 100), true).Success);
    for (var tick = 0; tick < 10; tick++)
    {
        memory.Put32(0x210010, BitConverter.SingleToInt32Bits(90));
        memory.Put32(0x210110, 144);
        True(engine.MaintainOwned(true).Success);
        Equal(100f, BitConverter.ToSingle(memory.Raw(0x210010, 4)));
        Equal(0, BinaryPrimitives.ReadInt32LittleEndian(memory.Raw(0x210110, 4)));
        var writes = memory.WriteCalls;
        True(engine.MaintainOwned(true).Success);
        Equal(writes, memory.WriteCalls);
        Equal(1, source.OpenCalls);
    }
    True(engine.Restore(true).Success);
    Equal(90f, BitConverter.ToSingle(memory.Raw(0x210010, 4)));
    Equal(144, BinaryPrimitives.ReadInt32LittleEndian(memory.Raw(0x210110, 4)));
});

Test("delayed read-only Inspect of baseline preserves held maintenance ledger", () =>
{
    var memory = FakeMemory.Create();
    memory.Identity = memory.Identity with { ModuleSize = 0x1D27000 };
    var source = new FakeSource(memory);
    using var engine = new ControlEngine(source);
    True(engine.Apply(new(165, 95), true).Success);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(90));
    memory.Put32(0x210110, 144);
    var snapshot = engine.Inspect();
    Equal(EngineState.ReadOnlyReady, snapshot.State);
    Equal(90d, snapshot.Features.Single(feature => feature.Id == "fov").ObservedValue);
    Equal(2, memory.WriteCalls);
    Equal(1, source.OpenCalls);
    True(engine.MaintainOwned(true).Success);
    Equal(95f, BitConverter.ToSingle(memory.Raw(0x210010, 4)));
    Equal(165, BinaryPrimitives.ReadInt32LittleEndian(memory.Raw(0x210110, 4)));
    Equal(1, source.OpenCalls);
    True(memory.Writes.All(write => write.Address is 0x210010 or 0x210110 && write.Bytes.Length == 4));
    Equal(80f, BitConverter.ToSingle(memory.Raw(0x210044, 4)));
    Equal(100, BinaryPrimitives.ReadInt32LittleEndian(memory.Raw(0x210144, 4)));
});

Test("maintenance pauses at an unrelated value even for an already-matching request", () =>
{
    var memory = FakeMemory.Create();
    var source = new FakeSource(memory);
    using var engine = new ControlEngine(source);
    True(engine.Apply(new(0, 90), true).Success);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(91));
    memory.Put32(0x210110, 144);
    False(engine.MaintainOwned(true).Success);
    Equal(91f, BitConverter.ToSingle(memory.Raw(0x210010, 4)));
    Equal(144, BinaryPrimitives.ReadInt32LittleEndian(memory.Raw(0x210110, 4)));
    Equal(1, memory.WriteCalls);
    Equal(1, source.OpenCalls);
});

Test("unrelated tuning value pauses writes and automatically recovers after a recognized reset", () =>
{
    var memory = FakeMemory.Create();
    var source = new FakeSource(memory);
    using var engine = new ControlEngine(source);
    True(engine.Apply(new(0, 100), true).Success);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(70));
    memory.Put32(0x210110, 144);
    var failedTick = engine.MaintainOwned(true);
    False(failedTick.Success);
    True(failedTick.Message.Contains("observed 70; last applied 100", StringComparison.Ordinal));
    Equal(2, memory.WriteCalls);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(90));
    True(failedTick.CanMaintainOwned);
    True(engine.MaintainOwned(true).Success);
    Equal(4, memory.WriteCalls);
    Equal(1, source.OpenCalls);
});

Test("maintenance rejects pointer lifetime and fingerprint transitions without reopening", () =>
{
    foreach (var corrupt in new Action<FakeMemory>[]
    {
        memory =>
        {
            memory.Put64(FakeMemory.SupportedIdentity.ModuleBase + KnownBuild.FovPointerRva, 0x211000);
            memory.Put64(0x211000, 0x310000);
            memory.Put32(0x211010, BitConverter.SingleToInt32Bits(90));
        },
        memory => memory.Identity = memory.Identity with { StartTimeUtcTicks = memory.Identity.StartTimeUtcTicks + 1 },
        memory => memory.Identity = memory.Identity with { Sha256 = new string('0', 64) },
        memory => memory.IdentityValid = false
    })
    {
        var memory = FakeMemory.Create();
        var source = new FakeSource(memory);
        using var engine = new ControlEngine(source);
        True(engine.Apply(new(0, 100), true).Success);
        corrupt(memory);
        False(engine.MaintainOwned(true).Success);
        Equal(2, memory.WriteCalls);
        Equal(1, source.OpenCalls);
    }
});

Test("maintenance fails closed on changed tuning kind domain or protection", () =>
{
    foreach (var corrupt in new Action<FakeMemory>[]
    {
        memory => memory.Put(0x21000C, [0]),
        memory => memory.Put(0x21010C, [0]),
        memory => memory.Put32(0x210044, BitConverter.SingleToInt32Bits(100)),
        memory => memory.Put32(0x210144, 500),
        memory => memory.Protections[0x210010] = MemoryProtection.ReadOnly,
        memory => memory.Protections[0x210010] = MemoryProtection.ExecuteReadWrite,
        memory => memory.Protections[0x210010] = MemoryProtection.ReadWrite | MemoryProtection.Guard
    })
    {
        var memory = FakeMemory.Create();
        var source = new FakeSource(memory);
        using var engine = new ControlEngine(source);
        True(engine.Apply(new(0, 100), true).Success);
        memory.Put32(0x210010, BitConverter.SingleToInt32Bits(90));
        memory.Put32(0x210110, 144);
        corrupt(memory);
        False(engine.MaintainOwned(true).Success);
        Equal(2, memory.WriteCalls);
        Equal(1, source.OpenCalls);
    }
});

Test("missing initial tuning metadata cannot seed maintenance via ordinary Apply", () =>
{
    var memory = FakeMemory.Create();
    memory.Put(0x21000C, [0]);
    var transaction = new TuningTransaction(memory);
    True(transaction.Apply(new(0, 100), true).Success);
    False(transaction.ReapplyOwned().Success);
    Equal(2, memory.WriteCalls);
    True(transaction.Restore(true).Success);
});

Test("advanced maintenance validates each owned type and domain before any group write", () =>
{
    foreach (var corrupt in new Action<FakeMemory>[]
    {
        memory => memory.Put(FakeMemory.Dvar(AdvancedDvar.StreamEnabled) + 0x0C, [6]),
        memory => memory.Put32(FakeMemory.Dvar(AdvancedDvar.SpecularMap) + 0x40, 3),
        memory => memory.SetCurrent(AdvancedDvar.SpecularMap, 2)
    })
    {
        var memory = FakeMemory.Create();
        var source = new FakeSource(memory);
        using var engine = new ControlEngine(source);
        True(engine.ApplyAdvanced(none with { MuteStreams = true, SpecularMode = 0 }, true).Success);
        memory.SetCurrent(AdvancedDvar.StreamEnabled, 1);
        memory.SetCurrent(AdvancedDvar.SpecularMap, 1);
        corrupt(memory);
        False(engine.MaintainOwned(true).Success);
        Equal(2, memory.WriteCalls);
        Equal(1, source.OpenCalls);
    }
});

Test("advanced maintenance reapplies only owned targets and preserves captured originals", () =>
{
    var memory = FakeMemory.Create();
    var source = new FakeSource(memory);
    using var engine = new ControlEngine(source);
    True(engine.ApplyAdvanced(none with { MuteStreams = true, SpecularMode = 0 }, true).Success);
    for (var tick = 0; tick < 3; tick++)
    {
        memory.SetCurrent(AdvancedDvar.StreamEnabled, 1);
        memory.SetCurrent(AdvancedDvar.SpecularMap, 1);
        True(engine.MaintainOwned(true).Success);
        Equal(0, memory.Current(AdvancedDvar.StreamEnabled));
        Equal(0, memory.Current(AdvancedDvar.SpecularMap));
        Equal(1, source.OpenCalls);
    }
    True(engine.RestoreAdvanced(true).Success);
    Equal(1, memory.Current(AdvancedDvar.StreamEnabled));
    Equal(1, memory.Current(AdvancedDvar.SpecularMap));
    False(engine.MaintainOwned(true).Success);
});

Test("cross-group maintenance reports partial completion and stops before unsafe advanced writes", () =>
{
    var memory = FakeMemory.Create();
    var source = new FakeSource(memory);
    using var engine = new ControlEngine(source);
    True(engine.Apply(new(0, 100), true).Success);
    True(engine.ApplyAdvanced(none with { MuteStreams = true, SpecularMode = 0 }, true).Success);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(90));
    memory.Put32(0x210110, 144);
    memory.SetCurrent(AdvancedDvar.StreamEnabled, 1);
    memory.SetCurrent(AdvancedDvar.SpecularMap, 2);
    var result = engine.MaintainOwned(true);
    False(result.Success);
    True(result.Message.Contains("Tuning may already have been reapplied", StringComparison.Ordinal));
    Equal(6, memory.WriteCalls);
    Equal(100f, BitConverter.ToSingle(memory.Raw(0x210010, 4)));
    Equal(0, BinaryPrimitives.ReadInt32LittleEndian(memory.Raw(0x210110, 4)));
    Equal(1, memory.Current(AdvancedDvar.StreamEnabled));
    Equal(2, memory.Current(AdvancedDvar.SpecularMap));
    False(engine.MaintainOwned(true).Success);
    Equal(6, memory.WriteCalls);
    Equal(2, source.OpenCalls);
});

Test("failed maintenance readback rolls back only its group and preserves originals", () =>
{
    var memory = FakeMemory.Create();
    var source = new FakeSource(memory);
    using var engine = new ControlEngine(source);
    True(engine.Apply(new(0, 100), true).Success);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(90));
    memory.Put32(0x210110, 144);
    memory.WriteCounts[4] = 2;
    False(engine.MaintainOwned(true).Success);
    Equal(90f, BitConverter.ToSingle(memory.Raw(0x210010, 4)));
    Equal(144, BinaryPrimitives.ReadInt32LittleEndian(memory.Raw(0x210110, 4)));
    Equal(6, memory.WriteCalls);
    True(engine.MaintainOwned(true).Success);
    Equal(8, memory.WriteCalls);
    True(engine.Apply(new(0, 100), true).Success);
    True(engine.Restore(true).Success);
    Equal(90f, BitConverter.ToSingle(memory.Raw(0x210010, 4)));
    Equal(144, BinaryPrimitives.ReadInt32LittleEndian(memory.Raw(0x210110, 4)));
});

Test("failed Apply residual bytes cannot become a new maintenance target", () =>
{
    var memory = FakeMemory.Create();
    memory.BeforeWrite = (call, _, _) =>
    {
        if (call != 2) return;
        memory.IdentityValid = false;
        throw new IOException("Synthetic interrupted Apply.");
    };
    var transaction = new TuningTransaction(memory);
    False(transaction.Apply(new(0, 100), true).Success);
    memory.IdentityValid = true;
    memory.BeforeWrite = null;
    var writes = memory.WriteCalls;
    False(transaction.ReapplyOwned().Success);
    Equal(writes, memory.WriteCalls);
    True(transaction.Restore(true).Success);
    Equal(90f, BitConverter.ToSingle(memory.Raw(0x210010, 4)));
});

Test("Reset Inspect and Dispose never reapply and clear maintenance eligibility", () =>
{
    var memory = FakeMemory.Create();
    var source = new FakeSource(memory);
    var engine = new ControlEngine(source);
    True(engine.Apply(new(0, 100), true).Success);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(90));
    Equal(EngineState.ReadOnlyReady, engine.Inspect().State);
    Equal(2, memory.WriteCalls);
    var nextAccessor = FakeMemory.Create();
    source.Memory = nextAccessor;
    Equal(EngineState.ReadOnlyReady, engine.ResetSession().State);
    False(engine.MaintainOwned(true).Success);
    Equal(0, nextAccessor.WriteCalls);
    var calls = source.OpenCalls;
    engine.Dispose();
    False(engine.MaintainOwned(true).Success);
    Equal(calls, source.OpenCalls);
    Equal(0, nextAccessor.WriteCalls);
});

Test("dispose never implicitly restores advanced values", () =>
{
    var memory = FakeMemory.Create();
    var engine = new ControlEngine(new FakeSource(memory));
    True(engine.ApplyAdvanced(none with { Fullbright = true }, true).Success);
    engine.Dispose();
    Equal(1, memory.Current(AdvancedDvar.Fullbright));
    Equal(1, memory.WriteCalls);
    Equal(EngineState.Error, engine.ResetSession().State);
});

Test("numeric ping enables both scoreboard changes and restores a mixed baseline", () =>
{
    var memory = FakeMemory.Create();
    var transaction = new AdvancedTransaction(memory);
    True(transaction.Apply(none with { NumericPing = true }, true).Success);
    Equal(2, memory.WriteCalls);
    True(memory.Writes.All(write => write.Bytes.Length == 1));
    Equal(1d, transaction.InspectFeatures().Single(feature => feature.Id == "ping").ObservedValue);
    memory.SetCurrent(AdvancedDvar.PingText, 0);
    True(transaction.Apply(none, true).Success);
    Equal(3, memory.WriteCalls);
    Equal(FakeMemory.Value(AdvancedDvar.PingGraph), memory.Writes[^1].Address);
    Equal(1, memory.Current(AdvancedDvar.PingGraph));
    False(transaction.ReapplyOwned().Success);
});

Test("combined Apply recaptures unowned settings after the game changes them", () =>
{
    var memory = FakeMemory.Create();
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(90));
    memory.Put32(0x210110, 144);
    memory.SetCurrent(AdvancedDvar.PingText, 1);
    memory.SetCurrent(AdvancedDvar.PingGraph, 0);
    using var engine = new ControlEngine(new FakeSource(memory));
    var first = engine.ApplySettings(new(144, 90), none with { NumericPing = true });
    True(first.Success);
    True(first.CanMaintainOwned);
    Equal(0, memory.WriteCalls);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(65));
    memory.SetCurrent(AdvancedDvar.PingGraph, 1);
    var retry = engine.ApplySettings(new(144, 90), none with { NumericPing = true });
    True(retry.Success);
    True(retry.CanMaintainOwned);
    Equal(2, memory.WriteCalls);
    True(engine.ResetSettings().Success);
    Equal(65d, engine.Inspect().Features.Single(feature => feature.Id == "fov").ObservedValue);
    Equal(1, memory.Current(AdvancedDvar.PingGraph));
});

Test("maintenance survives FPS and ping baseline resets during write readback", () =>
{
    var memory = FakeMemory.Create();
    memory.Put32(0x210110, 85);
    using var engine = new ControlEngine(new FakeSource(memory));
    True(engine.ApplySettings(new(333, 75), none with { NumericPing = true }).CanMaintainOwned);
    memory.Put32(0x210110, 85);
    memory.SetCurrent(AdvancedDvar.PingGraph, 1);
    memory.AfterWrite = (_, address, _) =>
    {
        if (address == 0x210110) memory.Put32(address, 85);
        if (address == FakeMemory.Value(AdvancedDvar.PingGraph)) memory.SetCurrent(AdvancedDvar.PingGraph, 1);
    };
    True(engine.MaintainOwned(true).Success);
    memory.AfterWrite = null;
    True(engine.MaintainOwned(true).Success);
    Equal(333d, engine.Inspect().Features.Single(f => f.Id == "fps").ObservedValue);
    Equal(0, memory.Current(AdvancedDvar.PingGraph));
    True(engine.ResetSettings().Success);
});

Test("numeric ping fails preflight if either setting is unavailable", () =>
{
    var memory = FakeMemory.Create();
    memory.Missing(AdvancedDvar.PingGraph);
    var transaction = new AdvancedTransaction(memory);
    False(transaction.InspectFeatures().Single(feature => feature.Id == "ping").Supported);
    False(transaction.Apply(none with { NumericPing = true }, true).Success);
    Equal(0, memory.WriteCalls);
});

Test("combined apply and reset acquire once each and report retention eligibility", () =>
{
    var memory = FakeMemory.Create();
    var source = new FakeSource(memory);
    using var engine = new ControlEngine(source);
    var noOp = engine.ApplySettings(new(144, 90), none);
    True(noOp.Success);
    True(noOp.CanMaintainOwned);
    Equal(1, source.OpenCalls);
    var applied = engine.ApplySettings(new(165, 95), none with { NumericPing = true });
    True(applied.Success);
    True(applied.CanMaintainOwned);
    Equal(2, source.OpenCalls);
    var reset = engine.ResetSettings();
    True(reset.Success);
    False(reset.CanMaintainOwned);
    Equal(3, source.OpenCalls);
    Equal(8, memory.WriteCalls);
});

Test("combined apply never opens a replacement target between groups", () =>
{
    var memory = FakeMemory.Create();
    var replacement = FakeMemory.Create();
    var source = new FakeSource(memory);
    using var engine = new ControlEngine(source);
    memory.AfterWrite = (call, _, _) =>
    {
        if (call != 2) return;
        source.Memory = replacement;
        memory.IdentityValid = false;
    };
    var result = engine.ApplySettings(new(165, 95), none with { NumericPing = true });
    False(result.Success);
    False(result.CanMaintainOwned);
    Equal(1, source.OpenCalls);
    Equal(0, replacement.WriteCalls);
});

Test("combined advanced failure preserves successful tuning for reset", () =>
{
    var memory = FakeMemory.Create();
    memory.Missing(AdvancedDvar.PingGraph);
    var source = new FakeSource(memory);
    using var engine = new ControlEngine(source);
    var result = engine.ApplySettings(new(165, 95), none with { NumericPing = true });
    False(result.Success);
    False(result.CanMaintainOwned);
    True(result.Message.StartsWith("Partially applied.", StringComparison.Ordinal));
    Equal(1, source.OpenCalls);
    Equal(2, memory.WriteCalls);
    True(engine.ResetSettings().Success);
    Equal(2, source.OpenCalls);
    Equal(4, memory.WriteCalls);
});


Test("maintenance recovers a reset between planning and the next write", () =>
{
    var memory = FakeMemory.Create();
    using var engine = new ControlEngine(new FakeSource(memory));
    True(engine.ApplySettings(new(333, 95), none with { NumericPing = true }).CanMaintainOwned);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(90));
    memory.Put32(0x210110, 144);
    memory.AfterWrite = (_, address, _) =>
    {
        if (address == 0x210010) memory.Put32(0x210110, 85);
    };
    var interrupted = engine.MaintainOwned(true);
    False(interrupted.Success);
    True(interrupted.CanMaintainOwned);
    memory.AfterWrite = null;
    True(engine.MaintainOwned(true).Success);
    Equal(333, BinaryPrimitives.ReadInt32LittleEndian(memory.Raw(0x210110, 4)));
    True(engine.ResetSettings().Success);
    var writes = memory.WriteCalls;
    False(engine.MaintainOwned(true).CanMaintainOwned);
    Equal(writes, memory.WriteCalls);
});

Test("already-matching FOV FPS and ping retain requests through known game resets", () =>
{
    var memory = FakeMemory.Create();
    memory.SetCurrent(AdvancedDvar.PingText, 1);
    memory.SetCurrent(AdvancedDvar.PingGraph, 0);
    using var engine = new ControlEngine(new FakeSource(memory));
    True(engine.ApplySettings(new(144, 90), none with { NumericPing = true }).CanMaintainOwned);
    Equal(0, memory.WriteCalls);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(65));
    memory.Put32(0x210110, 85);
    memory.SetCurrent(AdvancedDvar.PingText, 0);
    memory.SetCurrent(AdvancedDvar.PingGraph, 1);
    True(engine.MaintainOwned(true).Success);
    Equal(4, memory.WriteCalls);
    True(engine.ResetSettings().Success);
    Equal(4, memory.WriteCalls);
    False(engine.MaintainOwned(true).CanMaintainOwned);
});

Test("temporary missing dvars recover but process lifetime invalidation stays stopped", () =>
{
    var memory = FakeMemory.Create();
    using var engine = new ControlEngine(new FakeSource(memory));
    True(engine.ApplySettings(new(333, 95), none).CanMaintainOwned);
    memory.Put64(memory.Identity.ModuleBase + KnownBuild.FovPointerRva, 0);
    var unavailable = engine.MaintainOwned(true);
    False(unavailable.Success);
    True(unavailable.CanMaintainOwned);
    memory.Put64(memory.Identity.ModuleBase + KnownBuild.FovPointerRva, 0x210000);
    memory.Put32(0x210010, BitConverter.SingleToInt32Bits(65));
    True(engine.MaintainOwned(true).Success);
    memory.IdentityValid = false;
    False(engine.MaintainOwned(true).CanMaintainOwned);
    memory.IdentityValid = true;
    False(engine.MaintainOwned(true).CanMaintainOwned);
});

Console.WriteLine($"Results: {passed} passed, {failed} failed, {passed + failed} total (synthetic only)");
return failed == 0 ? 0 : 1;

void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
static void True(bool condition) { if (!condition) throw new Exception("Expected true."); }
static void False(bool condition) { if (condition) throw new Exception("Expected false."); }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
}
static void Bytes(byte[] expected, byte[] actual)
{
    if (!expected.AsSpan().SequenceEqual(actual)) throw new Exception("Byte sequence mismatch.");
}
