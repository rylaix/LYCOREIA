// Example: a tiny cheat panel that plays fair in Lycoreia co-op. SPDX-License-Identifier: MIT (see sdk/LICENSE)
// F6 opens it. It shows the session, lets you add Newbucks and spawn a Pink Slime, and follows the host's "Allow cheats" rule.
// Without Lycoreia installed everything still works (offline rules).
using System;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.Economy;
using LycoreiaSdk;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(CheatPanelExample.CheatPanelMod), "Cheat Panel Example", "1.0.0", "Lycoreia SDK example")]
[assembly: MelonGame("MonomiPark", "SlimeRancher2")]

namespace CheatPanelExample
{
    public sealed class CheatPanelMod : MelonMod
    {
        bool _open;
        string _status = "";

        public override void OnInitializeMelon()
        {
            LycoreiaClient.Register("Cheat Panel Example", "1.0.0");
            LycoreiaClient.RoleChanged += role => _status = "Session: " + role;
            LycoreiaClient.CheatsAllowedChanged += allowed =>
            {
                _status = allowed ? "Cheats allowed" : "Cheats turned off by the session";
                // Effects that keep running (god mode, flight...) must be switched off here when allowed == false.
            };
            LycoreiaClient.PlayerJoined += p => _status = p.Name + " joined";
        }

        public override void OnUpdate()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (LycoreiaClient.TypingOpen) return;                      // the player is typing in Lycoreia's chat: no hotkeys
            if (kb.f6Key.wasPressedThisFrame) _open = !_open;
            if (_open && LycoreiaClient.OverlayOpen) _open = false;    // Lycoreia's menu / chat / wheel owns the screen
        }

        public override void OnGUI()
        {
            if (!_open) return;
            var r = new Rect(20, 120, 330, 250);
            GUI.Box(r, "Cheat Panel Example");
            float y = r.y + 28;
            GUI.Label(new Rect(r.x + 10, y, 310, 22), $"Lycoreia: {(LycoreiaClient.Available ? LycoreiaClient.ModVersion : "not installed")}   role: {LycoreiaClient.Role}"); y += 22;
            GUI.Label(new Rect(r.x + 10, y, 310, 22), "Cheats: " + (LycoreiaClient.CheatsAllowed ? "allowed" : "off")); y += 22;
            foreach (var p in LycoreiaClient.Players)
            {
                GUI.Label(new Rect(r.x + 10, y, 310, 22), $"  {(p.IsLocal ? "you" : p.Name)}{(p.IsHost ? " (host)" : "")}"); y += 20;
                if (y > r.y + 150) break;
            }
            if (GUI.Button(new Rect(r.x + 10, r.y + 175, 150, 30), "+1000 Newbucks")) AddNewbucks(1000);
            if (GUI.Button(new Rect(r.x + 170, r.y + 175, 150, 30), "Spawn Pink Slime")) SpawnPink();
            GUI.Label(new Rect(r.x + 10, r.y + 212, 310, 34), _status);
        }

        void AddNewbucks(int amount)
        {
            using var cheat = LycoreiaClient.BeginCheat("currency");
            if (!cheat.Allowed) { _status = cheat.Reason; return; }
            try
            {
                var player = SceneContext.Instance?.PlayerState;
                var currency = LookupDirector.GetIfReady()?.CurrencyList?.GetDefaultCurrency();
                if (player == null || currency == null) { _status = "Load a save first"; return; }
                // Lycoreia syncs money through the game's own currency change: no extra call needed.
                player.AddCurrency(currency.Cast<ICurrency>(), amount, true, null);
                _status = $"+{amount} Newbucks";
            }
            catch (Exception e) { _status = "Failed: " + e.Message; }
        }

        void SpawnPink()
        {
            using var cheat = LycoreiaClient.BeginCheat("spawn");
            if (!cheat.Allowed) { _status = cheat.Reason; return; }
            try
            {
                var ctx = SceneContext.Instance;
                var player = ctx?.Player;
                if (ctx == null || player == null || ctx.GameModel == null) { _status = "Load a save first"; return; }
                IdentifiableType pink = null;
                foreach (var def in Resources.FindObjectsOfTypeAll<SlimeDefinition>())
                    if (def != null && def.name == "Pink") { pink = def; break; }
                if (pink == null) { _status = "Pink Slime not found"; return; }
                var tp = player.GetComponentInChildren<TeleportablePlayer>(true);
                var pos = player.transform.position + player.transform.forward * 3f + Vector3.up;
                var model = ctx.GameModel.InstantiateActorModel(pink, tp.SceneGroup, pos, Quaternion.identity, false);
                var go = model?.GetGameObject() ?? InstantiationHelpers.InstantiateActorFromModel(model);
                if (go == null) { _status = "Spawn failed"; return; }
                // This spawn path bypasses Lycoreia's own spawn hook: share it explicitly (no-op offline).
                if (LycoreiaClient.InSession && !LycoreiaClient.PublishSpawn(go, out var reason))
                    _status = "Spawned here only: " + reason;
                else
                    _status = "Spawned a Pink Slime";
            }
            catch (Exception e) { _status = "Failed: " + e.Message; }
        }
    }
}
