using SR2MP.Packets.World;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Keeps gadget blueprints and slime appearance unlocks identical on every peer. The host's save
/// is the history: joiners receive its full set, and any later unlock on any peer is sent as an
/// addition (host relays). Unlock-only union, so ordering and duplicates are harmless.
/// </summary>
internal static class NetworkProgressionUnlockManager
{
    private static readonly HashSet<int> KnownBlueprints = new();
    private static readonly HashSet<(int SlimeId, Il2Cpp.SlimeAppearance.AppearanceSaveSet SaveSet)> KnownAppearances = new();
    private static readonly HashSet<string> KnownPalettes = new(StringComparer.Ordinal);
    private static bool baselineReady;
    private static bool clientSnapshotReceived;
    private static bool hooksRegistered;
    private static float nextPumpAt;

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
        KnownBlueprints.Clear();
        KnownAppearances.Clear();
        KnownPalettes.Clear();
        baselineReady = false;
        clientSnapshotReceived = false;
    }

    /// <summary>Host: the complete unlock set for a joining client.</summary>
    internal static ProgressionUnlocksPacket CreateSnapshot()
    {
        var packet = new ProgressionUnlocksPacket();
        Collect(packet.BlueprintIds, packet.Appearances, packet.OwnedPalettes);
        return packet;
    }

    /// <summary>Main-thread pump: sends unlocks gained locally since the last check.</summary>
    internal static void Pump()
    {
        if (!Main.Server.IsRunning && !Main.Client.IsConnected)
            return;

        var now = UnityEngine.Time.unscaledTime;
        if (now < nextPumpAt)
            return;
        nextPumpAt = now + 2f;

        EnsureHooks();
        // A guest's own save is not authoritative: start tracking only after the host set applied.
        if (Main.Client.IsConnected && !clientSnapshotReceived)
            return;

        var blueprints = new List<int>();
        var appearances = new List<ProgressionUnlocksPacket.AppearanceUnlock>();
        var palettes = new List<string>();
        if (!Collect(blueprints, appearances, palettes))
            return;

        var packet = new ProgressionUnlocksPacket();
        foreach (var id in blueprints)
            if (KnownBlueprints.Add(id) && baselineReady)
                packet.BlueprintIds.Add(id);
        foreach (var appearance in appearances)
            if (KnownAppearances.Add((appearance.SlimeId, appearance.SaveSet)) && baselineReady)
                packet.Appearances.Add(appearance);

        foreach (var palette in palettes)
            if (KnownPalettes.Add(palette) && baselineReady)
                packet.OwnedPalettes.Add(palette);

        baselineReady = true;
        if (packet.BlueprintIds.Count == 0 && packet.Appearances.Count == 0 && packet.OwnedPalettes.Count == 0)
            return;

        Main.SendToAllOrServer(packet);
        Logger.LogDiagnostic("progression", "unlocks-sent",
            $"blueprints={packet.BlueprintIds.Count}; appearances={packet.Appearances.Count}; palettes={packet.OwnedPalettes.Count}");
    }

    /// <summary>Applies a received set (snapshot or additions). Returns true when anything was new.</summary>
    internal static bool Apply(ProgressionUnlocksPacket packet)
    {
        EnsureHooks();
        var changed = false;
        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            var gadgets = GameState?.GadgetsModel;
            foreach (var id in packet.BlueprintIds)
            {
                KnownBlueprints.Add(id);
                if (gadgets?.blueprints == null ||
                    !ActorManager.ActorTypes.TryGetValue(id, out var type) || type == null)
                    continue;

                var definition = type.TryCast<Il2Cpp.GadgetDefinition>();
                if (!definition || gadgets.blueprints.Contains(definition))
                    continue;

                try
                {
                    gadgets.RegisterBlueprint(definition);
                    changed = true;
                }
                catch (Exception ex)
                {
                    SrLogger.LogWarning($"Could not unlock blueprint {type.name}: {ex.Message}");
                }
            }

            var director = SceneContext.Instance ? SceneContext.Instance.SlimeAppearanceDirector : null;
            foreach (var unlock in packet.Appearances)
            {
                KnownAppearances.Add((unlock.SlimeId, unlock.SaveSet));
                if (!director || !ActorManager.ActorTypes.TryGetValue(unlock.SlimeId, out var type) || type == null)
                    continue;

                var definition = type.TryCast<Il2Cpp.SlimeDefinition>();
                var appearance = definition ? definition!.GetAppearanceForSet(unlock.SaveSet) : null;
                if (!appearance || appearance!.SaveSet != unlock.SaveSet ||
                    director!.IsAppearanceUnlocked(definition, appearance))
                    continue;

                try
                {
                    director.UnlockAppearance(definition, appearance);
                    changed = true;
                }
                catch (Exception ex)
                {
                    SrLogger.LogWarning($"Could not unlock appearance {type.name}/{unlock.SaveSet}: {ex.Message}");
                }
            }

            // Shop-bought vac/ranch skins: a guest purchase is executed on the host, so ownership
            // only ever lands in the host's inventory unless it is shared here.
            var paletteDirector = SceneContext.Instance ? SceneContext.Instance.PaletteDirector : null;
            var paletteList = paletteDirector ? paletteDirector!._palettes?.items : null;
            foreach (var referenceId in packet.OwnedPalettes)
            {
                KnownPalettes.Add(referenceId);
                if (paletteList == null || paletteDirector!.IsOwnedByReferenceId(referenceId))
                    continue;

                foreach (var palette in paletteList)
                {
                    if (!palette || palette.ReferenceId != referenceId)
                        continue;
                    try
                    {
                        paletteDirector.SetOwned(palette);
                        changed = true;
                    }
                    catch (Exception ex)
                    {
                        SrLogger.LogWarning($"Could not grant skin {referenceId}: {ex.Message}");
                    }
                    break;
                }
            }
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        Logger.LogDiagnostic("progression", "unlocks-applied",
            $"blueprints={packet.BlueprintIds.Count}; appearances={packet.Appearances.Count}; " +
            $"palettes={packet.OwnedPalettes.Count}; changed={changed}");

        if (Main.Client.IsConnected && !clientSnapshotReceived)
        {
            // Everything the guest has now (host set + its own) is the baseline; only later
            // unlocks are reported back.
            clientSnapshotReceived = true;
            baselineReady = false;
        }

        return changed;
    }

    private static bool Collect(List<int> blueprints, List<ProgressionUnlocksPacket.AppearanceUnlock> appearances,
        List<string> ownedPalettes)
    {
        var paletteDirector = SceneContext.Instance ? SceneContext.Instance.PaletteDirector : null;
        var paletteList = paletteDirector ? paletteDirector!._palettes?.items : null;
        if (paletteList != null)
            foreach (var palette in paletteList)
                if (palette && !string.IsNullOrEmpty(palette.ReferenceId) && paletteDirector!.IsOwned(palette))
                    ownedPalettes.Add(palette.ReferenceId);

        var gadgets = GameState?.GadgetsModel;
        var director = SceneContext.Instance ? SceneContext.Instance.SlimeAppearanceDirector : null;
        var slimes = director ? director!.SlimeDefinitions?.Slimes : null;
        if (gadgets?.blueprints == null || slimes == null)
            return false;

        foreach (var definition in gadgets.blueprints)
        {
            if (!definition)
                continue;
            try
            {
                blueprints.Add(NetworkActorManager.GetPersistentID(definition));
            }
            catch
            {
                // Not a networked identifiable type.
            }
        }

        foreach (var slime in slimes)
        {
            if (!slime)
                continue;

            int slimeId;
            try
            {
                slimeId = NetworkActorManager.GetPersistentID(slime);
            }
            catch
            {
                continue;
            }

            var candidates = new List<Il2Cpp.SlimeAppearance>();
            if (slime!.AppearancesDefault != null)
                foreach (var appearance in slime.AppearancesDefault)
                    candidates.Add(appearance);
            if (slime.AppearancesDynamic != null)
                foreach (var appearance in slime.AppearancesDynamic)
                    candidates.Add(appearance);

            foreach (var appearance in candidates)
            {
                if (appearance && director!.IsAppearanceUnlocked(slime, appearance))
                    appearances.Add(new ProgressionUnlocksPacket.AppearanceUnlock
                    {
                        SlimeId = slimeId,
                        SaveSet = appearance.SaveSet
                    });
            }
        }

        return true;
    }
}
