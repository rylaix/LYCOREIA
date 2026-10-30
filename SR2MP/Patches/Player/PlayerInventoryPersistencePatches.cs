using HarmonyLib;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Player;

[HarmonyPatch(typeof(SceneContext), nameof(SceneContext.Start))]
internal static class PlayerInventoryPersistenceLifecyclePatch
{
    private static bool subscribed;

    public static void Postfix()
    {
        if (subscribed)
            return;

        subscribed = true;
        PlayerInventoryManager.Initialize();
        Main.Client.OnConnected += _ =>
        {
            PlayerInventoryManager.OnClientConnected();
            StartCoroutine(PlayerInventoryManager.RestoreWhenPlayerReady());
        };
        Main.Client.OnDisconnected += PlayerInventoryManager.OnClientDisconnected;

        if (Main.Client.IsJoined)
        {
            PlayerInventoryManager.OnClientConnected();
            StartCoroutine(PlayerInventoryManager.RestoreWhenPlayerReady());
        }
    }
}

[HarmonyPatch(typeof(FileStorageProvider), nameof(FileStorageProvider.StoreGameData))]
internal static class PlayerInventoryPersistenceSavePatch
{
    public static void Prefix()
        => PlayerInventoryManager.SaveBeforeVanillaGameWrite();
}
