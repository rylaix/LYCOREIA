using SR2MP.Shared.Utils;
using Il2CppMonomiPark.SlimeRancher.Ranch;
using MelonLoader.Utils;
using SR2MP.Components.Player;
using SR2MP.Packets.Player;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Per-player vacpack skins. The game has one global vac palette that every registered
/// ApplyPalette follows, so remote avatars are detached from it and painted with their
/// owner's palette. Each player's choice is kept in their own UserData and re-sent on join.
/// </summary>
internal static class NetworkVacPaletteManager
{
    private static readonly Dictionary<string, string> PaletteByPlayer = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, string> AppliedByAvatar = new();
    private static string? localPalette;
    private static bool localLoaded;
    private static bool localSent;
    private static bool hooksRegistered;
    private static float nextPumpAt;

    private static string PreferencePath
        => Path.Combine(ModPaths.DataDirectory, "vac-palette.txt");

    private static void EnsureHooks()
    {
        if (hooksRegistered)
            return;

        hooksRegistered = true;
        Main.Server.OnServerStarted += Reset;
        Main.Client.OnConnected += _ => Reset();
        Main.Client.OnDisconnected += Reset;
    }

    private static void Reset()
    {
        PaletteByPlayer.Clear();
        AppliedByAvatar.Clear();
        localSent = false;
    }

    /// <summary>SetPalette postfix: the local player picked a vac skin at home.</summary>
    internal static void OnLocalPaletteSet(PaletteTarget target, Palette palette)
    {
        if (HandlingPacket || !target || !palette || !target.TryCast<VacPaletteTarget>())
            return;

        localPalette = palette.ReferenceId;
        localLoaded = true;
        try
        {
            File.WriteAllText(PreferencePath, localPalette);
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Could not save vac skin preference: {ex.Message}");
        }

        localSent = false;
        SendLocal();
    }

    private static void SendLocal()
    {
        if (localSent || string.IsNullOrEmpty(localPalette) || !Main.IsNetworkActive)
            return;

        localSent = true;
        var packet = new PlayerVacPalettePacket { PlayerId = LocalID, PaletteReferenceId = localPalette! };
        PaletteByPlayer[LocalID] = localPalette!;
        Main.SendToAllOrServer(packet);
    }

    /// <summary>Host: every known skin for a joining client.</summary>
    internal static void SendAllTo(System.Net.IPEndPoint client)
    {
        EnsureHooks();
        foreach (var (playerId, palette) in PaletteByPlayer)
            Main.Server.SendToClient(new PlayerVacPalettePacket { PlayerId = playerId, PaletteReferenceId = palette }, client);
    }

    /// <summary>Returns true when the host should relay the (stamped) packet.</summary>
    internal static bool Receive(PlayerVacPalettePacket packet, System.Net.IPEndPoint? sender)
    {
        EnsureHooks();
        if (Main.Server.IsRunning)
        {
            if (sender == null || !Main.Server.ClientManager.TryGetClient(sender, out var client) || client == null)
                return false;
            packet.PlayerId = client.PlayerId;
        }

        if (string.IsNullOrEmpty(packet.PlayerId) || packet.PlayerId == LocalID ||
            string.IsNullOrEmpty(packet.PaletteReferenceId) || packet.PaletteReferenceId.Length > 128)
            return false;

        PaletteByPlayer[packet.PlayerId] = packet.PaletteReferenceId;
        foreach (var key in AppliedByAvatar.Keys.ToList())
            AppliedByAvatar.Remove(key); // repaint on next pump
        return Main.Server.IsRunning;
    }

    /// <summary>Main-thread pump: restores the local choice and paints remote avatars.</summary>
    internal static void Pump()
    {
        if (!Main.Server.IsRunning && !Main.Client.IsConnected)
            return;

        var now = UnityEngine.Time.unscaledTime;
        if (now < nextPumpAt)
            return;
        nextPumpAt = now + 1f;

        EnsureHooks();
        var scene = SceneContext.Instance;
        var director = scene ? scene.PaletteDirector : null;
        if (!director || SystemContext.Instance.SceneLoader.IsSceneLoadInProgress)
            return;

        var target = FindVacTarget(director!);
        if (!target)
            return;

        RestoreLocal(director!, target!);
        SendLocal();
        PaintRemoteAvatars(director!, target!);
    }

    private static void RestoreLocal(PaletteDirector director, PaletteTarget target)
    {
        if (!localLoaded)
        {
            localLoaded = true;
            try
            {
                if (File.Exists(PreferencePath))
                    localPalette = File.ReadAllText(PreferencePath).Trim();
            }
            catch
            {
                localPalette = null;
            }

            var current = director.GetPaletteForTarget(target);
            if (string.IsNullOrEmpty(localPalette))
                localPalette = current ? current!.ReferenceId : null;
        }

        // A guest's world comes from the host save, which carries the host's vac skin.
        var active = director.GetPaletteForTarget(target);
        if (string.IsNullOrEmpty(localPalette) || (active && active!.ReferenceId == localPalette))
            return;

        var palette = FindPalette(director, localPalette!);
        if (!palette)
            return;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            director.SetPalette(target, palette);
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
    }

    private static void PaintRemoteAvatars(PaletteDirector director, PaletteTarget target)
    {
        if (!director._applicators.TryGetValue(target, out var rawApplicator) || rawApplicator == null)
            return;

        var applicator = rawApplicator.TryCast<VacPaletteApplicator>();
        var vacTarget = target.TryCast<VacPaletteTarget>();
        if (applicator == null || !vacTarget)
            return;

        foreach (var avatar in UnityEngine.Object.FindObjectsOfType<NetworkPlayer>())
        {
            if (!avatar || string.IsNullOrEmpty(avatar.ID) || avatar.ID == LocalID)
                continue;

            var wanted = PaletteByPlayer.TryGetValue(avatar.ID, out var id) ? id : string.Empty;
            var key = avatar.GetInstanceID();
            if (AppliedByAvatar.TryGetValue(key, out var applied) && applied == wanted)
                continue;

            var palette = string.IsNullOrEmpty(wanted) ? null : FindPalette(director, wanted)?.TryCast<VacPalette>();
            palette ??= vacTarget!.DefaultVacPalette;
            if (!palette)
                continue;

            var painted = 0;
            foreach (var applyPalette in avatar.GetComponentsInChildren<ApplyPalette>(true))
            {
                if (!applyPalette || applyPalette.Target != target)
                    continue;

                try
                {
                    // Detach from the global vac skin, then paint this avatar's own.
                    director.Unregister(applyPalette);
                    applicator.Apply(applyPalette, palette);
                    painted++;
                }
                catch (Exception ex)
                {
                    SrLogger.LogWarning($"Could not paint vac skin for {avatar.ID}: {ex.Message}");
                }
            }

            AppliedByAvatar[key] = wanted;
            Logger.LogDiagnostic("player", "vac-palette-applied",
                $"player={avatar.ID}; palette={(string.IsNullOrEmpty(wanted) ? "<default>" : wanted)}; renderers={painted}");
        }
    }

    private static PaletteTarget? FindVacTarget(PaletteDirector director)
    {
        var targets = director._paletteTargets?.items;
        if (targets == null)
            return null;

        foreach (var target in targets)
            if (target && target.TryCast<VacPaletteTarget>())
                return target;
        return null;
    }

    private static Palette? FindPalette(PaletteDirector director, string referenceId)
    {
        var palettes = director._palettes?.items;
        if (palettes == null)
            return null;

        foreach (var palette in palettes)
            if (palette && palette.ReferenceId == referenceId)
                return palette;
        return null;
    }
}
