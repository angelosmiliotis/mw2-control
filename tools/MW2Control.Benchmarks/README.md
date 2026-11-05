# Retention benchmark

Run on Windows x64 with the .NET 8 SDK:

```powershell
dotnet run --project tools/MW2Control.Benchmarks -c Release -- 30 3
```

The arguments are seconds per sample and repeat count. The tool alternates 500 ms full checks with 50 ms read hints plus 500 ms full checks. Each sample reports CPU time, allocations, working set, actual native read counts and stable writes as JSON. All eleven target values are active.

The tool starts its own child containing synthetic dvars. Reads and writes use actual Windows process-memory APIs against that child; the core transactions and validators execute normally through the test adapter. Native checks include process lifetime, image path, module and committed-page queries. It never discovers or opens a game. Stable samples must perform zero writes. A separate loop exercises recovery after twenty fixture FOV resets.

This measures the retention algorithm and native calls, excluding WPF, lobby scanning and game rendering. It cannot establish in-game frame times or the visible result of a graphics setting. Use a private match for those checks. The delay is a requested scheduling interval, not a real-time deadline.
