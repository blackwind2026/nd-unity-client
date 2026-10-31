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

## Why AGPL-3.0
You may use, study, modify, and share this code freely — but **any distributed or
network-served derivative must also release its complete source under AGPL-3.0.**
Closed-source commercial redistribution is not permitted.
