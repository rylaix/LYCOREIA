using System.Collections;
using System.Net;
using Il2CppMonomiPark.SlimeRancher.Player.Component;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Upgrade;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Utils;

namespace SR2MP.Handlers.Refinery;

[PacketHandler((byte)PacketType.InitialComponents, HandlerType.Client)]
internal sealed class InitialComponentsHandler : BasePacketHandler<InitialComponentsPacket>
{
    private static readonly OrderedSnapshotDeltaQueue<InitialComponentsPacket, UpgradeComponentPacket> pendingState = new();
    private static bool hooksRegistered;

    protected override bool Handle(InitialComponentsPacket packet, IPEndPoint? _)
    {
        EnsureSessionHooks();

        // Applying the snapshot is deferred for one frame so the player model is ready.
        // Buffer following ordered component deltas; otherwise they can be applied first
        // and then erased when this snapshot replaces the dictionary.
        pendingState.SetSnapshot(packet);
        StartCoroutine(InitializeComponents());
        return false;
    }

    internal static bool TryQueueDelta(UpgradeComponentPacket packet)
    {
        EnsureSessionHooks();

        if (!pendingState.HasPending)
            return false;

        pendingState.EnqueueDelta(packet);
        return true;
    }

    private static void EnsureSessionHooks()
    {
        if (hooksRegistered)
            return;

        hooksRegistered = true;
        Main.Client.OnConnected += _ => pendingState.Clear();
        Main.Client.OnDisconnected += pendingState.Clear;
        Main.Server.OnServerStarted += pendingState.Clear;
    }

    private static IEnumerator InitializeComponents()
    {
        yield return null;

        while (pendingState.HasPending)
        {
            if (pendingState.TryDrain(ApplySnapshot, ApplyDelta))
                yield break;

            // The model can be unavailable during scene bootstrap. Keep the snapshot
            // and any following deltas queued until its component catalog is ready.
            yield return null;
        }
    }

    private static bool ApplySnapshot(InitialComponentsPacket packet)
    {
        var model = GameState.PlayerModel?.UpgradeComponentsModel;
        if (model == null || model._upgradeComponentList == null || model._upgradeComponentList.items == null)
            return false;

        var ownedComponents = new CppCollections.Dictionary<UpgradeComponent, int>();
        foreach (var (componentId, count) in packet.Items)
        {
            var component = model._upgradeComponentList.items._items
                .FirstOrDefault(item => item != null && item._referenceId == componentId);
            if (component == null)
                continue;

            ownedComponents.Add(component, count);
        }

        model._ownedComponents = ownedComponents;
        return true;
    }

    private static bool ApplyDelta(UpgradeComponentPacket packet)
    {
        UpgradeComponentUpdateHandler.ApplyAuthoritativeUpdate(packet);
        return true;
    }
}
