# Why Lycoreia exists

Ranching Together proved that Slime Rancher 2 co-op is possible and laid the reverse-engineering
groundwork Lycoreia started from. Its authors are credited in [NOTICE.md](../NOTICE.md).

When we started playing it in real two-PC sessions, we kept hitting the same classes of problems:
lost packets over normal Wi-Fi, two players "owning" the same slime, items freezing mid-air when a
guest vacuumed them, and progress that only existed on one side. Fixing them meant changing the
foundations — ownership, netcode, vacuum and lifecycle — rather than patching symptoms.

That scale of change (125 new source files, 241 changed — about 40,000 lines added or rewritten) is hard to land as pull requests
to a project with its own direction and pace, so we chose to develop it as an independent
successor. The GPLv3, under which Ranching Together is published, explicitly gives everyone the
right to do this; we chose to use it, while keeping the original copyright and history intact.

This is not a statement against the original authors. It is a different set of priorities: a
host-authoritative design tested under bad network conditions, and frequent real-world test sessions.

## What we changed, and why

Each item below was found in real two-PC play or in our test harness, then fixed and confirmed in
real sessions for the 1.0.0 release candidate.

### Ownership and authority
- **Before:** a client that touched an item marked itself the owner and told everyone. Two players
  touching the same slime both became owners; a client could delete an actor from the host's world.
- **Now:** the host decides. Clients send interest; the host grants ownership with epochs, hold
  timers and distance priority, and rejects stale or unauthorised changes.

### Netcode
- The reliable UDP layer could drop already-acknowledged packets (first-arrival baseline, reorder
  buffer overflow), break at the 65535 → 1 sequence wrap, and kept stale state across reconnects.
- Now: zero baseline, wrap handling, 4096-entry reorder buffer, gap skipping, per-peer reset.
  Verified under a custom chaos relay that simulates loss, jitter and bad Wi-Fi.

### Vacuum
- Guests' long-range pulls were cut off: the capture path destroyed the game's own pulling spring
  joint, and the host rejected captures beyond ~4.5 m while the game's reach is 20 m.
- Now: the native spring joint is preserved and guests have the full native reach.

### Visuals
- The other player's suction cone was clipped to nothing by the shader's local-player-only uniforms.
  Now overridden per renderer, with physical sway. The vac shockwave is synced too.
- Thrown items were pulled toward the other player by an over-wide force field. Fixed.

### Progression and economy
- Treasure pods, blueprints, slime appearances, bought vac skins, market saturation and plort sales
  now go through the host, which keeps the history and sends it to joining players. Shop purchases,
  upgrades and currency are host-authoritative, and every player sees treasure pod animations and
  reward popups.

### Missing features from the original
- Guest inventories were not stored (the original asked guests to reuse the same save); they are now
  kept per host. Tangle and Tabby behaviour, Gray Labyrinth Prisma disruption and stabilizing fields,
  and Sanctuary rooms and themes are now synced.

### World safety
- In co-op, the game's population cleanups counted other players' copies as local population and
  deleted ranch slimes, hens and plorts. While connected, the ranch is now exempt from those cleanups.
- A wild spawner could spawn animals endlessly ("chicken waterfall"); spawns are now capped while
  connected.

### Performance
- Per-actor visibility tracing ran several component searches every half second and produced tens of
  megabytes of logs per session. It is now opt-in, and diagnostics are off in player builds.
  
### And much more!

Technical details (engine internals, offsets, measurements) live in
[TECHNICAL-NOTES.md](TECHNICAL-NOTES.md).
