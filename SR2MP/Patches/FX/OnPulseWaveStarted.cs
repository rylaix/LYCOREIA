using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Player.CharacterController.Abilities;
using SR2MP.Packets.FX;

namespace SR2MP.Patches.FX;

[HarmonyPatch(typeof(PulseWaveAbilityBehavior), nameof(PulseWaveAbilityBehavior.Start))]
internal static class OnPulseWaveStarted
{
    public static void Postfix(PulseWaveAbilityBehavior __instance)
    {
        if (HandlingPacket || (!Main.Server.IsRunning && !Main.Client.IsConnected))
            return;

        var origin = __instance.VacOrigin;
        if (!origin)
        {
#if SR2MP_TEST
            if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_PLAYER_FX") == "1")
                SrLogger.LogWarning("[SR2MP_TEST] pulse-wave visual send skipped: ability VacOrigin is null");
#endif
            return;
        }

        var pulseFx = __instance.AbilityData ? __instance.AbilityData.PulseFx : null;
        if (!pulseFx)
        {
#if SR2MP_TEST
            if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_PLAYER_FX") == "1")
                SrLogger.LogWarning("[SR2MP_TEST] pulse-wave visual send skipped: AbilityData.PulseFx is null");
#endif
            return;
        }

        var packet = new PlayerFXPacket
        {
            FX = PlayerFXType.VacShockwave,
            Position = origin.position
        };

#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_PLAYER_FX") == "1")
            SrLogger.LogMessage(
                $"[SR2MP_TEST] player-fx send source=PulseWaveAbilityBehavior.Start fx={packet.FX} " +
                $"prefab={pulseFx.name} position={packet.Position} host={Main.Server.IsRunning} client={Main.Client.IsConnected}");
#endif
        Main.SendToAllOrServer(packet);
    }
}
