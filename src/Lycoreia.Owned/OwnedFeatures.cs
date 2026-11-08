// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Game;
using Lycoreia.Game.Avatars;
using Lycoreia.Owned.Shared.Managers;
using Lycoreia.Owned.Components.FX;
using Lycoreia.Net.Messages;

namespace Lycoreia.Compat
{
    [Lycoreia.Game.FeatureRegistration]
    internal static class OwnedFeatures
    {
        internal static void Register()
        {
        try { Lycoreia.Owned.Features.WidescreenFov.WidescreenFov.Initialize(); }
        catch (System.Exception ex) { OwnedLog.LogWarning($"[WidescreenFov] Initialize failed: {ex.Message}"); }
        LycoreiaMod.Pumps.Add(new Pump("owned-widescreen-fov", Lycoreia.Owned.Features.WidescreenFov.WidescreenFov.Tick));
        // Our MonoBehaviours must be known to the Il2Cpp runtime before any Get/AddComponent (no framework does this for us).
        if (!Il2CppInterop.Runtime.Injection.ClassInjector.IsTypeRegisteredInIl2Cpp<Lycoreia.Owned.Components.FX.RemoteVacuumSuctionFX>()) Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<Lycoreia.Owned.Components.FX.RemoteVacuumSuctionFX>();
            LycoreiaMod.Pumps.Add(new Pump("owned-friend-fun", () => { if (!Lycoreia.Game.GameState.Ready) return; PlayerPings.Tick(); FriendInteractions.Tick(); }));
#if LYCOREIA_TEST
            LycoreiaMod.Pumps.Add(new Pump("test-avatar-rig", Lycoreia.Owned.Shared.Utils.TestAvatarRig.Pump));
#endif
            LycoreiaMod.LatePumps.Add(new Pump("owned-first-person-legs", () => { if (Lycoreia.Game.GameState.Ready) FirstPersonLegs.LateTick(); }));
            LycoreiaMod.Pumps.Add(new Pump("owned-refinery-reconcile", () => { if (Lycoreia.Game.GameState.Ready) NetworkRefineryManager.Reconcile(); }));
            LycoreiaMod.Pumps.Add(new Pump("owned-world-managers", () => { if (Lycoreia.Game.GameState.Ready && ModFacade.IsNetworkActive) { WorldManagersGlue.Pump(); UpgradesGlue.Pump(); } }));
            LycoreiaMod.Pumps.Add(new Pump("owned-vac-palette", () => { if (Lycoreia.Game.GameState.Ready) NetworkVacPaletteManager.Pump(); }));
            LycoreiaMod.Pumps.Add(new Pump("owned-slime-behavior", () => { if (Lycoreia.Game.GameState.Ready) NetworkSlimeBehaviorManager.Pump(); }));
            LycoreiaMod.Pumps.Add(new Pump("owned-vac-fx", () =>
            {
                if (!Lycoreia.Game.GameState.Ready) return;
                if (!_fxTemplate && UnityEngine.Time.unscaledTime >= _nextTemplateTry) { _nextTemplateTry = UnityEngine.Time.unscaledTime + 1f; _fxTemplate = RemoteVacuumSuctionFX.InitializeTemplate(); }
                RemoteVacuumSuctionFX.PumpPendingPlayerStates();
            }));
            RemoteAvatars.FxShown += fx =>
            {
                if (fx.Kind == PlayerFxKind.RunStart || fx.Kind == PlayerFxKind.RunEnd) RemoteVacuumSuctionFX.SetSuction(fx.PlayerId, fx.Kind == PlayerFxKind.RunStart, out _);
            };
            LycoreiaMod.GameLayersReady += () =>
            {
                NetworkTreasurePodManager.Register(); NetworkRefineryManager.Register(); WorldManagersGlue.Register(); UpgradesGlue.Register(); NetworkShopManager.Register(); NetworkSlimeBehaviorManager.Register();
                Mod.Node.On<VacSuctionPose>(Lycoreia.Net.Side.Both, (c, m) =>
                {
                    if (c.OnHost) m.PlayerId = c.SenderId;                // the sender comes from the connection; stamped before the relay
                    if (m.PlayerId == Mod.LocalId) return;
                    RemoteVacuumSuctionFX.ApplySuctionPose(m.PlayerId, m.Sequence, m.Position.ToUnity(), new UnityEngine.Quaternion(m.Rotation.X, m.Rotation.Y, m.Rotation.Z, m.Rotation.W), m.Holding);
                });
                Mod.Session.PlayerRemoved += p => { if (p != null) RemoteVacuumSuctionFX.ForgetPendingPlayer(p.Id); };
                Mod.Snapshots.Register(Lycoreia.Snapshot.SnapStep.VacPalettes, NetworkVacPaletteManager.SendAllTo);
                if (Lycoreia.Game.World.WorldRuntime.Upgrades != null) Lycoreia.Game.World.WorldRuntime.Upgrades.SnapshotApplied += PlayerInventoryManager.NotifyPlayerUpgradeSnapshotApplied;
                Mod.Node.On<Lycoreia.Net.Messages.PlayerVacPalette>(Lycoreia.Net.Side.Both, (c, m) => { NetworkVacPaletteManager.Receive(m, c.OnHost ? c.SenderId : null); });
            };
            RemoteAvatars.PingShown += p => { if (PlayerPings.AcceptRemote(p, false)) PlayerPings.Show(p.PlayerId, p.Position.ToUnity(), p.Normal.ToUnity(), p.SceneGroup); };
            RemoteAvatars.KickApplied += k => { if (FriendInteractions.IsPlausibleKick(k)) FriendInteractions.ReceiveKick(k); };
        }

        static bool _fxTemplate; static float _nextTemplateTry;

        sealed class Pump : IFramePump
        {
            readonly string _name; readonly System.Action _a; public Pump(string n, System.Action a) { _name = n; _a = a; }
            public string Name => _name; void IFramePump.Pump() => _a();
        }
    }
}
