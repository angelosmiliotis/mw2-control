# Selective audio — next stage, not shipped functionality

Goal: distinguish soundtrack, ambient battlefield loops, S&D round stingers/announcer/objective cues, and gameplay weapon/footstep audio. Do not present master mute as selective mute.

Known finding: friend's music switch only writes snd_enableStream=0. Streamed vs loaded is a storage/playback distinction, not a semantic 'unwanted sound' classifier. Some desired and unwanted sounds may share channels, so a category-volume change may have collateral effects. Current x64 mappings require separate evidence, not blind reuse of IW4x x86 structure layouts.

Next bounded investigation: offline inspect available local asset metadata and existing dumps, classify an explicit small set of aliases per map/mode, then assess whether supported channel/dvar controls exist. Do not inject hooks or run snd_restart to discover this. No live game reads/writes are required by the current build tasks.

If selective suppression cannot be achieved reliably by validated external data controls, report that limitation. A supported IW4x mod adapter is a separate target/client, not a substitute advertised as Steam-public compatible. Changing IWD/FF files is excluded because of integrity/server compatibility issues already observed by the user.

Acceptance for any later audio feature: preserve actual footsteps and player weapon fire in a controlled test, confirm round start/end and bomb cues separately, survive map changes, be independently reversible, and display its exact scope. No 'all sound removed' or 'music only' claim based solely on snd_enableStream.
