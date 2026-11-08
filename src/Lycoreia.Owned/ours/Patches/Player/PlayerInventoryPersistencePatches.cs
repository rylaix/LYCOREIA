// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using Lycoreia.Owned.Shared.Managers;

namespace Lycoreia.Owned.Patches.Player;

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
        Mod.Session.ClientJoined += () =>                          // session events
        {
            PlayerInventoryManager.OnClientConnected();
            StartCoroutine(PlayerInventoryManager.RestoreWhenPlayerReady());
        };
        Mod.Session.ClientDisconnected += _ => PlayerInventoryManager.OnClientDisconnected();

        if (ModFacade.Client.IsJoined)
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
