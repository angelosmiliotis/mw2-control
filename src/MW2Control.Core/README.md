# Core / QA adapter surface

`ControlEngine()` uses the Windows adapter. `ControlEngine(IProcessSource)` is the
dependency-free fake boundary; no native calls are made when a fake source is used.
`IProcessSource.OpenTarget()` returns null for no game, a new owned accessor (or the
same instance), or throws `TargetProcessException` for ambiguity/access failures.
The engine disposes replaced or redundant accessors, retaining the transaction
ledger only when every `ProcessIdentity` field matches. No implicit restore on
dispose, process exit, refresh, or permission changes.
The facade remembers pending writes from a previous lifetime and explicitly refuses
Restore into a replacement process, even when a Refresh already discovered it.

For isolated tests construct `TuningTransaction(IProcessMemory)` and call
`Apply(request, confirmed)`, `Restore(confirmed)` or `InspectFeatures()`.
The transaction does not dispose its accessor. Public `TuningValidation` performs
pure request, profile, address and observed-value checks. Public
`DvarValidator.Resolve(memory, feature, requireWritable)` validates exact names,
pointer slots and value bytes; its default is read-only.

Fake setup: use the public `KnownBuild` constants, a page-aligned module base and
module size greater than `MaxFpsPointerRva + 8`. Identity needs a positive PID and
UTC start ticks, AMD64=true, an absolute executable path ending in iw4mp.exe,
and the exact approved SHA256. Place
8-byte little-endian dvar pointers at module base + each pointer RVA; put the
8-byte ASCII-name pointer at the dvar address, then a float32 FOV/int32 FPS at
dvar + 0x10. Each exact name ends in NUL. Dvar pointers need 8-byte alignment;
names need no alignment. `QueryRegion` must cover each entire requested range.
Use committed `ReadWrite` data pages to allow explicit operations. Return false
from `ValidateIdentity` for stale session/map tests.

`ReadMemory` returns bytes, allowing short-read simulation. `WriteMemory` returns
the contiguous prefix byte count actually written, allowing short-write tests.
`MemoryWriteException.BytesWritten` reports a known partial native failure.
Throwing a generic exception simulates unknown write completion; rollback will
act only if the bytes exactly equal the intended value (or known written prefix).
Rollback and Restore re-resolve the dvar/name/protection/session and compare owned
bytes immediately before every write. A failed rollback preserves whatever known
ownership can be retained, permitting a later guarded Restore when possible.

The fixed profile's RVAs remain unverified against a live game in this preview.
Unknown hashes are diagnostic-only and never trigger pointer resolution. External
ReadProcessMemory/WriteProcessMemory cannot offer atomic compare-and-swap against
other writers; a race between final validation and the OS write remains possible.
No anti-cheat safety assurance is made. No native/live game tests are authorized.

## Stage 2 guarded advanced controls

`ControlEngine.ApplyAdvanced(AdvancedSettings, confirmed)` and
`RestoreAdvanced(confirmed)` use a separate ownership ledger from FOV/FPS.
`AdvancedTransaction(IProcessMemory)` exposes the same operations and independently
checked readings for synthetic tests. `AdvancedValidation`, `AdvancedDvarValidator`
and `DvarWritePolicy` are public, dependency-free validation surfaces. Real native
writes repeat the fixed whitelist check, and acquire a temporary write handle only
for the explicit write call. Optional missing/invalid features do not block FOV/FPS.

False booleans and null specular mode restore only owned originals, never guessed
defaults. A requested unsupported control fails the entire transaction preflight.
Detail overlays combines the boolean r_detail=0 with enum r_detailMap=0 (Hidden).
It is **not guaranteed No Camos**. Stream mute is only music plus some streamed
audio, with no snd_restart; selective ambient and S&D filtering remains unsupported.

Snapshot ids `music`, `fullbright`, `fog`, `glow`, `detail` report 1 when the named
feature is active, otherwise 0, with raw dvar values in Detail. `specular` reports
the raw enum index: **0 Black, 1 Unchanged, 2 White**, and observed 3 Gray is allowed
only as a captured original to restore. Each optional reading catches its own
validation failure. The original `TuningTransaction.InspectFeatures()` still has
exactly its original two FOV/FPS readings; the facade appends six optional readings.

Stage 2 FPS requests accept **0 = explicitly uncapped**, or 30..500. An observed 0
is valid and restorable. `ResetSession()` is an explicit, serialized read-only
action: it releases native handles, forgets both restore histories and previous
lifetime markers, then reinspects. It does not restore or change game values.
Fake sources used for ResetSession tests must return a fresh accessor after the
previous accessor is disposed, matching real discovery behavior. UI must confirm
loss of restore history and reset its private/offline acknowledgement separately.

## Reproduced offline mapping evidence

Read-only inspection of the installed iw4mp.exe independently confirmed SHA256
`926F8A47959F2B24420898D07F599883E0866050D42D5289F94EEF546A4A3492`.
The original stdlib-only `Tools/inspect-profile.py` checks this hash before parsing
the PE on disk; it never executes it or opens a process. It locates exact NUL-ended
names and prints all code LEA-RCX name references, nearby calls and RAX stores.
Each listed name had exactly one such registration reference. Decoded registration
paths in that image:

| Dvar | Name LEA RVA | Registration call RVA | RAX cache pointer RVA |
| --- | --- | --- | --- |
| snd_enableStream | 0x234ECA | 0x234ED3 -> 0x274840 | 0x65449E0 |
| r_fullbright | 0x2E132 | 0x2E13D -> 0x274840 | 0x8CF78C8 |
| r_fog | 0x2E440 | 0x2E449 -> 0x274840 | 0x8CF7C08 |
| r_glow | 0x2F79E | 0x2F7A7 -> 0x274840 | 0x8CF7D78 |
| r_specularMap | 0x2E6E8 | 0x2E6F3 -> 0x274B20 | 0x8CF7880 |
| r_detail | 0x2E5B7 | 0x2E5C0 -> 0x274840 | 0x8CF7DB0 |
| r_detailMap | 0x2E688 | 0x2E693 -> 0x274B20 | 0x8CF78A8 |

The bool wrapper at 0x274840 takes the initial value from DL as a byte, builds a
zeroed value union, and passes kind 0 to registration. The enum wrapper at
0x274B20 counts a NUL-terminated pointer table, builds count/pointer domain fields,
and passes kind 6. The allocator at 0x274E40 stores the type byte at +0x0C, the
value union at +0x10, and the 16-byte domain at +0x40 (enum count +0x40, table
pointer +0x48). Runtime validation requires these exact type/domain shapes.
Advanced booleans write **one byte** at +0x10; enums write four. Flags, adjacent
union bytes, latched +0x20 and defaults +0x30 are never changed.

Specular domain table RVA0x577860 contains four strings at RVAs0x3C2F14 (Black),
0x3C2F20 (Unchanged), 0x3C2F2C (White), 0x3C2F34 (Gray), then NULL. Detail-map
table RVA0x5779C0 contains 0x3C2F3C (Hidden), 0x3C2F20 (Unchanged), then NULL.
The validator requires the exact count, relocated table pointer, each relocated
string pointer, exact label bytes and terminator, not just a plausible integer.

Reproduction (read-only):

```powershell
python src/MW2Control.Core/Tools/inspect-profile.py '<absolute iw4mp.exe path>' 0x274840:0xc0 0x274b20:0x100 0x274e40:0x2a0 0x577860:0x30 0x5779c0:0x20 0x3c2f10:0x40
```

All tests in `tests/MW2Control.Advanced.Tests` use sparse fake memory and never
construct the native source. Offline evidence establishes mappings and data
layouts, not the live audio/visual effects, anti-cheat compatibility or persistence
across game transitions. Those remain user verification items.

## 2026-11-03 live-test repair and guarded Keep applied

Value drift does not prove a second tool exists. Conflict and read-only inspection
details now identify the dvar, observed value and last-owned value (or captured
baseline), and explain that the game itself can reset values. A user-triggered
Apply accepts either the last-owned bytes or the **exact independently captured
original bytes**, in the same lifetime and dvar mapping. It never changes the
captured originals. Unrelated third values still require explicit Reset baseline;
Restore, false advanced booleans and null enum mode accept the last-owned bytes
or the exact captured original. An already restored value clears ownership
without a write after successful transaction verification.

`ControlEngine.MaintainOwned(bool privateSessionConfirmed)` provides exactly one
caller-scheduled tick. It does not call `OpenTarget`/Acquire, reopen the inspected
session, hash files, create a timer, or capture new originals. The UI must keep this
off by default, require a successful manual Apply and private/offline confirmation,
serialize bounded-rate ticks, and stop on any failure, revoked acknowledgement,
manual Scan/Reset/Restore or close. Stopping does not restore. Native writes still
use the existing temporary minimal-rights write handle only when bytes need writing.

The two public transaction methods `TuningTransaction.ReapplyOwned()` and
`AdvancedTransaction.ReapplyOwned()` act only on successfully applied owned
targets, after whole-group preflight. An unchanged target causes no write. An exact
original-byte reset can be reapplied. Any third value, lifetime/pointer change,
protection/name/type/domain error or failed readback stops that group and latches
maintenance off until another successful manual Apply. Partial failed Apply bytes
cannot seed a maintenance target. Originals remain available for strict Restore.
Tuning and advanced groups are individually transactional; maintenance across both
groups is **not atomic**. If advanced preflight fails after tuning succeeds, tuning
can already be reapplied and the result explicitly reports this before stopping.

Maintenance adds a stricter FOV/FPS eligibility check than the original one-shot
fake-adapter contract: it captures kind +0x0C and the two bound values +0x40/+0x44
at the first baseline capture, requiring the approved image's registration metadata.
It checks these unchanged before every maintenance write/readback/rollback. Test
fakes must supply FOV kind1/float domain65..80 and FPS kind5/int domain0..100;
legacy fakes without that data may still exercise one-shot APIs, but cannot seed
maintenance. Advanced bool/enum name/type/domain checks already run on each resolve.
No bounds, flags, latched/default fields or executable bytes are written.

Additional independent offline PE evidence from the same approved SHA256:

* `cg_fov` name LEA at RVA0xD75FE, registration call0xD762B -> float wrapper0x274C20,
  cached pointer store0xD7659 ->0x809C78. XMM1 loads float65 from0x363DBC, XMM2
  copies it as the minimum, XMM3 loads float80 from0x359C88 as the maximum.
  The float wrapper passes kind1 to registration.
* `com_maxfps` name LEA0x1F43D1, call0x1F43E3 -> integer wrapper0x274D50,
  store0x1F43E8 ->0x1D260A8. Zeroed EDI supplies minimum0, EDX gets0x55 (85)
  as default, R9D gets0x64 (100) as maximum. The wrapper passes kind5.
* Friend reference `docs/how-it-works.md` documents cg_fov being reinitialized on
  respawn/load and its watchdog; `src/features.cpp` also lists broader current,
  latched/default writes and FOV max-domain modification. Those mechanisms were
  inspected as text only, not copied, executed or adopted here.

Reproduce the registration/default evidence without a process:

```powershell
python src/MW2Control.Core/Tools/inspect-profile.py '<absolute iw4mp.exe path>' 0xd75e0:0xb0 0x363db0:0x20 0x359c80:0x20 0x1f43b0:0x50 0x274c20:0x130 0x274d50:0xf0
```

Reported 65/85 after immediate successful 100/0 readback matches these registered
defaults and is consistent with game-side reset, but this offline investigation
does not identify the exact later writer or prove when it ran. The user reports
the symptom inside a map as well. Keeping raw current values applied may mitigate
resets, but cannot guarantee the rendered/audio effect: in particular FOV100 is
above the unchanged declared FOV maximum80. Live persistence/effects and anti-cheat
compatibility remain unverified by this worker. All new tests use fake memory.

Numeric scoreboard ping uses the verified kind-0 caches `0x80E5C0` and
`0x80E5F8`. Registration calls at `0xE56A4` and `0xE56C3` both target
`0x274840`. The toggle sets the current text byte to 1 and the graph byte to 0.
Both dvars must pass preflight. Snapshot feature ID is `ping`.
