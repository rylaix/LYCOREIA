// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Player.CharacterController.Abilities;

namespace Lycoreia.Owned.Patches.FX;

[HarmonyPatch(typeof(PulseWaveAbilityBehavior), nameof(PulseWaveAbilityBehavior.Start))]
internal static class OnPulseWaveStarted
{
    public static void Postfix(PulseWaveAbilityBehavior __instance)
    {
        if (HandlingPacket || (!ModFacade.Server.IsRunning && !ModFacade.Client.IsConnected))
            return;

        var origin = __instance.VacOrigin;
        if (!origin)
        {
#if LYCOREIA_TEST
            if (Environment.GetEnvironmentVariable("LYCOREIA_TEST_TRACE_PLAYER_FX") == "1")
                OwnedLog.LogWarning("[LYCOREIA_TEST] pulse-wave visual send skipped: ability VacOrigin is null");
#endif
            return;
        }

        var pulseFx = __instance.AbilityData ? __instance.AbilityData.PulseFx : null;
        if (!pulseFx)
        {
#if LYCOREIA_TEST
            if (Environment.GetEnvironmentVariable("LYCOREIA_TEST_TRACE_PLAYER_FX") == "1")
                OwnedLog.LogWarning("[LYCOREIA_TEST] pulse-wave visual send skipped: AbilityData.PulseFx is null");
#endif
            return;
        }

        var packet = new Lycoreia.Net.Messages.PlayerFx
        {
            Kind = Lycoreia.Net.Messages.PlayerFxKind.Shockwave,
            Position = origin.position.ToWire(),
            PlayerId = LocalID
        };

#if LYCOREIA_TEST
        if (Environment.GetEnvironmentVariable("LYCOREIA_TEST_TRACE_PLAYER_FX") == "1")
            OwnedLog.LogMessage(
                $"[LYCOREIA_TEST] player-fx send source=PulseWaveAbilityBehavior.Start fx={packet.Kind} " +
                $"prefab={pulseFx.name} position={packet.Position} host={ModFacade.Server.IsRunning} client={ModFacade.Client.IsConnected}");
#endif
        ModFacade.SendToAllOrServer(packet);
    }
}
