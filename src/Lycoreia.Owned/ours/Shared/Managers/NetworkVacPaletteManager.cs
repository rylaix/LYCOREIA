// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Owned.Shared.Utils;
using Il2CppMonomiPark.SlimeRancher.Ranch;
using MelonLoader.Utils;

namespace Lycoreia.Owned.Shared.Managers;

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
        Mod.Session.ServerStarted += Reset;                       // session events
        Mod.Session.ClientJoined += Reset;
        Mod.Session.ClientDisconnected += _ => Reset();
    }

    private static void Reset()
    {
        PaletteByPlayer.Clear();
        AppliedByAvatar.Clear();
        UnpaintableLogged.Clear();
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
            OwnedLog.LogWarning($"Could not save vac skin preference: {ex.Message}");
        }

        localSent = false;
        SendLocal();
    }

    private static void SendLocal()
    {
        if (localSent || string.IsNullOrEmpty(localPalette) || !ModFacade.IsNetworkActive)
            return;

        localSent = true;
        var packet = new Lycoreia.Net.Messages.PlayerVacPalette { PlayerId = LocalID, PaletteId = localPalette! };
        PaletteByPlayer[LocalID] = localPalette!;
        ModFacade.SendToAllOrServer(packet);
    }

    /// <summary>Host: every known skin for a joining client.</summary>
    internal static void SendAllTo(Lycoreia.Snapshot.SnapshotContext client)
    {
        EnsureHooks();
        foreach (var (playerId, palette) in PaletteByPlayer)
            client.Send(new Lycoreia.Net.Messages.PlayerVacPalette { PlayerId = playerId, PaletteId = palette });
    }

    /// <summary>Returns true when the host should relay the (stamped) packet.</summary>
    internal static bool Receive(Lycoreia.Net.Messages.PlayerVacPalette packet, string? authenticatedSender)
    {
        EnsureHooks();
        if (ModFacade.Server.IsRunning)
        {
            if (string.IsNullOrEmpty(authenticatedSender))       // the sender comes from the connection
                return false;
            packet.PlayerId = authenticatedSender;
        }

        if (string.IsNullOrEmpty(packet.PlayerId) || packet.PlayerId == LocalID ||
            string.IsNullOrEmpty(packet.PaletteId) || packet.PaletteId.Length > 128)
            return false;

        PaletteByPlayer[packet.PlayerId] = packet.PaletteId;
        foreach (var key in AppliedByAvatar.Keys.ToList())
            AppliedByAvatar.Remove(key); // repaint on next pump
        return ModFacade.Server.IsRunning;
    }

    /// <summary>ModFacade-thread pump: restores the local choice and paints remote avatars.</summary>
    internal static void Pump()
    {
        if (!ModFacade.Server.IsRunning && !ModFacade.Client.IsConnected)
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

        var wasHandlingPacket = Lycoreia.Net.Applying.Enter();   // scoped applying flag
        try
        {
            director.SetPalette(target, palette);
        }
        finally
        {
            wasHandlingPacket.Dispose();
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

        foreach (var avatar in Lycoreia.Game.Avatars.RemoteAvatars.All.Values)   // the avatar roster
        {
            if (avatar.Root == null || string.IsNullOrEmpty(avatar.PlayerId) || avatar.PlayerId == LocalID)
                continue;

            var wanted = PaletteByPlayer.TryGetValue(avatar.PlayerId, out var id) ? id : string.Empty;
            var key = avatar.GetInstanceID();
            if (AppliedByAvatar.TryGetValue(key, out var applied) && applied == wanted)
                continue;

            var palette = string.IsNullOrEmpty(wanted) ? null : FindPalette(director, wanted)?.TryCast<VacPalette>();
            palette ??= vacTarget!.DefaultVacPalette;
            if (!palette)
                continue;

            var painted = 0;
            foreach (var applyPalette in avatar.Root.GetComponentsInChildren<ApplyPalette>(true))
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
                    OwnedLog.LogWarning($"Could not paint vac skin for {avatar.PlayerId}: {ex.Message}");
                }
            }

            if (painted == 0)
                painted = PaintByLocalLayout(director, vacTarget!, palette, avatar.Root);

            // Nothing was painted (the menu-model copy may carry no ApplyPalette): retry on a later pump instead of caching a no-op.
            if (painted == 0)
            {
                if (UnpaintableLogged.Add(key))
                    DiagLog.LogDiagnostic("player", "vac-palette-unpainted", $"player={avatar.PlayerId}; palette={(string.IsNullOrEmpty(wanted) ? "<default>" : wanted)}; applyPalettes={avatar.Root.GetComponentsInChildren<ApplyPalette>(true).Length}");
                continue;
            }

            AppliedByAvatar[key] = wanted;
            DiagLog.LogDiagnostic("player", "vac-palette-applied",
                $"player={avatar.PlayerId}; palette={(string.IsNullOrEmpty(wanted) ? "<default>" : wanted)}; renderers={painted}");
        }
    }

    private static readonly HashSet<int> UnpaintableLogged = new();

    /// <summary>
    /// Fallback when the avatar has no ApplyPalette: mirror the local vacpack's slot layout. For every slot assignment of the
    /// local (scene) ApplyPalette, renderers of the avatar with the same name get that slot's material from the wanted palette.
    /// </summary>
    private static int PaintByLocalLayout(PaletteDirector director, VacPaletteTarget target, VacPalette palette, GameObject avatarRoot)
    {
        var painted = 0;
        try
        {
            var avatarRenderers = avatarRoot.GetComponentsInChildren<Renderer>(true);
            foreach (var local in Resources.FindObjectsOfTypeAll<ApplyPalette>())
            {
                if (!local || !local.gameObject.scene.IsValid() || local.Target == null || local.Target.Pointer != target.Pointer ||
                    local.transform.IsChildOf(avatarRoot.transform) || local.PaletteSlotAssignments == null)
                    continue;

                foreach (var assignment in local.PaletteSlotAssignments)
                {
                    if (assignment?.paletteSlotIdentifier == null || assignment.renderers == null)
                        continue;
                    Material? material = null;
                    foreach (var slot in palette._materialSlots)
                        if (slot?.Slot != null && slot.Slot.Pointer == assignment.paletteSlotIdentifier.Pointer) { material = slot.Material; break; }
                    if (!material)
                        continue;

                    foreach (var source in assignment.renderers)
                    {
                        if (!source) continue;
                        foreach (var candidate in avatarRenderers)
                            if (candidate && candidate.name == source.name)
                            {
                                var count = Math.Max(1, candidate.sharedMaterials.Length);
                                var materials = new Il2CppReferenceArray<Material>(count);
                                for (var i = 0; i < count; i++) materials[i] = material;
                                candidate.sharedMaterials = materials;
                                painted++;
                            }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            OwnedLog.LogWarning($"Could not paint vac skin by layout: {ex.Message}");
        }
        return painted;
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
