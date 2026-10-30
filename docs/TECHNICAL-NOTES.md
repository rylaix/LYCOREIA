# Technical notes

Engine-level findings behind Lycoreia's fixes. Offsets are for the game build named in each section
and change with every game update; treat them as historical notes, not stable API.

## Vacuum: native spring joints (game 1.3.0)
- Long-range pulls use dynamic `SpringJoint`s stored in `VacuumItem._joints`
  (`ConsumeExistingJointed`, RVA `0xA63470`).
- The original guest path checked `LockJoint`, assumed the pull had failed and called
  `Capture(LockJoint)`. Native `Vacuumable.SetCaptive` (RVA `0x644200`) destroys the previous joint's
  GameObject, killing the active pull, so items beyond ~2.5 m froze mid-air.
- The host also limited guest captures to `CaptureDist + 3 m` (~4.5 m) while `MaxVacDist` is 20 m.

## Remote vacuum cone shader
- `AMP/FX/VacCone` clips against the global uniforms `_VacVisualWorldPosition` and `_VacWindDistance`,
  which the game only sets for the local player. Remote cones are drawn with a per-renderer
  `MaterialPropertyBlock` override of both values.

## Reliable UDP layer (`ReliabilityManager`)
- First-arrival baseline dropped earlier, already-ACKed packets → baseline 0 / `NextSequence`.
- Sequence wrap 65535 → 1 vs. expected 0 → explicit wrap handling.
- 64-entry reorder buffer overflowed under jitter → 4096 entries plus out-of-order delivery placeholder,
  12 s gap skip.
- No per-peer reset on reconnect → `ResetPeer` on client add/remove.

## Treasure pods
- `TreasurePod` does not observe its model: it redraws only when `_forceUpdate` is set in `Update`.
  Network state must set it explicitly (animated for live opens, `UpdateImmediate` on load).
- Save keys keep leading zeros (`pod0118409926`) while the wire id is an integer; lookups resolve the
  real key instead of formatting `pod{id}`.
- Blueprint reward popups come from the gadget's Pedia entry (`PediaDirector.ShowPopupIfUnlocked`);
  component and appearance popups use `PreviewPopupViewModel(component|appearance, config)`.

## Population cleanups in co-op
- `CellDirector.Despawn`, `MaybeCullOnReenable.MaybeDestroy` and `DestroyAfterTime` count replicas and
  other peers' actors as local population. The ranch test uses the game's own
  `CellDirector.IsOnRanch(RegionMember)`: the `ConservatoryFields` scene group also contains
  Rainbow Fields, so a scene-group test would protect the wilds too.

## Measurements
- Chaos relay, `bad_wifi` profile: 16/16 simultaneous captures consumed, 0 reliable-packet failures.
- Visibility tracing (now opt-in) produced ~57 MB of logs per session before the change.

## Wild animal spawning ("chicken waterfall")
- Observed in Rumbling Gorge: ~1,370 hens in 3 minutes from one host, peaking at 21 spawns/s, plus
  ~440 from the guest at the same spawners. The native population check did not see animals owned or
  simulated by the other peer.
- While connected, `DirectedAnimalSpawner.CanSpawn` and `CellDirector.NeedsMoreAnimals` return false
  once a spawner has 12+ animals within 45 m (any owner, from the networked actor table).
- **Do not Harmony-patch `DirectedActorSpawner.OnActorSpawned`** (the game crashes silently while
  loading) **or `DirectedActorSpawner.CanContinueSpawning`** (NullReferenceException inside the
  Il2Cpp trampoline). Found by bisecting builds with a startup smoke test.

## Build-mode inventory
- `GadgetsModel._itemCounts` is held by the game's UI and directors: the join snapshot is applied into
  the existing dictionary, never by replacing it. Unknown item types are skipped after 10 s instead of
  blocking the snapshot forever.
- A 1 s reconcile pass publishes count changes made by native paths the `SetCount` patch misses.

## Gadget pickup
- Placed gadgets have no physics owner, so the host accepts a gadget destroy from any connected
  player. Rejecting it had left the gadget on the host while the picker's +1 inventory count synced,
  causing a duplicate.

## Separate identity from Ranching Together
- Mod id `com.rylaix.lycoreia`; data in `UserData/Lycoreia` (`ModPaths.DataDirectory`, one-time copy of
  `UserData/SR2MP` without logs); firewall rules `Lycoreia UDP In/Out <port>`. MelonPreferences keep the
  `SR2MP` category so existing settings carry over.
