# Audio

Selective mute applies to known music, round announcements and match countdowns on the supported Steam x64 build. The planted bomb timer and gameplay effects keep their original volumes.

Lobby music is handled separately because the game can replace its audio stream while changing screens. The app rechecks it while settings are active. Reset restores volume values that still belong to the same sound in the same game session.

The game needs audio streaming enabled. If another tool disabled it and the game saved that change, set `snd_enableStream` to `1` in `players/config_mp.cfg` with MW2 closed, then relaunch.
