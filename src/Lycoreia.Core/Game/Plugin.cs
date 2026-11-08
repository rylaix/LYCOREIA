// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Lycoreia.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

[assembly: MelonLoader.MelonInfo(typeof(Lycoreia.Game.LycoreiaMod), "Lycoreia", Lycoreia.ModVersion.Value, "Rylaix")]
[assembly: MelonLoader.MelonGame("MonomiPark", "SlimeRancher2")]
[assembly: AssemblyDescription(Lycoreia.BuildInfo.Description)]
[assembly: AssemblyMetadata("display_version", Lycoreia.BuildInfo.Version)]
[assembly: AssemblyMetadata("source_code", Lycoreia.BuildInfo.SourceCode)]
[assembly: AssemblyMetadata("nexus", Lycoreia.BuildInfo.Nexus)]
[assembly: AssemblyMetadata("discord", Lycoreia.BuildInfo.Discord)]

namespace Lycoreia.Game
{
    /// <summary>Marks a static class whose static Register() the expansion calls once during late init (feature modules register pumps and events).</summary>
    [AttributeUsage(AttributeTargets.Class)] public sealed class FeatureRegistrationAttribute : Attribute { }

    /// <summary>One feature manager pumped from the main-thread dispatcher each frame. A failing item is logged and never stops the rest.</summary>
    public interface IFramePump { string Name { get; } void Pump(); }

    sealed class ArbiterPump : IFramePump
    {
        public string Name => "arbiter";
        public void Pump() { if (Mod.IsHost && Mod.Actors != null) Mod.Actors.Arbiter.Tick(Time.unscaledTime); }
    }

    /// <summary>The MelonLoader mod: identity (assembly attributes), init order, scene hooks, pump order and quit order.</summary>
    public sealed class LycoreiaMod : MelonLoader.MelonMod
    {
        public static readonly List<IFramePump> Pumps = new List<IFramePump>();
        /// <summary>Pumps that must run after the animators (LateUpdate), e.g. procedural rigs.</summary>
        public static readonly List<IFramePump> LatePumps = new List<IFramePump>();
        static Thread _tick; static volatile bool _stop;
        static bool _gameContextReady;
        static bool _firstUpdate;
        static bool _initStarted;   

        public override void OnInitializeMelon()
        {
            if (_initStarted) return;      // one-shot even if a step below throws (a second run would start a second tick thread and re-register features)
            _initStarted = true;
            Prefs.Create();
            Log.Console = (lv, m) => { switch (lv) { case LogLevel.Warning: MelonLoader.MelonLogger.Warning(m); break; case LogLevel.Error: MelonLoader.MelonLogger.Error(m); break; default: MelonLoader.MelonLogger.Msg(m); break; } };
            Mod.Init(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, diagnostics: Prefs.Get<bool>(Settings.DevDiagnostics));
            Log.PacketSizes = Prefs.Get<bool>(Settings.PacketSizeLogging);
            Mod.Session.LocalName = Prefs.Username; Mod.Session.AllowCheats = Prefs.AllowCheats;
            Mod.Session.ServerStarted += () => Mod.Session.AllowCheats = Prefs.AllowCheats;     // cheats follow the local setting when the server starts
            _stop = false;
            _tick = new Thread(() => { while (!_stop) { try { Mod.Node.Tick(); } catch (Exception e) { Log.Error("network tick", e); } Thread.Sleep(5); } }) { IsBackground = true, Name = "Lycoreia-tick" };
            _tick.Start();
            Type[] all; try { all = typeof(LycoreiaMod).Assembly.GetTypes(); } catch (ReflectionTypeLoadException e) { all = Array.FindAll(e.Types, x => x != null); }
            foreach (var t in all)
            {
                if (t.GetCustomAttribute<FeatureRegistrationAttribute>() == null) continue;
                try { t.GetMethod("Register", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, null); }
                catch (Exception e) { Log.Error("feature registration " + t.Name, e); }
            }
            Pumps.Add(new DiscordPresencePump());
            Log.Info($"Lycoreia {BuildInfo.Version} ({BuildInfo.Flavour}) ready; {Mod.Registry.Count} message types");
        }


        public override void OnLateInitializeMelon()
        {
            Log.Info("Unity late initialization complete; main-thread dispatcher ready");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (!Mod.Initialised) return;
            SceneObjectCache.Invalidate();
            Log.Info("scene loaded: " + sceneName);
            Mod.Diagnostics?.Record("scene", "scene-loaded", $"buildIndex={buildIndex}; scene={sceneName}");
            if (sceneName == "MainMenuEnvironment") Avatars.AvatarTemplate.BeginCapture();
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (!Mod.Initialised) return;
            SceneObjectCache.Invalidate();
            Log.Info("scene initialized: " + sceneName);
            Mod.Diagnostics?.Record("scene", "scene-initialized", $"buildIndex={buildIndex}; scene={sceneName}");
            if (sceneName == "MainMenuEnvironment")
            {
                Avatars.AvatarTemplate.BeginCapture();      // the menu model may only activate a few frames later: retried from OnUpdate
                Lycoreia.Owned.Components.UI.MultiplayerUI.Initialize();
            }
        }

#if LYC_TEST
        static bool _introEnded;
        static float _nextIntroCheck;
        static void TrySkipTestIntro()
        {
            if (_introEnded || Environment.GetEnvironmentVariable("LYCOREIA_TEST_SKIP_INTRO") != "1"
                || Time.unscaledTime < _nextIntroCheck) return;
            _nextIntroCheck = Time.unscaledTime + 1f;
            foreach (var root in Resources.FindObjectsOfTypeAll<Il2CppMonomiPark.SlimeRancher.UI.IntroSequence.IntroSequenceUIRoot>())
            {
                if (!root || !root.gameObject.activeInHierarchy) continue;
                root.EndSequence();
                _introEnded = true;
                Log.Info("test harness: intro sequence ended");
                break;
            }
        }
#endif

        /// <summary>Raised once the actor, world and avatar layers exist (owned features register their snapshot steps and handlers here).</summary>
        public static event Action GameLayersReady;

        internal static void AfterGameContext(GameContext gameContext)
        {
            _gameContextReady = true;
            Lycoreia.Owned.Components.UI.MultiplayerUI.RegisterMenuButtons();
            if (Mod.Initialised && Mod.Actors == null)
            {
                var world = new GameActorWorld();
                Mod.Actors = new Lycoreia.Actors.ActorSync(Mod.Session, Mod.Snapshots, world, () => Time.unscaledTimeAsDouble, Mod.Guest.Buffer);
                Mod.Snapshots.SendTypeRegistry = p => Mod.Node.SendTo(p, ActorTypes.BuildRegistry());
                Mod.Node.On<Lycoreia.Net.Messages.ActorTypeRegistry>(Side.Guest, (c, m) => ActorTypes.Apply(m));
                Mod.Actors.RegisterSnapshotWriter(0);
                Mod.Actors.Trace = (ev, d) => Mod.Diagnostics?.Record("actors", ev, d);
                ActorReconciler.Install(Mod.Session); Mod.Session.ServerStarted += ActorReconciler.EnsureHostTypes;
                Mod.Guest.Applier = new GameSnapshotApplier(Mod.Actors);
                Pumps.Add(new ArbiterPump()); Pumps.Add(new ActorRuntime()); Pumps.Add(new ActorReconciler());
                World.WorldRuntime.Init(Mod.Session, Mod.Snapshots); Pumps.Add(new World.WorldRuntime.Pump());
                var avatars = Avatars.RemoteAvatars.Install(Mod.Session); Pumps.Add(avatars); LatePumps.Add(avatars.Late);   // rigs pose bones after the animator
#if DEEPLOG
                Pumps.Add(new Hooks.PauseTrace.Poll());                                                                         // installs the pause hooks on the first in-game frame
                Pumps.Add(new Avatars.AvatarProbe());                                                                           // animator facts for the avatar mapping ([anim-probe] lines)
#endif
                try { GameLayersReady?.Invoke(); } catch (Exception e) { Log.Error("owned feature registration", e); }
                Mod.Session.ServerStarted += () => { Hooks.SpawnGate.Reset(); GameCommands.AutoBackup("host"); };
                Mod.Session.ClientJoined += () => { Hooks.SpawnGate.Reset(); GameCommands.AutoBackup("connect"); };
                Mod.Session.ServerStopped += ActorTypes.Clear; Mod.Session.ClientDisconnected += _ => ActorTypes.Clear();
            }
            Mod.Diagnostics?.Record("scene", "game-context-ready", $"types={ActorTypes.Count}");
        }

        public override void OnUpdate()
        {
            if (!Mod.Initialised) return;
            LagProbe.Frame();
            if (!_firstUpdate) { _firstUpdate = true; Log.Info("main-thread update started"); }
#if LYC_TEST
            try { TrySkipTestIntro(); } catch (Exception e) { Log.Error("test intro skip", e); }
#endif
            long started = LagProbe.Start();
            try { Mod.Node.Pump(); } catch (Exception e) { Log.Error("dispatcher", e); }
            finally { LagProbe.End("dispatcher", started); }
            Hooks.NetworkMenuInput.Pump();
            started = LagProbe.Start();
            try { Avatars.AvatarTemplate.PumpCapture(); } catch (Exception e) { Log.Error("avatar template", e); }
            finally { LagProbe.End("avatar-template", started); }
            foreach (var p in Pumps)
            {
                started = LagProbe.Start();
                try { p.Pump(); } catch (Exception e) { Log.Error("pump " + p.Name, e); }
                finally { LagProbe.End(p.Name, started); }
            }
        }

        public override void OnLateUpdate()
        {
            if (!Mod.Initialised) return;
            foreach (var p in LatePumps)
            {
                long started = LagProbe.Start();
                try { p.Pump(); } catch (Exception e) { Log.Error("late pump " + p.Name, e); }
                finally { if (started != 0) LagProbe.End("late-" + p.Name, started); }
            }
        }

        public override void OnApplicationQuit()
        {
            _stop = true;
            Mod.Quit(DiscordPresencePump.Client.Shutdown);
        }
    }
    /// <summary>Runs the game-context step once per GameContext instance (replaces the former framework callback).</summary>
    [HarmonyPatch(typeof(GameContext), nameof(GameContext.Start))]
    static class GameContextStartPatch
    {
        static IntPtr _last;
        [HarmonyPostfix]
        static void Postfix(GameContext __instance)
        {
            try
            {
                if (__instance == null || __instance.Pointer == IntPtr.Zero || __instance.Pointer == _last) return;
                _last = __instance.Pointer;
                LycoreiaMod.AfterGameContext(__instance);
            }
            catch (Exception e) { Log.Error("game context step", e); }
        }
    }
}
