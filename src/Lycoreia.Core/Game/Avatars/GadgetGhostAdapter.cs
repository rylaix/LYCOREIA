// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using Lycoreia.Game;
using Lycoreia.Game.Hooks;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using UnityEngine;

namespace Lycoreia.Game.Avatars
{
    /// <summary>Publishes the local GadgetItem preview and creates matching previews under remote avatars.</summary>
    static class GadgetGhostAdapter
    {
        const double SendEvery = 0.05, LostAfter = 0.35, ItemStaleAfter = 0.5;
        static GadgetItem _localItem;
        static double _lastObserved, _nextSend;
        static bool _lastSentEnabled;
        static int _lastSentGadget;
        static bool _hookLogged, _pollLogged;
        static GadgetItem _factory;
        static double _nextFactorySearch;
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);

        public static void Observe(GadgetItem item)
        {
            try
            {
                var player = SceneContext.Instance != null ? SceneContext.Instance.Player : null;
                var local = player != null ? player.GetComponent<PlayerItemController>()?._gadgetItem : null;
                if (item == null || local == null || item.Pointer != local.Pointer) return;
                _localItem = item; _factory = item; _lastObserved = Time.unscaledTimeAsDouble;
                if (!_hookLogged) { _hookLogged = true; Mod.Diagnostics?.Record("players", "build-ghost-hook", "local GadgetItem.Update observed"); }
            }
            catch (Exception e) { Rl.Warn("observe", "build preview observation failed: " + e.Message); }
        }

        public static void Pump()
        {
            if (!Mod.IsNetworkActive || RemoteAvatars.Sync == null) { _lastSentEnabled = false; return; }
            double now = Time.unscaledTimeAsDouble; if (now < _nextSend) return; _nextSend = now + SendEvery;
            try
            {
                if (_localItem == null || now - _lastObserved > ItemStaleAfter)
                {
                    var polled = Factory();
                    if (polled != null)
                    {
                        _localItem = polled; _lastObserved = now;
                        if (!_pollLogged) { _pollLogged = true; Mod.Diagnostics?.Record("players", "build-ghost-polled", "local GadgetItem found by frame polling fallback"); }
                    }
                }
                GadgetItem item = now - _lastObserved <= ItemStaleAfter ? _localItem : null;
                var placeholder = item != null ? item._gadgetPlaceholderInstance : null;
                var definition = item != null ? item._heldGadget : null;
                int typeId = definition != null ? ActorTypes.IdOf(definition) : 0;
                bool enabled = item != null && placeholder != null && item.IsPlaceholderVisible && typeId > 0;
                if (!enabled)
                {
                    if (_lastSentEnabled)
                    {
                        RemoteAvatars.Sync.PublishGadgetGhost(new GadgetGhost { Enabled = false, PlayerId = Mod.LocalId });
                        Mod.Diagnostics?.Record("players", "build-ghost-sent", $"enabled=false; previous-type={_lastSentGadget}");
                    }
                    _lastSentEnabled = false; _lastSentGadget = 0; return;
                }

                var t = placeholder.transform; var pl = item._player.transform;
                var worldRotation = t.rotation; var localRotation = Quaternion.Inverse(pl.rotation) * worldRotation;
                var m = new GadgetGhost
                {
                    Enabled = true, Valid = item._isPlacementValid, PlayerId = Mod.LocalId, Gadget = typeId,
                    Position = new Vec3(t.position.x, t.position.y, t.position.z),
                    Rotation = new Quat(worldRotation.x, worldRotation.y, worldRotation.z, worldRotation.w),
                    LocalRotation = new Quat(localRotation.x, localRotation.y, localRotation.z, localRotation.w)
                };
                RemoteAvatars.Sync.PublishGadgetGhost(m);
                if (!_lastSentEnabled || _lastSentGadget != typeId)
                    Mod.Diagnostics?.Record("players", "build-ghost-sent", $"enabled=true; type={typeId}; valid={m.Valid}; position={m.Position}");
                _lastSentEnabled = true; _lastSentGadget = typeId;
            }
            catch (Exception e) { Rl.Warn("publish", "build preview publish failed: " + e.Message); }
        }

        static GadgetItem Factory()
        {
            var current = SceneContext.Instance?.Player?.GetComponent<PlayerItemController>()?._gadgetItem;
            if (current != null) { _factory = current; return current; }
            if (_factory != null && _factory.gameObject != null && _factory.gameObject.scene.IsValid()) return _factory;
            double now = Time.unscaledTimeAsDouble; if (now < _nextFactorySearch) return null; _nextFactorySearch = now + 1.0;
            try
            {
                var player = SceneContext.Instance != null ? SceneContext.Instance.Player : null;
                if (player == null) return null;
                foreach (var candidate in Resources.FindObjectsOfTypeAll<GadgetItem>())
                    if (candidate != null && candidate._player != null && candidate._player.gameObject == player && candidate.gameObject.scene.IsValid() && candidate.gameObject.scene.isLoaded)
                    { _factory = candidate; return candidate; }
            }
            catch (Exception e) { Rl.Warn("factory", "build preview factory lookup failed: " + e.Message); }
            return null;
        }

        public static bool Create(RemoteAvatar avatar, int typeId, out GameObject preview)
        {
            preview = null;
            try
            {
                if (!ActorTypes.TryGetType(typeId, out var type) || type == null) return false;
                var definition = type.TryCast<GadgetDefinition>(); if (definition == null || definition.prefab == null) return false;
                var factory = Factory(); if (factory == null) return false;
                preview = factory.CreateGadgetPlaceholder(definition.prefab, avatar.transform);
                if (preview == null) return false;
                // RemoteAvatar's body is deliberately scaled to match its humanoid template. Preserve the prefab's world scale.
                float s = avatar.transform.lossyScale.x; if (Mathf.Abs(s) > 1e-4f) preview.transform.localScale /= s;
                preview.SetActive(false);
                return true;
            }
            catch (Exception e) { Rl.Warn("create:" + typeId, $"remote build preview creation failed for type {typeId}: {e.Message}"); return false; }
        }

        public static bool Fresh(double at) => Time.unscaledTimeAsDouble - at <= LostAfter;
    }

    [HarmonyPatch(typeof(GadgetItem), nameof(GadgetItem.Update))]
    static class GadgetGhostObserveHook
    {
        static void Postfix(GadgetItem __instance) { try { GadgetGhostAdapter.Observe(__instance); } catch (Exception e) { HookGuard.Fail("GadgetItem.Update/build-ghost", e); } }
    }

    public sealed partial class RemoteAvatar
    {
        GameObject _gadgetGhost;
        int _gadgetGhostType;
        double _gadgetGhostAt;
        bool _gadgetGhostShown;
        Vector3 _gadgetGhostPosition;
        Quaternion _gadgetGhostRotation = Quaternion.identity;

        public void ShowGadgetGhost(GadgetGhost m)
        {
            _gadgetGhostAt = Time.unscaledTimeAsDouble;
            if (m == null || !m.Enabled)
            {
                bool wasVisible = _gadgetGhostShown;
                if (_gadgetGhost != null && _gadgetGhost.activeSelf) _gadgetGhost.SetActive(false);
                _gadgetGhostShown = false; _gadgetGhostType = 0;
                if (wasVisible) Mod.Diagnostics?.Record("players", "build-ghost-applied", $"player={PlayerId}; enabled=false");
                return;
            }
            if (_gadgetGhost == null || _gadgetGhostType != m.Gadget)
            {
                if (_gadgetGhost != null) UnityEngine.Object.Destroy(_gadgetGhost);
                _gadgetGhost = null; _gadgetGhostType = 0;
                if (!GadgetGhostAdapter.Create(this, m.Gadget, out _gadgetGhost))
                { Mod.Diagnostics?.Record("players", "build-ghost-create-failed", $"player={PlayerId}; type={m.Gadget}"); return; }
                _gadgetGhostType = m.Gadget;
                Mod.Diagnostics?.Record("players", "build-ghost-applied", $"player={PlayerId}; type={m.Gadget}; valid={m.Valid}");
            }
            var p = m.Position; var q = m.Rotation;
            _gadgetGhostPosition = new Vector3(p.X, p.Y, p.Z); _gadgetGhostRotation = new Quaternion(q.X, q.Y, q.Z, q.W);
            _gadgetGhost.transform.SetPositionAndRotation(_gadgetGhostPosition, _gadgetGhostRotation);
            if (_shown && !_gadgetGhostShown) { _gadgetGhost.SetActive(true); _gadgetGhostShown = true; }
        }

        void TickGadgetGhost(double now, bool avatarVisible)
        {
            bool show = avatarVisible && _gadgetGhost != null && GadgetGhostAdapter.Fresh(_gadgetGhostAt);
            if (show) _gadgetGhost.transform.SetPositionAndRotation(_gadgetGhostPosition, _gadgetGhostRotation);
            if (show != _gadgetGhostShown)
            {
                _gadgetGhost.SetActive(show); _gadgetGhostShown = show;
            }
        }
    }
}
