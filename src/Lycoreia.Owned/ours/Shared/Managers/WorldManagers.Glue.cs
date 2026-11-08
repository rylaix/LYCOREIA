// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia.Owned.Shared.Managers;

internal static class WorldManagersGlue
{
    internal static void Register()
    {
        NetworkPrismaDisruptionManager.Initialize(); NetworkStabilizingFieldSpawnerManager.Initialize(); NetworkSanctuaryManager.Initialize(); RegisterProgression();

        // prisma disruption: host-only state; the guest keeps a pending copy (received messages are pooled: clone)
        Mod.Snapshots.Register(Lycoreia.Snapshot.SnapStep.PrismaDisruption, ctx => { var p = NetworkPrismaDisruptionManager.CreateSnapshot(); if (p != null) ctx.Send(p); });
        Mod.Node.On<PrismaDisruptionState>(Side.Guest, (c, m) => NetworkPrismaDisruptionManager.ApplySnapshot(Mod.Node.Clone(m)));

        // stabilising field spawners: host broadcasts changed spawners, the snapshot carries all
        Mod.Snapshots.Register(Lycoreia.Snapshot.SnapStep.StabilizingSpawner, ctx => { var p = NetworkStabilizingFieldSpawnerManager.CreateSnapshot(); if (p != null) ctx.Send(p); });
        Mod.Node.On<StabilizingSpawnerState>(Side.Guest, (c, m) => NetworkStabilizingFieldSpawnerManager.ApplySnapshot(Mod.Node.Clone(m)));

        // sanctuary: any peer's change goes to the host, which applies and relays it
        Mod.Snapshots.Register(Lycoreia.Snapshot.SnapStep.Sanctuary, ctx => ctx.Send(NetworkSanctuaryManager.CreateSnapshot()));
        Mod.Node.On<InitialSanctuary>(Side.Guest, (c, m) => NetworkSanctuaryManager.ApplySnapshot(Mod.Node.Clone(m)));
        Mod.Node.On<SanctuaryRoomState>(Side.Both, (c, m) =>
        {
            if (c.OnHost && !Lycoreia.Domain.HostAuthority.Require(c)) return Verdict.Reject;
            if (m.State == null || string.IsNullOrWhiteSpace(m.State.Id)) return Verdict.Reject;
            NetworkSanctuaryManager.ApplyRoomState(Mod.Node.Clone(m).State);
            return c.OnHost ? Verdict.Relay : Verdict.Accept;
        });
        Mod.Node.On<SanctuaryTheme>(Side.Both, (c, m) =>
        {
            if (c.OnHost && !Lycoreia.Domain.HostAuthority.Require(c)) return Verdict.Reject;
            if (string.IsNullOrWhiteSpace(m.ThemeReferenceId)) return Verdict.Reject;
            NetworkSanctuaryManager.ApplyThemeOwnership(m.ThemeReferenceId, m.IsOwned);
            return c.OnHost ? Verdict.Relay : Verdict.Accept;
        });
    }

    internal static void RegisterProgression()
    {
        Mod.Snapshots.Register(Lycoreia.Snapshot.SnapStep.Progression, ctx => ctx.Send(NetworkProgressionUnlockManager.CreateSnapshot()));
        Mod.Node.On<ProgressionUnlocks>(Side.Both, (c, m) =>
        {
            if (c.OnHost && !Lycoreia.Domain.HostAuthority.Require(c)) return Verdict.Reject;
            if (m.BlueprintIds.Any(id => id <= 0) || m.Appearances.Any(a => a == null || a.SlimeId <= 0 || a.SaveSet <= 0) || m.OwnedPalettes.Any(string.IsNullOrWhiteSpace)) return Verdict.Reject;   // malformed fields
            NetworkProgressionUnlockManager.Apply(m);
            return c.OnHost ? Verdict.Relay : Verdict.Accept;                                                   // additions are relayed by the host
        });
    }

    internal static void Pump()
    {
        NetworkProgressionUnlockManager.Pump();
        NetworkPrismaDisruptionManager.Pump(); NetworkStabilizingFieldSpawnerManager.Pump(); NetworkSanctuaryManager.Pump();
    }
}
