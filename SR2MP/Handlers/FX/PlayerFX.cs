using System.Net;
using SR2MP.Components.FX;
using SR2MP.Components.Player;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.FX;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.FX;

[PacketHandler((byte)PacketType.PlayerFX)]
internal sealed class PlayerFXHandler : BasePacketHandler<PlayerFXPacket>
{
    protected override bool Handle(PlayerFXPacket packet, IPEndPoint? clientEp)
    {
        if (!IsPlayerSoundDictionary.TryGetValue(packet.FX, out var isSound))
        {
#if SR2MP_TEST
            if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_PLAYER_FX") == "1")
                SrLogger.LogWarning($"[SR2MP_TEST] rejected unknown player-fx enum {(byte)packet.FX}");
#endif
            return false;
        }

        if (IsServerSide && isSound &&
            !RequireAuthenticatedPlayer(clientEp, packet.Player))
            return false;

#if SR2MP_TEST
        var tracePlayerFx = Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_PLAYER_FX") == "1";
        if (tracePlayerFx)
            SrLogger.LogMessage($"[SR2MP_TEST] player-fx receive side={(IsServerSide ? "server" : "client")} fx={packet.FX} player={packet.Player} position={packet.Position} reliability={packet.Reliability}");
#endif

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;

        try
        {
            if (!isSound)
            {
                var playerFxMap = FXManager.PlayerFXMap;
                if (playerFxMap == null || !playerFxMap.TryGetValue(packet.FX, out var fxPrefab))
                    return false;

                if (!fxPrefab)
                {
#if SR2MP_TEST
                    if (tracePlayerFx)
                        SrLogger.LogWarning($"[SR2MP_TEST] player-fx visual prefab unavailable fx={packet.FX}");
#endif
                    return true;
                }

                FXHelpers.SpawnAndPlayFX(fxPrefab, packet.Position, Quaternion.identity);
#if SR2MP_TEST
                if (tracePlayerFx && packet.FX == PlayerFXType.VacShockwave)
                    SrLogger.LogMessage($"[SR2MP_TEST] player-fx shockwave spawned prefab={fxPrefab.name} position={packet.Position}");
#endif
            }
            else
            {
                if (PlayerFXPacket.IsSuctionStateTransition(packet.FX))
                {
                    var running = packet.FX == PlayerFXType.VacRunningStart;
                    var applied = RemoteVacuumSuctionFX.SetSuction(packet.Player, running, out var result);
#if SR2MP_TEST
                    if (tracePlayerFx)
                        SrLogger.LogMessage($"[SR2MP_TEST] player-fx receiver suction {(running ? "start" : "end")} player={packet.Player} accepted={applied} result={result}");
#endif
                }

                if (!PlayerObjects.TryGetValue(packet.Player, out var remotePlayer) || !remotePlayer)
                {
#if SR2MP_TEST
                    if (tracePlayerFx)
                        SrLogger.LogWarning($"[SR2MP_TEST] player-fx receiver missing remote player fx={packet.FX} player={packet.Player}; suction edge queued when applicable");
#endif
                    return true;
                }

                PlayPlayerAudio(packet, remotePlayer);
            }
        }
        catch (Exception ex)
        {
#if SR2MP_TEST
            if (tracePlayerFx)
                SrLogger.LogWarning($"[SR2MP_TEST] player-fx receiver failed fx={packet.FX} player={packet.Player}: {ex.GetType().Name}: {ex.Message}");
#endif
            // Non-critical; typically triggered during scene transitions.
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        return true;
    }

    private static void PlayPlayerAudio(PlayerFXPacket packet, GameObject remotePlayer)
    {
        if (!FXManager.PlayerAudioCueMap.TryGetValue(packet.FX, out var cue) || !cue ||
            !ShouldPlayerSoundBeTransientDictionary.TryGetValue(packet.FX, out var isTransient) ||
            !PlayerSoundVolumeDictionary.TryGetValue(packet.FX, out var volume))
            return;

        if (isTransient)
        {
            RemoteFXManager.PlayTransientAudio(
                cue,
                remotePlayer.transform.position,
                volume);
        }
        else
        {
            var playerAudio = remotePlayer.GetComponent<SECTR_PointSource>();
            if (!playerAudio)
                return;

            playerAudio.Cue              = cue;
            playerAudio.Loop             = DoesPlayerSoundLoopDictionary.TryGetValue(packet.FX, out var loop) && loop;
            playerAudio.instance.Volume  = volume;
            playerAudio.Play();
        }
    }
}
