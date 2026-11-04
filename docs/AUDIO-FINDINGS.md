# Audio

Stream mute changes the current value of `snd_enableStream` to 0 on the supported Steam build. Reset restores its captured original value if the current value can still be verified.

This mutes streamed background audio while preserving gameplay sounds such as footsteps, player gunfire, explosions and the bomb timer. The control applies to streamed sounds as a group, so its effect depends on how each sound is stored by the game.

The app changes the current dvar value. It leaves game archives and sound assets intact. The dvar mapping and type are checked before writing. The command `snd_restart` is unnecessary for this implementation and previously caused a crash when sent through an external console.
