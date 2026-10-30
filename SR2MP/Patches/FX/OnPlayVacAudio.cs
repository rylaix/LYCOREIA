using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using SR2MP.Packets.FX;

namespace SR2MP.Patches.FX;

[HarmonyPatch(typeof(VacuumItem), nameof(VacuumItem.PlayTransientAudio))]
internal static class OnPlayVacAudio
{
    // Note: You CAN rename cue by using [HarmonyArgument(0)] SECTR_AudioCue youNewName - Az
    public static void Postfix(SECTR_AudioCue cue)
    {
        if (!FXManager.TryGetFXType(cue, out PlayerFXType fxType))
        {
            return;
        }

        // Suction state follows the native input lifecycle hooks so remote
        // visuals do not depend on a particular audio cue being emitted.
        if (PlayerFXPacket.IsSuctionStateTransition(fxType))
            return;

        var packet = new PlayerFXPacket
        {
            FX = fxType,
            Player = LocalID
        };

#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_PLAYER_FX") == "1")
            SrLogger.LogMessage($"[SR2MP_TEST] player-fx send source=VacuumItem.PlayTransientAudio fx={packet.FX} player={packet.Player} cue={cue.name} reliability={packet.Reliability}");
#endif
        Main.SendToAllOrServer(packet);
    }
}
