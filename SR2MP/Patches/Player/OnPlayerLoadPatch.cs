using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Player.CharacterController;
using Starlight.Utils;
using SR2MP.Components.Player;

namespace SR2MP.Patches.Player;

[HarmonyPatch(typeof(SRCharacterController), nameof(SRCharacterController.Awake))]
internal static class OnPlayerLoadPatch
{
    public static void Postfix(SRCharacterController __instance)
    {
        if (Main.Server.IsRunning)
        {
            BindLocalPlayer(__instance, Main.Server.PlayerId);
        }
        else if (Main.Client.IsConnected)
        {
            BindLocalPlayer(__instance, Main.Client.PlayerId);
        }
        else
        {
            Main.Client.OnConnected += id =>
            {
                if (!__instance)
                    return;

                BindLocalPlayer(__instance, id);
            };

            Main.Server.OnServerStarted += () =>
            {
                if (!__instance)
                    return;

                BindLocalPlayer(__instance, Main.Server.PlayerId);
            };
        }
    }

    private static void BindLocalPlayer(SRCharacterController controller, string playerId)
    {
        if (!controller || string.IsNullOrWhiteSpace(playerId))
            return;

        var networkPlayer = controller.GetComponent<NetworkPlayer>();
        if (!networkPlayer)
            networkPlayer = controller.gameObject.AddComponent<NetworkPlayer>();

        networkPlayer.BindLocalIdentity(playerId);
        PlayerManager.AddPlayer(playerId).Username = Main.Username;
    }
}
