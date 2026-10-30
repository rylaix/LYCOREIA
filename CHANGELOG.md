# Changelog

## 1.0.0-rc.16

### Changed
- Credits: Rylaix (core developer) and Kum1te (core QA) are now named in the mod info, the main-menu
  watermark and the README. Upstream credits moved to NOTICE.md.

## 1.0.0-rc.15

### Added
- New chat built with the game's own UI: Enter opens and sends, Esc closes, F5 hides, mouse wheel
  scrolls. Messages fade out after a few seconds; sounds for opening, typing and sending.

### Fixed
- Text fields in the multiplayer menu no longer lose focus right after being clicked.

## 1.0.0-rc.14

### Added
- Host a world or join a friend straight from the main menu: pick a save to host (join code or fixed
  port), or join with a code or IP on a dedicated co-op save that is created automatically.
- Nickname colour picker: palette, hex code, colour square, or random.

## 1.0.0-rc.13

### Fixed
- Other players' avatars: no more body or vac clipping when they look far up or down.

## 1.0.0-rc.6 – rc.12

### Added
- Native multiplayer menu ("Multiplayer" in the main menu and pause menu) built with the game's UI:
  host, join (code or IP), settings with a nickname color palette, player list, resync, disconnect.
  The old F4 window remains as a fallback.
- Other players' avatars: the head follows where they look, the torso leans with the look angle,
  breathing and head sway, an occasional "hand warm-up" and look-around while standing still, and the
  left hand holds the vac's front grip with curled fingers.
- Nicknames scale with distance so far-away players stay readable.

### Fixed
- Other players' vac arm no longer tears the shoulder or clips through the body when they look up or
  down; the vac follows their look direction.
- Map discovery icons opened by a guest now disappear for that guest too.
- Opening the multiplayer menu no longer freezes the main-menu background.
- Wild-animal spawn cap no longer throws errors inside the game every second (and no longer stops
  animal spawning).

## 1.0.0-rc.5

### Fixed
- Guests over the internet could miss the host's whole world on join (no breakable containers, none
  of the host's placed decorations, pickups not removed): the join snapshot is sent in hundreds of
  pieces, and the same last pieces were lost on every resend. Large messages are now resent paced,
  starting from a different piece each time, for up to 30 seconds; network buffers raised to 2 MB.

## 1.0.0-rc.4

### Fixed
- The game still crashed on startup with rc.3. The cause was a hook on the game's spawner
  (`OnActorSpawned`); it has been removed. The wild animal spawn cap now only limits spawning while
  12 or more animals are already near a spawner.

## 1.0.0-rc.3

### Fixed
- The game crashed on startup (black screen) with rc.1 and rc.2: the wild animal spawn cap ran
  before the mod had finished starting.

## 1.0.0-rc.2

### Fixed
- Guests can vacuum, hold and throw breakable containers (the stone "vases" that break into
  resources). They were treated as storable items, so the guest's pull was cancelled every time and
  only the host could pick them up.

## 1.0.0-rc.1 — first Lycoreia release candidate

Forked from **Ranching Together 0.3.9** (upstream revision `619bc4c`, 2026-09-24; credits in NOTICE.md).
Compared with that version, Lycoreia adds 125 source files, changes 241 and removes 1.
Earlier Lycoreia builds were internal test builds (`0.3.9-coop.N`); all of their changes are listed here.

**Not compatible with Ranching Together:** players of the two mods cannot join each other.

### Identity
- New name **Lycoreia**, mod id `com.rylaix.lycoreia`, own update check, Discord presence and server.
- In-game watermark: name, version and credit in the main menu; version tag in the corner in game.
- Own data folder `UserData/Lycoreia` (copied once from `UserData/SR2MP` on first start) and own
  Windows firewall rule names, so it never mixes data with Ranching Together.

### Authority & ownership
- Host-authoritative ownership: clients send interest, the host grants (two-phase commit, epochs,
  hold timers, distance priority) and rejects stale or unauthorised changes.
- Host-authoritative capture of held items and Tarr (non-consuming commit so the holder can throw).
- Destroy ordering and authorization: a client can no longer delete actors it does not own;
  any player may pick up a placed gadget.
- Inactive host-owned actors are handed to a nearby guest instead of becoming unreachable ghosts.

### Netcode
- Reliable UDP layer: zero baseline (no more lost early packets), 65535 → 1 wrap fix, 4096-entry
  reorder buffer with out-of-order delivery, 12 s gap skip, per-peer reset on reconnect.
- Server heartbeat and stale-session timeout; connection generations so old packets never apply to a
  new session.
- World revisions and snapshot/delta ordering for actors and other join snapshots.

### Vacuum & physics
- Native spring joints preserved: guest long-range pulls no longer freeze mid-air.
- Guests have the game's full 20 m vacuum reach (was ~4.5 m).
- Local vacuum prediction for responsive capture; mass capture of many items at once.
- Thrown items are no longer pulled toward other players.

### Player
- Remote suction cone visible (shader uniform override) with physical sway; vac shockwave synced.
- Guest vacuum inventory stored per host and restored on rejoin.
- Per-player vac skins, changeable mid-session, visible to everyone.

### Slimes & world
- Tangle vine grapple and Tabby stalking synced; slime eating and behaviour decided by the owner.
- Gordo reward recovery.
- Ranch protection: while connected, population cleanups, timed cleanups and fall-outs never delete
  slimes, animals or plorts on the ranch (Tarr and produce keep vanilla rules; fallen actors are put
  back on the ground).
- Slimes are never population-culled anywhere while connected.
- Wild animal spawn cap while connected (fixes endless hen spawning, "chicken waterfall").
- Gray Labyrinth: Prisma disruption and stabilizing field spawners synced.
- Sanctuary: room state and theme ownership synced.
- Teleporter unlinking synced; weather sync policy.

### Progression & economy
- Treasure pods: the host keeps the history; open state, opening animation, rewards and reward popups
  (blueprint, upgrade component, slime appearance) reach every player; pods with leading-zero ids work;
  a guest can open a pod the host is not near.
- Progression sync: blueprints, slime appearances and owned vac skins (full set on join, live additions).
- Host-authoritative shop purchases, upgrade purchases and currency.
- Host-authoritative market saturation for plort sales.
- Build-mode inventory (gadget storage) sync, including the join snapshot.
- Gadget placement held until ownership is known; host rejections clean up on the placing player.

### Performance & diagnostics
- Per-actor visibility tracing removed from player builds; settled remote actors stop rewriting
  transforms; force field uses non-allocating physics queries.
- Optional deep diagnostics build (`-deeplog`) with rotating `diagnostics.jsonl`.

### Removed
- `NewbuckRewardPatch` (currency is now handled by the host-authoritative currency manager).

### Known issues
- Garden crops can occasionally differ between players.
- Water hitting a Tarr shows no effect for guests; guests cannot always kill Tarr with water.
- Cotton Hunter largo aura can desync.
- Jetpack effects of other players are not shown.
