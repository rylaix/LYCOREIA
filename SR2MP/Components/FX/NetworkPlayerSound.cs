using JetBrains.Annotations;
using MelonLoader;
using SR2MP.Packets.FX;
using Starlight.Storage;

namespace SR2MP.Components.FX;

[InjectIntoIL]
internal sealed class NetworkPlayerSound : MonoBehaviour
{
    public PlayerFXType FXType;

    private bool cachedIsPlaying;
    private SECTR_AudioCue cachedAudioCue;
    private SECTR_PointSource audioSource;
    private bool warnedAboutMissingAudioSource;

    public bool IsPlaying => audioSource.IsPlaying && !audioSource.instance.Paused;
    public SECTR_AudioCue AudioCue => audioSource.Cue;

    [UsedImplicitly]
    public void Awake()
    {
        audioSource = GetComponent<SECTR_PointSource>();
        if (!audioSource && !warnedAboutMissingAudioSource)
        {
            warnedAboutMissingAudioSource = true;
            Trace("vac audio producer has no SECTR_PointSource; suction edges cannot be observed");
        }
    }

    public void Update()
    {
        if (!audioSource)
            return;

        var isPlaying = IsPlaying;
        var cue = AudioCue;
        var hasChanged = isPlaying != cachedIsPlaying || cue != cachedAudioCue;

        cachedIsPlaying = isPlaying;
        cachedAudioCue = cue;

        if (!hasChanged)
            return;

        var mapped = FXManager.TryGetFXType(cue, out PlayerFXType fxType);
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_PLAYER_FX") == "1" && cue &&
            (cue.name.Contains("VacStart", StringComparison.OrdinalIgnoreCase) ||
             cue.name.Contains("VacEnd", StringComparison.OrdinalIgnoreCase) ||
             cue.name.Contains("VacRun", StringComparison.OrdinalIgnoreCase)))
        {
            SrLogger.LogMessage(
                $"[SR2MP_TEST] vac edge observe cue={cue.name} playing={isPlaying} mapped={mapped}" +
                (mapped ? $" fx={fxType} reliability={new PlayerFXPacket { FX = fxType }.Reliability}" : string.Empty));
        }
#endif
        if (!mapped)
            return;

        FXType = fxType;

        var packet = new PlayerFXPacket
        {
            FX = FXType,
            Player = LocalID
        };
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_PLAYER_FX") == "1")
        {
            var cueName = audioSource.Cue ? audioSource.Cue.name : "<null>";
            SrLogger.LogMessage($"[SR2MP_TEST] player-fx send source=NetworkPlayerSound fx={packet.FX} player={packet.Player} cue={cueName} playing={isPlaying} host={Main.Server.IsRunning} client={Main.Client.IsConnected} reliability={packet.Reliability}");
        }
#endif
        Main.SendToAllOrServer(packet);
    }

    private static void Trace(string message)
    {
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_PLAYER_FX") == "1")
            SrLogger.LogWarning($"[SR2MP_TEST] player-fx producer {message}");
#endif
    }
}
