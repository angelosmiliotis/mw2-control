# Selective audio feasibility (offline)

## Evidence found

- Local reference `MusicAliasProbe/Program.cs` documents an x64 structural hypothesis: `snd_alias_t`-like records include a mixer pointer, volume fields and a channel encoded in flags; it traces a menu-music asset to an alias and states the observed retail x64 music `entchannel` as 13. This is source-code evidence from an existing probe, not an independently reproduced result for the supported executable/build.
- The probe also searches loaded process memory and contains an optional `--mute-test` write path. It was inspected as text only; it was not run. Its dynamic scan, inferred offsets, and mutating path are not suitable as a validated mapping for this app.
- The local friend reference describes `snd_enableStream=0` as a stream toggle. Streamed-vs-loaded does not establish semantic categories. No verified alias list or per-category mapping for ambient battlefield sounds, S&D stingers/objective/announcer cues, footsteps, and weapon audio was found in this bounded inspection.

## Blockers and limits

- Channel 13 is only a candidate music channel from the local probe; no independent build-fingerprint-bound validation or collateral-audio test establishes that muting it preserves desired cues.
- Alias memory is runtime-loaded and can change with menu/lobby/map transitions. A heuristic scan cannot safely become a persistent write target without stronger build-specific evidence and lifecycle validation.
- No evidence found for a reliable external data control that isolates ambient or S&D sounds while retaining gameplay cues. A global stream toggle is too broad and must not be labeled music-only.
- No live process was accessed; no sound behavior or reversible control was tested.

## Smallest honest next step

Keep selective ambient/S&D controls unsupported. If pursuing further, perform offline asset metadata/alias classification for a small explicit set of sounds, then require build-bound structural validation and controlled user testing that separately checks footsteps, weapon fire, and S&D cues across map changes. Do not implement a write based only on the probe's candidate channel or `snd_enableStream`.
