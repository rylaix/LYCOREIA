// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia.Owned.Shared.Managers;

/// <summary>In-memory form of a pod message as the owned manager uses it; converts to and from the wire message.</summary>
internal sealed class TreasurePodPacket
{
    public int ID; public uint RequestId; public bool IsRejectedResponse, ClientActivated; public RewardSnapshot Rewards = new();

    internal sealed class RewardSnapshot
    {
        public List<string> BlueprintReferenceIds = new(), UpgradeComponentReferenceIds = new(); public List<AppearanceReward> AppearanceRewards = new();
        public bool HasSupportedRewards => BlueprintReferenceIds.Count > 0 || UpgradeComponentReferenceIds.Count > 0 || AppearanceRewards.Count > 0;
        internal sealed class AppearanceReward { public string SlimeDefinitionReferenceId; public Il2Cpp.SlimeAppearance.AppearanceSaveSet SaveSet; }

        public PodRewards ToWire() => new PodRewards
        {
            Blueprints = BlueprintReferenceIds.ToList(), Components = UpgradeComponentReferenceIds.ToList(),
            Appearances = AppearanceRewards.Select(a => new PodAppearance { SlimeDefinition = a.SlimeDefinitionReferenceId, SaveSet = (int)a.SaveSet }).ToList()
        };
        public static RewardSnapshot FromWire(PodRewards w) => new RewardSnapshot
        {
            BlueprintReferenceIds = w.Blueprints?.ToList() ?? new List<string>(), UpgradeComponentReferenceIds = w.Components?.ToList() ?? new List<string>(),
            AppearanceRewards = (w.Appearances ?? new List<PodAppearance>()).Select(a => new AppearanceReward { SlimeDefinitionReferenceId = a.SlimeDefinition, SaveSet = (Il2Cpp.SlimeAppearance.AppearanceSaveSet)a.SaveSet }).ToList()
        };
    }

    public static implicit operator PodOpen(TreasurePodPacket p) => new PodOpen { PodId = p.ID, RequestId = p.RequestId, Rejected = p.IsRejectedResponse, ClientActivated = p.ClientActivated, Rewards = (p.Rewards ?? new RewardSnapshot()).ToWire() };
    public static TreasurePodPacket From(PodOpen m) => new TreasurePodPacket { ID = m.PodId, RequestId = m.RequestId, IsRejectedResponse = m.Rejected, ClientActivated = m.ClientActivated, Rewards = RewardSnapshot.FromWire(m.Rewards) };
    /// <summary>Converts through the wire message (used by the owned test-flavour self check).</summary>
    public static TreasurePodPacket From(TreasurePodPacket p) => From((PodOpen)p);
}

internal sealed class InitialTreasurePodsPacket
{
    public Dictionary<int, Il2Cpp.TreasurePod.State> TreasurePods = new();
    public Dictionary<int, TreasurePodPacket.RewardSnapshot> RewardsByPod = new();
}

internal static partial class NetworkTreasurePodManager
{
    /// <summary>Called once the game layers exist: message handlers and the join snapshot step.</summary>
    internal static void Register()
    {
        Mod.Node.On<PodOpen>(Side.Host, (c, m) =>
        {
            if (m.ClientActivated && m.Rejected) HandleGuestActivationDeclined(m.PodId, c.Sender);
            else if (m.ClientActivated) HandleGuestActivationReport(m.PodId, TreasurePodPacket.RewardSnapshot.FromWire(m.Rewards), c.Sender);
            else HandleGuestOpenRequest(m.PodId, m.RequestId, c.Sender);
            return Verdict.Reject;                                                                              // never relayed as committed state
        });
        Mod.Node.On<PodOpen>(Side.Guest, (c, m) =>
        {
            if (m.Rejected && !m.ClientActivated) { HandleHostOpenRejection(m.PodId, m.RequestId); return; }
            if (m.ClientActivated) { HandleHostActivationGrant(m.PodId, m.RequestId); return; }
            var rewards = TreasurePodPacket.RewardSnapshot.FromWire(m.Rewards);
            ShowRewardPopups(m.PodId, rewards);                                                                 // before the state: a pod already open stays silent
            ApplyState(m.PodId, Il2Cpp.TreasurePod.State.OPEN, rewards);
        });
        Mod.Snapshots.Register(Lycoreia.Snapshot.SnapStep.TreasurePods, ctx =>
        {
            var packet = new InitialTreasurePodsPacket(); var pods = GameState?.pods;
            if (pods != null)
                foreach (var entry in pods)
                    if (entry.value != null && entry.value.state != null && TryParsePodKey(entry.key, out var id))
                        packet.TreasurePods[id] = entry.value.state.Value;
            PopulateInitialRewardSnapshot(packet);
            var msg = new InitialTreasurePods();
            foreach (var (id, state) in packet.TreasurePods) msg.Pods[id] = (int)state;
            foreach (var (id, r) in packet.RewardsByPod) msg.Rewards[id] = r.ToWire();
            ctx.Send(msg);
        });
        Mod.Node.On<InitialTreasurePods>(Side.Guest, (c, m) =>
        {
            foreach (var (id, state) in m.Pods)                                                                  // snapshots never show popups
                ApplyState(id, (Il2Cpp.TreasurePod.State)state, m.Rewards.TryGetValue(id, out var r) ? TreasurePodPacket.RewardSnapshot.FromWire(r) : null);
        });
    }
}

/// <summary>
/// TreasurePod.Activate pays one-time rewards. A guest never runs it on its own: it asks the host (which activates if it has the pod
/// loaded, or delegates the activation back to this guest). The host's own activation publishes the committed open once.
/// </summary>
[HarmonyPatch(typeof(Il2Cpp.TreasurePod), nameof(Il2Cpp.TreasurePod.Activate))]
internal static class TreasurePodActivateGate
{
    public static bool Prefix(Il2Cpp.TreasurePod __instance)
    {
        try
        {
            if (!Mod.IsNetworkActive || !NetworkTreasurePodManager.TryGetPodId(__instance, out var id)) return true;
            if (Mod.IsHost) return true;
            if (NetworkTreasurePodManager.IsClientActivationGranted(id)) return true;
            if (!HandlingPacket) NetworkTreasurePodManager.RequestHostOpen(id);
            return false;
        }
        catch (Exception e) { OwnedLog.LogWarning("TreasurePod.Activate gate: " + e.Message); return true; }
    }
    public static void Postfix(Il2Cpp.TreasurePod __instance)
    {
        try
        {
            if (!Mod.IsNetworkActive || !NetworkTreasurePodManager.TryGetPodId(__instance, out var id)) return;
            if (Mod.IsHost) { if (!NetworkTreasurePodManager.IsHostActivationInProgress(id)) NetworkTreasurePodManager.RecordHostOpen(id); }
            else if (NetworkTreasurePodManager.IsClientActivationGranted(id)) NetworkTreasurePodManager.RecordLocalOpen(__instance);
        }
        catch (Exception e) { OwnedLog.LogWarning("TreasurePod.Activate commit: " + e.Message); }
    }
}
