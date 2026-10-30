using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.FX;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.FX;

[PacketHandler((byte)PacketType.WorldFX)]
internal sealed class WorldFXHandler : BasePacketHandler<WorldFXPacket>
{
    protected override bool Handle(WorldFXPacket packet, IPEndPoint? _)
    {
        if (!IsWorldSoundDictionary.TryGetValue(packet.FX, out var isSound))
            return true;

        if (!isSound)
        {
            var worldFxMap = FXManager.WorldFXMap;
            if (worldFxMap == null || !worldFxMap.TryGetValue(packet.FX, out var fxPrefab) || !fxPrefab)
                return true; // World FX are transient; an early packet can be safely dropped before scene assets load.

            var wasHandlingPacket = HandlingPacket;
            HandlingPacket = true;
            try
            {
                FXHelpers.SpawnAndPlayFX(fxPrefab, packet.Position, Quaternion.identity);
            }
            catch (Exception ex)
            {
                SrLogger.LogDebug($"Could not play remote world FX {packet.FX}: {ex.Message}");
            }
            finally
            {
                HandlingPacket = wasHandlingPacket;
            }
        }
        else
        {
            var audioMap = FXManager.WorldAudioCueMap;
            if (audioMap == null
                || !audioMap.TryGetValue(packet.FX, out var cue)
                || !cue
                || !WorldSoundVolumeDictionary.TryGetValue(packet.FX, out var volume))
                return true;

            try
            {
                RemoteFXManager.PlayTransientAudio(cue, packet.Position, volume);
            }
            catch (Exception ex)
            {
                SrLogger.LogDebug($"Could not play remote world audio {packet.FX}: {ex.Message}");
            }
        }

        return true;
    }
}
