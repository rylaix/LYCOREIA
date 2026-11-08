# Changelog

## [2.0.0] - 2026-11-08

Lycoreia 2.0.0 is a ground-up rewrite of the co-op mod by Rylaix (core developer) and Kum1te (core QA). The networking, session, actor, world and menu code of 1.x has been replaced with new systems written from scratch. Lycoreia no longer needs Starlight, installs as a plain MelonLoader mod, and uses a new wire protocol, so every player must run the same 2.0.0 build. Players on 1.x cannot join 2.0.0 sessions.

Several 2.0.0 fixes are built and tested, but are still waiting for confirmation in full multiplayer sessions. They are listed under Known issues.

### Highlights

- Lycoreia is a normal MelonLoader mod. Starlight is no longer needed.
- A new networking layer with its own session handling, join codes and automatic hosting.
- Lycoreia opens its own UPnP and NAT-PMP port mappings.
- More of the world is shared between players: time, weather, switches and doors, puzzles, the map and its fog, the Slimepedia, gardens, drones, Gordos, treasure pods, the refinery, the market, the shop, upgrades, teleporters, resource nodes, placed decorations and gadgets, and world events.
- Slimes and plorts change hands between players through an ownership system that keeps one authoritative copy per actor. The host confirms captures before they are committed.
- Ranch slimes are protected from the game's dawn and re-enable cleanups.
- Shared money: sales and income are credited once, and other players see a "+money" notice when you earn.
- Remote players have avatars with walking, running, jumping, look, vacuum-arm and kick animation, nametags, pings, chat and map markers.
- Lycoreia has its own in-game menu toolkit. The Multiplayer launcher appears on the main menu and on the pause menu, and our menu sits above the pause menu.
- Discord Rich Presence is built in, for Windows and for Linux players running the game through Proton.
- Better diagnostics: per-session logs, automatic save backups, and startup identity records so each log can be matched to its build.

### Installation changes

- Put Lycoreia.dll in the Mods folder on every seat, as a plain MelonLoader mod.
- Remove Mods/Starlight.dll. Lycoreia 2.0.0 does not load it.
- Remove UserLibs/SharpOpenNat.dll. Lycoreia has its own port-mapping client.
- Remove UserLibs/DiscordRPC.dll. Discord Rich Presence is built into Lycoreia.
- Remove the old UserData/Lycoreia/*.lic.txt files left over from 1.x.
- All seats must run the identical 2.0.0 build. A session refuses a player on a different build and shows a readable message.
- Lycoreia 2.0.0 is not compatible with 1.x. Host and join with 2.0.0 on every seat.
- Settings are stored with MelonPreferences in the Lycoreia category. The "Discord Presence" setting is on by default.

### Networking and sessions

- New wire protocol. Every message is described once and sent with a compact, versioned header that includes a link epoch.
- Messages are compressed with LZ4 and checked with a CRC32, so corrupted packets are dropped instead of applied.
- Reliable messages are acknowledged and resent at a paced rate, with a reorder buffer, so lost or out-of-order packets are handled in order where order matters.
- Actor movement uses its own unreliable lane, so a late position update does not hold up ownership or capture messages.
- A peer that restarts on the same address is detected from its new link epoch, so stale state from the old session is discarded.
- Incoming packets are checked for truncation and garbage. Malformed input is rejected without crashing the game.
- Join codes: a host can share a short join code. Guests join with the code, or with an IP address and port.
- The host approves or denies each join request. A denied player sees the reason.
- Heartbeats and timeouts detect dropped connections. A guest treats any packet from the host as proof that the host is alive, so a long scene load does not look like a disconnect.
- A player who reconnects replaces the stale connection with the same player id.
- The host and each guest exchange their build version when they connect, and mismatched builds are refused.
- Own UPnP IGD client: discovery on every network adapter, unicast discovery to each gateway, IGD v1 and v2, WANIP and WANPPP services, SOAP port mapping with lease fallback.
- Own NAT-PMP fallback for routers that do not support UPnP.
- Clearer port-mapping errors: a timeout, "no answer" and an error on a particular adapter are reported separately, with the adapters and gateways that were tried.
- Automatic hosting shows each setup step, has a Cancel button, and stops after a 45-second deadline instead of waiting indefinitely.
- Automatic resync: if a guest fails to apply the world snapshot, it retries automatically, up to a set limit.
- Manual resync has a 2-minute cooldown and a 5-second spam guard, and a refused request says why.
- Join snapshots are sent in pages, so a save with thousands of actors joins without one long stall.
- The host stamps each shared change with a revision, so every seat applies updates in the same order.
- Shared requests (plort sales, shop purchases, Gordo feeds, captures) carry request ids that the host checks, so a retried request is applied once.
- A request id that is reused by a different player, or with a different payload, is rejected.

### Shared world

#### Time and weather

- The host owns the world clock. Sleeping on the host moves the guests' clock too.
- Weather is host-authoritative per zone. The host sends each zone's forecast and the state of its running weather patterns.
- Guests rebuild a zone's weather from the host's state, create missing weather patterns, stop obsolete ones, and reapply the state every two seconds, so weather still shows after an island loads or a model appears late.
- Each island follows the host's state for that island. Two islands may legitimately have different weather.

#### Switches, doors, puzzles and depositors

- Switches, doors, puzzle slots and plort depositors are synced as keyed state. A change on one seat appears on the others.
- A guest publishes only the keys that the host's snapshot established, so a zone loaded from a guest's own save cannot overwrite the host's state.
- Switch and puzzle updates wait for their scene object if it is not loaded yet, and are applied when it loads.
- Ember Valley's primary sphere switch is synced, and its portal state is sent to the other seats.
- Teleporter puzzle locks are reconciled with the host's state, so a guest's finished puzzle does not open a gate the host has closed.

#### Map, fog and physical map nodes

- Map fog is host-authoritative. A reveal on one seat reveals the same zone on the others.
- Physical map nodes can be activated by any player. The node's icon and hologram update on every seat.
- Fog reveals that the host already has are applied to a joining player's map.
- The M map is redrawn with the synced fog when it is opened, when its page changes, and after a scene change.
- Other players are shown on the M map and on the native radar and compass, with their names and colours.

#### Slimepedia and research

- Slimepedia unlocks are shared. An entry unlocked on one seat is unlocked on every seat.
- A player who joins with a more advanced save takes the host's Slimepedia on join. A player with a less advanced save receives the host's entries.
- Entries unlocked later show the research popup on every seat, once. The initial join sync does not show popups.
- Received unlocks are applied without being echoed back to the host.
- Browsing the Fabricator no longer unlocks Slimepedia entries. Entries unlock when a blueprint or item is actually acquired.

#### Gardens, plots and crops

- Gardens and plots are shared: a plot's type, its upgrades, and a demolished plot are the same on every seat.
- Crops in a garden are the same on every seat. A crop that is planted, cleared or replanted on one seat is updated on the others.
- A garden's crops keep growing while its owner is away, driven by the lease owner's timer.

#### Drones

- Ranch drones have one owner at a time, chosen per station, so a drone runs on one seat and the others see it move.

#### Gordos

- Gordo feeding is host-authoritative. Each food item counts once, whether it was fed by the host or a guest.
- Normal, favourite and mixed feeds count with their own weight.
- Retries and duplicate food items do not add to a Gordo's count.
- When a Gordo reaches its target, its burst runs on every seat, and the Gordo is removed.
- The burst's rewards are paid once, by the host.
- A burst on a Gordo the host has not loaded is paid when the host loads that Gordo.
- A player who joins after a burst does not see that Gordo alive, and a Gordo that is still alive is shown alive.

#### Treasure pods

- A treasure pod opened on one seat opens on every seat.
- A pod's rewards are paid once.

#### Refinery

- Refinery deposits and spends are host-authoritative. Counts converge on every seat, and two deposits made at the same time both count.

#### Market and plort sales

- Plort prices are broadcast from the host about every half second. Only changes are sent.
- A plort sold on any seat is counted once in the market, so saturation moves once per sale.
- Each sale carries its own request id, so a retried sale is not paid twice.
- A sale before the host's first price snapshot is priced with the host's live price, not the seller's claimed price.

#### Shop, upgrades and blueprints

- Shop purchases are run by the host. The host sends the purchase result before the new stock snapshot.
- A purchase the host rejects (for example, one that is not affordable) gets a result, and no currency or stock changes.
- A repeated purchase request with the same id is applied once.
- A blueprint acquired through the shop is recorded as acquired, not only registered, on every seat.

#### Shared currency

- The shared balance is kept by the host. Sales, vase and coin income, Lucky rewards, shop and upgrade spending, and death costs go through one ledger.
- Income shows the "+money" notice once on the seat that earns it. Other seats update their balance without a second notice.
- Joining, checkpoints and retries are silent, so a join does not repeat old notices.
- Default-currency income, such as stone vases, is counted once.

#### Teleporters, gadget links and events

- Teleporter links are shared. A link made on one seat works on every seat.
- An unlink made on the host is not restored for a late joiner.
- World events are synced: map unlocks, zone unlocks, object discoveries, vacuum upgrades, Slimepedia unlocks, vacuumed and refined items, tutorial completion, and area entry and exit.

#### Resource nodes, decorations and gadgets

- Resource nodes are set by the host, so every player sees the same nodes.
- A node harvested on one seat shows as harvested on the others.
- Placed decorations and gadgets are shared. A decoration placed on one seat appears on the others, and picking it up removes it everywhere.
- Pre-placed gadgets, such as the tutorial wheelbarrow, are matched by type and position, so picking one up is seen by every seat.
- Gadget and decoration counts are kept the same on every seat, and a count echoed from another seat is not counted again.
- A gadget or decoration acquired by one player is recorded in the G inventory's Recent list on the other seats.
- Remote players' build-mode previews show the gadget they are placing, following its position and rotation. The preview hides when they leave build mode, or after 350 ms without updates.

#### Sea, lava and loading

- A player who falls into the sea is killed by the game's own sea trigger, as in single-player.
- A slime that falls into the sea is removed by its owner, and the other seats remove their copies when the owner's removal arrives.
- A sea-trigger death during a zone load is deferred until the load finishes, so travelling from the ranch to Ember Valley or home does not end in a sea death.
- Ocean damage applies again as soon as the load finishes.
- Lava damage uses the local player's own cooldown, which is rebased when the host's world clock is corrected.
- A scene load pauses the world the way the game does, and the pause is released when the load finishes.
- Animal breeding runs only for the awake owner, and it catches up after a long sleep in a bounded way.

### Actors and slimes

#### Ownership

- Each actor has one owner at a time. The owner simulates it and sends its position, state and changes.
- Ownership changes are decided by an arbiter with a hold time, so an actor does not change owner on every step.
- A new owner starts the actor's motion from the position on screen, so the actor does not jump or step back.
- An owner that has released an actor cannot claim it again with a stale claim, and a late grant cannot override newer state.
- Actors that are hibernating, or outside the 60 m interest range, are not claimed by a player.
- Ownership candidates are measured from accepted positions, not from hidden render positions.
- An actor that is being captured or is reserved for a capture is not handed to another player until the capture completes.
- An actor held in an owner's vacuum is flagged, and other players cannot take it.

#### Capture, vacuum and consumption

- Any player can vacuum an actor, including one owned by another player.
- Captures are host-confirmed: the host prepares and approves a capture, and commits it when the vacuum consumes or holds the actor.
- A capture that is held but not consumed is committed without being stored.
- A consumed actor is removed from every seat once, and its inventory addition happens once.
- A consumed actor's id is tombstoned, so a late message cannot recreate it.
- An item that a player throws or expels gets a new id, so it is not mistaken for the consumed actor.
- Capture permits are exact: a consume is allowed only for the capture it was approved for.
- Tarr captures are host-confirmed for held (GIANT) actors, so a guest can pull a Tarr without it being stored.
- A guest can vacuum a wild item when the host is far away. The host grants the item to the guest.
- Ripe fruit on a plant can be vacuumed by a player other than the plant's owner, and the plant's joint is released before the fruit is pulled.

#### Spawns and population

- Spawns are checked by the host. A spawn that would exceed the cap is refused, and the refusal is logged.
- Chicken and similar animal spawns are capped at 20 within 30 m in the wild, and at 64 within 30 m on the ranch. The wild cap applies whether or not a player is near.
- A spawn that was refused, or whose id is tombstoned, is dropped on the sender's side.
- Large spawn bursts are cut, and spawns are held back for 60 s after 80 m of movement.
- Vacuum-expelled items and ranch spawns are exempt from the back-off.
- The chicken cap is applied before a breeding spawn is created, so no excess children are instantiated and then dropped.

#### Ranch protection

- The game's dawn cleanup (DestroyOutsideHoursOfDay) does not delete ranch slimes.
- The game's re-enable cleanup (MaybeCullOnReenable) does not delete ranch slimes.
- The host refuses destroy requests for ranch slimes that come from those cleanups, and logs them as protected.
- Phosphor largos, pinks and other ranch slimes stay on the ranch and its expansions.
- Gameplay removals (eating, transforming, vacuuming, Gordos, sales and falls) work as in single-player.
- Wild phosphors still vanish at dawn.

#### Produce, crops and plants

- A crop is grown by its owner, and every seat sees the stage changes.
- Produce that another player is growing ripens on every seat, so a wild crop can be harvested from any seat.
- A plant whose fruit was vacuumed and released no longer keeps a stale held flag.
- A wild plant that a second player arrives at does not drop a burst of fruit.

#### Slime behaviours

- Dervish cyclones, Sloomber yawns, Tangle vine grapples and Tabby stalk targets are sent from the owner to every seat.
- A behaviour that is already running when a player joins is shown once on that seat.

#### Held items

- A stone pod held by one player is shown as held on every seat, and it follows the holder.

#### Cleanup

- /cleanup previews how many actors it would remove, and /cleanup now removes them, 40 per frame.

### Players and avatars

#### Remote avatars

- Each other player appears as an avatar built from the game's own humanoid model, or as a capsule when that model is not available.
- Avatars move smoothly between received positions, and they are hidden while their player is in a different scene group.
- Each avatar has a nametag in the player's colour. The nametag scales so it stays readable at a distance.
- Avatars walk, run and jump, using the owner's animator values.
- Avatars use inverse kinematics so the hands follow the vacuum and the look direction.
- The left hand is placed on a fore-grip under the vacuum's front, with the fingers curling toward the vacuum, rather than on the handle.
- The head and chest follow the player's look direction.
- Avatars show the vacuum cone, the suction effect, and the vacuum skin the owner has chosen.
- A remote player's vacuum bends grass, flowers and plants on every seat, using the game's own ripples.

#### Kick, pings and legs

- The kick key (V) pushes another player. The kicker's avatar plays the kick animation, and the host plays every accepted kick.
- The kick animation is shown on the other player's avatar.
- First-person legs follow the body animator.
- The ping key marks a point in the world. The ping is shown in the pinger's colour, and the pinger's avatar points at it.

#### Chat

- Chat is shared, and each message is shown once per seat.

#### Player state and inventory

- A guest's inventory is saved and restored on reconnect, once the upgrade snapshot has arrived.
- Items that a guest had in its vacuum when it disconnected are restored after it reconnects.

### User interface and menus

- Lycoreia has its own UI toolkit. Its menus and pages no longer depend on the third-party menu framework.
- The Multiplayer button appears on the main menu and on the pause menu.
- The Multiplayer menu has pages for hosting (automatic or fixed port), joining (join code or IP), settings, a nickname colour picker, and widescreen FOV.
- Escape and F1 open and close the game's pause menu and Slimepedia through the game's own behaviour, and the tablet is lowered when it closes.
- Opening and closing the map is handled, so the HUD returns and the cursor hides after the map closes.
- Opening the pause menu, the map or the Slimepedia does not stop the world on other seats: owned slimes and other actors keep moving.
- Widescreen FOV: a slider in Multiplayer > Settings changes the field of view live. Unticking the option returns the game's FOV, and the setting is saved across restarts.

### Discord Rich Presence

- Lycoreia has its own Discord Rich Presence client. It is built into the mod, and no separate Discord library is needed.
- Presence states: main menu, solo ranching, hosting a co-op ranch, and on a friend's ranch.
- Hosting shows the party size ("Party of n") with no maximum.
- The party id is derived from the host's id, so no IP address or join code is shown.
- Presence updates are sent at most once every 15 seconds, and the client reconnects after Discord restarts.
- Linux and Proton: the client is designed to reach the native Linux Discord client through the Proton prefix. The endpoint it connects to is written to the log.
- The "Discord Presence" setting in MelonPreferences turns it off.

### Performance and stability

- Actor updates are sent on an unreliable lane, so they do not compete with critical ownership and capture messages.
- Duplicate-message filtering does not mistake a wrapped message id for a duplicate in long sessions.
- Map node and fog refreshes are batched and throttled, so repeated fog updates do not stall the network loop.
- Native scene scans are cached, so teleporter, puzzle and radar lookups are not repeated every frame.
- Animator metadata and the ping font are cached, so the cost per avatar is lower.
- Switch and puzzle updates no longer throw repeated null-reference exceptions when their scene object is not loaded.
- The network dispatcher no longer runs full-scene searches when a fog reveal arrives.
- Slow dispatcher work and long frame gaps are logged with the name of the part that was slow, so a stutter can be traced.

### Diagnostics

- Each session writes UserData/Lycoreia/latest.log for text and diagnostics.jsonl for structured records. Each record carries the seat's role.
- Each build writes its version, flavour and assembly identity at startup, so a log can be matched to an exact build.
- Automatic save backups: the host makes a backup when it starts hosting, and a guest makes one when it joins. Old backups are pruned, keeping the number set in the settings (at least one).

### Fixed

#### Networking

- A reset connection could reuse its previous epoch, so the other side kept stale state. The epoch now always moves forward.
- A wrapped reliable message id could be mistaken for a duplicate and acknowledged without being applied. Actor updates are no longer sent as reliable messages, and critical actor messages use ordered delivery.
- Automatic hosting could keep its Host button busy after a cancelled or late step. Progress is cleared.
- Automatic hosting no longer waits for about five minutes after the world loads.

#### Actors and captures

- Only an actor's owner could vacuum it. A guest's vacuum now asks the host, and the host confirms the capture.
- A slime or plort taken by a vacuum could stay in the air on another seat. Consumed actors are removed on every seat.
- A consumed plort or carrot could float untouchable after a mass vacuum. Consumed items are removed with their model, once, on every seat.
- A simultaneous vacuum by two players could leave the host's copy in place and add the item twice. Only one copy is committed.
- A consumed actor could be recreated by a late message, leaving a ghost copy. Tombstoned ids are refused.
- A consumed actor's id could be reused for a thrown item. Expelled items get a new id.
- A capture approved for one player could be taken by the host while it was reserved. A reservation now blocks the host from taking the actor.
- An owner who had released an actor could still claim it. Released claims are ignored.
- A stale grant that arrived after a newer state could override ownership. Newer state wins.
- Actors on a hibernating area no longer claim ownership repeatedly.
- Actors far outside a player's range are no longer claimed by that player.
- A remote player's distance check used the hidden render position. It uses the latest received position.
- Frozen plorts that became vacuumable only after a resync are repaired automatically.
- Crops on a plant that another player had vacuumed were left frozen. The plant's joint is released, so the crop can be harvested.
- A plant whose fruit was vacuumed and released before its detachment kept a stale held flag. The held flag is cleared.
- Frozen produce copies never ripened on the other seats, so they could not be harvested. Produce ripens on every seat.
- A Tarr vacuumed by a guest was not confirmed by the host. Tarr captures are now confirmed.
- A consumed actor that was still being vacuumed could leave a second copy. The consume permit is now exact.
- A stone pod held by a remote player was not shown as held. Held state is shown on every seat.
- Ranch slimes were deleted by the dawn cleanup on the guest's seat. That cleanup is blocked for ranch slimes.
- A chicken burst after a forced death (Emergency Return) could fill the ranch. Animal spawns are capped.
- Chicken breeding that caught up after a long sleep created a burst of animals. Breeding now catches up once per period, and only for the awake owner.
- A roostro (rooster variant) was not counted by the chicken cap. Roostros are counted.
- A wild chicken near a player was exempt from the cap. Wild chickens are capped regardless of distance.
- A sea-trigger death during a zone load happened on the guest's seat. It is deferred until the load finishes.
- Lava damage stopped after the host's clock was corrected. The local lava cooldown is rebased.
- A zone load on a guest could leave a black screen, because the loader's pause was skipped. The loader's pause is kept.
- Travel from the ranch to Ember Valley or home could leave the home buttons unusable. The menu input maps are checked again after travel and death.

#### World

- Map fog revealed on the host did not appear on a guest's map. Reveals are applied to the physical map nodes and the fog layer.
- A guest joining after a reveal did not see it on the map. Joining players receive the host's fog.
- A guest's own unexplored map did not update after a node was activated. The map is redrawn.
- Map nodes were not redrawn after an unlock on another seat. They are redrawn.
- Physical map nodes could only be activated from the host. Guests can activate them too.
- Resource nodes were rolled differently on each seat, so one player saw a node the other did not. The host sets the nodes.
- A decoration placed on one seat was not visible on the other. Placed decorations and gadgets are shared.
- Picking up a decoration on one seat left it on the other seats. The pickup is shared.
- A pre-placed gadget picked up by one player stayed on the other seats. The pickup is shared.
- Inventory counts of decorations and gadgets were changed twice by an echo of a count change. Echoes are ignored.
- A gadget collected by a remote player was not listed in the G inventory's Recent list. It is listed now.
- The build-mode preview was not shown on other seats. It is shown.
- A Gordo fed by a guest could stay on the host's side after it burst on the guest's side. The burst is applied once and the Gordo is removed everywhere.
- A Gordo burst on a guest's side could be paid twice. The host pays once.
- A Gordo burst while the host had not loaded it did not pay out. The reward is paid when the host loads it.
- Gordo food retries could increase a Gordo's count more than once. Each food item counts once.
- The Gordo burst effects did not play on the guests. They play now.
- A treasure pod opened by a guest could be shown closed on the host. Opened pods are shown open on every seat.
- A treasure pod's rewards could be paid more than once. They are paid once.
- Refinery counts could drift when two players deposited at the same time. Counts converge.
- Teleporter links made on one seat did not appear on another. Links are shared.
- A teleporter unlinked on the host came back for a late joiner. Unlinks are kept.
- A teleporter puzzle finished on a guest's save could open a gate the host had closed. The host's puzzle state wins.
- A switch or door changed on one seat did not change on the others. Switch and door states are shared.
- A puzzle slot filled on one seat did not show on the others. Puzzle slots are shared.
- Ember Valley's primary sphere switch did not open the portal on the other seats. The sphere switch and the portal are synced.
- Shared weather did not appear on a guest after an island loaded. Weather is reapplied after loads.
- Weather that was running when a player joined was not shown. It is shown.
- A zone whose weather assets were missing was skipped silently. Missing assets are created or retried.
- Garden crops could differ between players. The crops are kept the same on every seat.
- Browsing the Fabricator shop unlocked the whole shop's entries in the Slimepedia. Browsing no longer unlocks entries.
- A blueprint acquired through the shop was only registered, not acquired, on the other seats. It is recorded as acquired.
- A received Slimepedia unlock was echoed back to the host. Received unlocks are applied without an echo.
- A plort sale was credited to the balance twice. Each sale is credited once.
- A plort sold by a guest was paid by the game and again by the mod. Each sale is paid once.
- A plort sold before the host's first price snapshot used the seller's claimed price. The host's live price is used.
- A shop purchase that was retried or sent with a reused request id was applied again. Each request id is applied once.
- The shop's stock was not updated on every seat after a purchase. The purchase result arrives before the stock snapshot.
- The shared balance could diverge between seats for a while. The host's ledger is the only source, and the seats catch up to it.
- Stone vase and other default-currency income did not reach other players. It is shared.
- Join and checkpoint messages repeated old "+money" notices. They are silent now.

#### Avatars and UI

- Remote avatars did not animate: walking, running and jumping were not shown. The animator values are taken from the owner.
- Kicks were not visible on a remote player's avatar. The kick animation is shown.
- The left hand rested on the handle with a full fist. It is placed on a fore-grip under the vacuum's front.
- The right arm clipped through clothing when the player looked down. The arm is adjusted when the player looks down.
- Lycoreia's Multiplayer menu was drawn behind the game's pause menu. It is drawn on top.
- Escape did not close the pause menu during a session, only the mouse Resume button did. Escape closes it.
- The menu input stayed locked after the map was opened. Map close restores the input.
- Pausing for a menu froze other players' owned slimes. Simulation continues while a menu is open.
- A widescreen FOV setting was not saved, and its slider did nothing. The FOV changes live and is saved.
- A stutter after joining came from repeated null-reference errors in switch and puzzle updates. Those updates no longer throw.
- The F1 tablet could stay in front of the face after F1 was pressed twice. The tablet is lowered.

#### Startup

- Starting the game with Lycoreia could crash on the main menu. The crash-causing menu hook was removed.
- Hosting with a router that did not answer reported "router does not support UPnP" for every kind of failure. Timeouts, no-answer replies and errors are reported separately.

### Removed

- The Starlight dependency. Mods/Starlight.dll is not used.
- The bundled SharpOpenNat and DiscordRPC libraries (see Installation changes).
- The old 1.x license files in UserData/Lycoreia (*.lic.txt).
- The forced pause that stopped the world when a menu opened. The game's own pause is used, and the world keeps running for everyone.

### Known issues

These are open problems in this release. When you report one, please include the seat's `UserData/Lycoreia/latest.log` and `diagnostics.jsonl`, and the time of the event.

- Several fixes in this release were built and unit-tested, but they have not all been confirmed in full multiplayer sessions. If something you expected to be fixed still happens, please report it.
- Some guests report a frame gap of about 0.1 to 0.4 seconds right after joining. The cause is not yet confirmed.
- The Discord presence on/off switch is only in MelonPreferences for now, not in the in-game menu.
- The remote build-mode preview shows the gadget and its position, but not the game's green or red placement colour.
- A pre-placed gadget removed on the host before a guest joined is not reconciled when that guest joins.
- Ranch slimes in an expansion that is not loaded on a player's seat depend on the game's own rules for that expansion.
- Escape, F1, nested menus (options, Slimepedia, dialogs), gamepad transitions and menu-close timing need a full check on every seat.
- The world keeps running while menus are open, but this has not been checked on every menu page in a long session.
- Loading into Ember Valley and other zones can still show a longer black screen on a slow seat. Please report the time and the log.
- Tarr vacuuming is host-confirmed, but Tarr-specific water effects and kills by a guest are not yet synced.
- Cotton Hunter largo auras can still desync between players.
- Water "piece" objects that sometimes appear in the world are not yet fixed.
- Floating carrots after a mass vacuum with a full inventory, and duplicate items from simultaneous vacuums, have fixes in this build that still need confirmation in a full session.
- Chickens and other animals could still burst after an unusual sequence of sleeps and time jumps. The cap is in place; report any burst with the time.
- Choppy slime motion during ownership handovers has fixes in this build, but a long session still needs checking for backtracking or duplicates.
- Cross-island captures without the host present, and harvesting ripe attached fruit from a distance, need a full check.
- The "Rejected ActorDestroy from a non-owner" log spam has not been confirmed as fixed.
- Radiant slimes seen as frozen while the host is paused have a fix in this build that is not yet confirmed.
- Other players' jet (vacuum jetpack) effects are not yet shown.
- First-person kick legs may not show correctly when looking down. Please report it with a screenshot.
- Some world effects are not yet synced: conversations, geysers, lightning and tornadoes, cannon fire timers, teleporter effects, plort collectors, auto-feeders, silo slot selection, and drone ammo and explorer resource stores.
- Remote players' footstep and movement sounds are not played.
- Shockwave and splash effects are not shown on remote seats.
- The port mapping release on quit is implemented, but it still needs a check on a real router.
- Manual internet hosting still needs the port forwarded on the host's router when UPnP and NAT-PMP are not available.

## [1.0.1] and earlier - 2026-10

First public releases of Lycoreia. Superseded by 2.0.0; 1.x is no longer distributed or supported.
