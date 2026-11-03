# First preview — integration verification

Date: 2026-11-02 (Europe/Athens).

## Verified

- Standalone WPF Release build: 0 warnings, 0 errors.
- Framework-dependent Windows x64 publish: `out/first-preview/MW2Control.exe`.
- Dependency-free synthetic tests: 21 passed, 0 failed, rerun after the final core changes.
- Real locally built application launched through Windows app controls.
- Actual no-game read-only scan showed GAME NOT RUNNING; Apply/Restore disabled.
- Explicit Window style, selected content host, dark content area, scrollbar and native dark caption visually inspected after correction.
- No live game was launched, no game memory was written and no Steam files, registry or antivirus configuration were modified by this build workflow.

## Not verified / not implemented

- Native in-game FOV/FPS behavior and visual effect remain unverified. The approved hash and named data addresses are guarded reference mappings, not a claim of live testing.
- No promise of VAC compatibility. No anti-cheat bypass is included.
- Audio and advanced visuals are disabled roadmap pages, not functional features in this preview.
- Full navigation, DPI, keyboard and resizing matrix is not complete. Human input was detected during the last automated navigation attempt, and automation was not forced over the user.
- External memory validation/write has an unavoidable race against other writers; don't run multiple tuners together. No auto-restore on close. Use explicit Restore in the same process session, or restart the game to discard runtime values.

## Economy and coordination

Four user-requested sibling chats in the existing project: 00 ADMIN, 01 CORE, 02 UI, 03 QA. Worker prompts use one bounded shared brief rather than full chat history. Core: GPT-6.1 Sol/high. UI: GPT-6.1 Sol/medium, small polish edits: Luna/low. QA: Luna/high. Admin: Luna/medium then low. No Astra workers were created. No background agent monitoring loop or auto-updater was installed. Coordinator's existing chat model was not changed or asserted.

## Suggested next acceptance gate

With other tuners closed, obtain an explicitly approved read-only inspection of the user's private/offline game session; verify exact dvar names/current values and UI state. Only after that, user-controlled isolated Apply/Restore testing (FOV first, FPS second, then map/respawn checks). Keep selective audio research separate and offline initially; see AUDIO-RESEARCH.md. Do not add speculative memory fields just to light up roadmap controls.
