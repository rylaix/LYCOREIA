# Lycoreia SDK (API v1)

For mods that want to play fair in Lycoreia co-op: cheat panels, spawners, map and HUD mods.
The SDK files in this folder are MIT-licensed (see `LICENSE`); Lycoreia itself is not.

## What you get

- **Session state**: Offline / Host / Guest, whether cheats are allowed, a per-host key for your save data.
- **Players**: everyone in the session with name, colour, host flag and transform (for minimaps and markers).
- **Screen etiquette**: whether the player is typing in Lycoreia's chat, or a Lycoreia overlay is open.
- **Spawn sharing**: one call to share an actor your mod created with every player.
- **Cheat gate**: one call that applies the session's "Allow cheats" rule and logs the action in Lycoreia's diagnostics.
- **Events**: role changed, cheat rule changed, player joined, player left.

## Install (no dependency on Lycoreia.dll)

1. Copy `LycoreiaClient.cs` into your mod's source.
2. Use `LycoreiaSdk.LycoreiaClient`. When Lycoreia is not installed everything still works with offline rules:
   `Role` is `Offline`, `CheatsAllowed` is `true`, `PublishSpawn` returns false, and events never fire.

The client looks up one public type, `Lycoreia.Api.LycoreiaApi` in the assembly `Lycoreia`, and checks its `Version`. It retries every 3 seconds until Lycoreia has loaded. Your mod never touches Lycoreia's internal classes, so Lycoreia updates do not break it.

If you would rather reference `Lycoreia.dll` directly, use `Lycoreia.Api.LycoreiaApi`; the member names are the same. Your mod then needs Lycoreia installed to load.

## Quick start

```csharp
using LycoreiaSdk;

public override void OnInitializeMelon()
{
    LycoreiaClient.Register("My Cheat Panel", "1.0.0");          // optional: shows up in Lycoreia's diagnostics
    LycoreiaClient.CheatsAllowedChanged += allowed => { if (!allowed) TurnOffGodModeFlightEtc(); };
}

public override void OnUpdate()
{
    if (LycoreiaClient.TypingOpen) return;                         // player is typing in Lycoreia's chat: no hotkeys
    if (menuOpen && LycoreiaClient.OverlayOpen) CloseMenu();       // Lycoreia's menu, chat, wheel or popup owns the screen
}

void AddMoney(int amount)
{
    using var cheat = LycoreiaClient.BeginCheat("currency");
    if (!cheat.Allowed) { ShowMessage(cheat.Reason); return; }     // Reason is already in the player's language (en/ru/es)
    playerState.AddCurrency(newbucks, amount, true, null);         // Lycoreia syncs money itself
}

void Spawn(IdentifiableType type, Vector3 pos)
{
    using var cheat = LycoreiaClient.BeginCheat("spawn");
    if (!cheat.Allowed) { ShowMessage(cheat.Reason); return; }
    var model = gameModel.InstantiateActorModel(type, sceneGroup, pos, Quaternion.identity, false);
    var go = model.GetGameObject() ?? InstantiationHelpers.InstantiateActorFromModel(model);
    LycoreiaClient.PublishSpawn(go);                               // this spawn path is not seen by Lycoreia: share it
}
```

A complete, buildable example is in `examples/CheatPanelExample`. Press F6 in game to open it. It shows the session, adds Newbucks and spawns a Pink Slime.

## Reference

| Member | Meaning |
|---|---|
| `Available`, `ApiVersion`, `ModVersion` | Lycoreia found; API version (1); Lycoreia build ("2.1.0"). |
| `Role` | `Offline`, `Host` or `Guest`. A guest counts only once the host has approved it. |
| `InSession` | `Role != Offline`. |
| `CheatsAllowed` | Offline: true. Host: its "Allow cheats" setting. Guest: what the host approved it with. |
| `CheatsDeniedReason` | Text for the player when cheats are off (en/ru/es); null when allowed. |
| `SessionKey` | "" offline, otherwise `lyc_<host id>`. The same for every session with that host; safe for file names. |
| `LocalPlayerId` | The local player's id ("" offline). Ids are keys: never show them. |
| `StreamerMode` | The local player hides other players' names. `Players` already returns "Player" for them. |
| `TypingOpen` | Lycoreia's chat or a menu text field has keyboard focus. |
| `OverlayOpen` | `TypingOpen`, or Lycoreia's multiplayer menu, co-op wheel or what's-new popup is open. |
| `Players` | Everyone, the local player first: `Id`, `Name`, `Color`, `IsHost`, `IsLocal`, `Transform` (null while not spawned nearby). |
| `Register(name, version)` | Optional; names your mod in Lycoreia's diagnostics. |
| `PublishSpawn(go[, out reason])` | Shares an actor created with `InstantiateActorModel` + `InstantiateActorFromModel`. Calling it twice is safe. While cheats are allowed, the actor skips Lycoreia's crowding and chicken caps. |
| `BeginCheat(kind)` | Returns a scope with `Allowed` / `Reason`. Dispose it after the action. |
| `RoleChanged`, `CheatsAllowedChanged`, `PlayerJoined`, `PlayerLeft` | Raised on the main thread, once per change. |

All members are main-thread only and never throw.

## What Lycoreia already syncs (no API call needed)

Lycoreia hooks these game paths, so a change made through them reaches every player:

- **Money**: `PlayerState.AddCurrency` / `SpendCurrency` (any change of the currency model). Money is shared and the host's value wins.
- **Actors created through `InstantiationHelpers.InstantiateActor`**: shared automatically. Other paths need `PublishSpawn`.
- **Slimepedia unlocks**: `PediaDirector.Unlock`.
- **Upgrades**: `UpgradeModel.IncrementUpgradeLevel`. Upgrades are shared by the whole session.
- **Gadgets placed**: `GadgetDirector.InstantiateGadget`.
- **Doors**: `AccessDoor.CurrState`.
- **Time skips**: `TimeDirector.FastForwardTo`, from the host only. World time belongs to the host.

**Not synchronised, keep them single-player:** freezing time, freezing slimes, changing `Time.timeScale` or simulation speed. Each player would end up in a different world.

## Rules for co-op-friendly mods

1. Gate every cheat with `BeginCheat`. When `CheatsAllowedChanged(false)` fires, switch off running effects (god mode, flight, speed, infinite energy).
2. Weather and clock belong to the host: offer them only when `Role != Guest`.
3. Never delete actors you did not create while in a session. Other players own some of them.
4. Respect `TypingOpen` and `OverlayOpen`.
5. Do not bind Lycoreia's internal types by reflection; they change between builds. If you need something the API lacks, ask for it.

## Moving from internal reflection (Creator Toolkit 0.9.x)

| Before (internal, breaks on refactors) | API v1 |
|---|---|
| `Lycoreia.Compat.ModFacade.Server.IsRunning` / `Client.IsConnected` | `Role` |
| `Lycoreia.Mod.Session.AllowCheats` / `HostAllowsCheats` | `CheatsAllowed`, `CheatsAllowedChanged` |
| `Session.HostId` (sanitised by the mod) | `SessionKey` |
| `ChatHud.IsOpen`, `InputGate.Blocked` | `TypingOpen` |
| `LycoreiaMenu.IsOpen`, `CoopWheel.Busy`, `WhatsNewPopup.IsOpen` | `OverlayOpen` |
| `ModFacade.StreamerMode` | `StreamerMode` |
| `RemoteAvatars.All` + `RemoteAvatar` fields | `Players` |
| `InstantiateActorHook.Postfix(go)` + writing `VacuumExpelHook.Depth` | `PublishSpawn(go)` |

## Versioning

`Version` is raised only for breaking changes; new members keep the number. The client refuses an API older than the one it was written for and stays in offline mode.

Planned for v2: host-run requests (a guest asks the host to set time or weather, or to spawn), and host-side checking of cheat actions.
