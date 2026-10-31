# nd-unity-client

An independent, from-scratch reimplementation of the classic **9Dragons** MMORPG client,
written entirely in **C# on Unity**. It renders the world, UI, characters, and combat by
speaking the game's network protocol and loading data at runtime — **no original game
assets or data are included in this repository.**

- **Author:** blackwind2026
- **License:** GNU Affero General Public License v3.0 — see [`LICENSE`](LICENSE)
- **Engine:** Unity 6 (6000.3.x)

## What this is
Original C# source for the client:
- World / terrain / map loading and rendering
- UI windows (inventory, character, quick-slot, target frame, …)
- Skeletal characters, equipment, animation driving
- Combat / skills and the network session layer

A clean, from-scratch implementation for **preservation, interoperability, and education**.

## What is NOT included (and why it won't build as-is)
- **No game assets or data.** Maps, models, textures, item/skill tables, etc. are not
  distributed here. Supply your own copy of the original game data to run a client.
- **No encryption modules.** Packet/data encryption code and keys are intentionally omitted.
  Provide your own transport/crypto layer to connect to a server.

This repository is therefore a **source reference**, not a ready-to-run build.

## Copyright & trademarks
"9Dragons" and all original game content are the property of their respective owners.
This is an **independent reimplementation**, not affiliated with or endorsed by the
original rights holders, and it ships none of their assets. All **source code** here is
© 2026 blackwind2026 and licensed under AGPL-3.0.

## Why this is open
This project is released as free and open source under **AGPL-3.0** to establish that an
independent **Unity** implementation of this client exists publicly and is available to
everyone. It exists so that no individual or company can falsely claim sole or exclusive
ability to build a Unity — or mobile — client of this kind, or lock it away for commercial
exploitation.

Because AGPL-3.0 is **strong copyleft**, anyone may use and build on this code — including
porting it to mobile — but **any derivative, including a mobile version, must also be released
as open source under AGPL-3.0.** No one, not even a competitor, can take this code, close its
source, and monopolize a proprietary mobile version of it. It remains common and open to all.
