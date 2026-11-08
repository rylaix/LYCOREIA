// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections;
using HarmonyLib;
using Lycoreia.Owned.Shared.Managers;

namespace Lycoreia.Owned.Patches.GordoSlime;

[HarmonyPatch(typeof(GordoRewardsBase), nameof(GordoRewardsBase.Start))]
internal static class OnGordoSlimeRewardRecovery
{
    private static readonly HashSet<string> RecoveriesInProgress = new(StringComparer.Ordinal);

    public static void Postfix(GordoRewardsBase __instance)
    {
        if (!__instance._eat)
            return;

        Schedule(__instance, __instance._eat.Id);
    }

    internal static void Schedule(GordoRewardsBase rewards, string gordoId)
    {
        if (!ModFacade.Server.IsRunning || !rewards || string.IsNullOrWhiteSpace(gordoId) ||
            !NetworkGordoSlimeManager.HasPendingHostReward(gordoId) ||
            !RecoveriesInProgress.Add(gordoId))
            return;

        try
        {
            StartCoroutine(RetryPendingReward(rewards, gordoId));
        }
        catch (Exception exception)
        {
            RecoveriesInProgress.Remove(gordoId);
            OwnedLog.LogWarning(
                $"Could not schedule Gordo reward recovery for {gordoId}; payout remains pending: {exception.Message}");
        }
    }

    private static IEnumerator RetryPendingReward(GordoRewardsBase rewards, string gordoId)
    {
        var failedAttempts = 0;
        try
        {
            while (true)
            {
                if (!ModFacade.Server.IsRunning || !rewards || !rewards._eat)
                    yield break;

                if (NetworkGordoSlimeManager.HasGivenRewards(gordoId))
                {
                    rewards._eat.gameObject.SetActive(false);
                    yield break;
                }

                if (NetworkGordoSlimeManager.IsRewardGenerationUncertain(gordoId) ||
                    !NetworkGordoSlimeManager.HasPendingHostReward(gordoId))
                    yield break;

                // A failure reconciliation coroutine owns the in-progress state
                // until it has observed the native call's side effects.
                if (NetworkGordoSlimeManager.IsRewardGenerationInProgress(gordoId))
                {
                    yield return new WaitForSecondsRealtime(0.25f);
                    continue;
                }

                try
                {
                    rewards.GiveRewards();
                }
                catch (Exception exception)
                {
                    OwnedLog.LogWarning(
                        $"Gordo reward recovery attempt for {gordoId} failed: {exception.Message}");
                }

                if (NetworkGordoSlimeManager.HasGivenRewards(gordoId))
                {
                    if (rewards && rewards._eat)
                        rewards._eat.gameObject.SetActive(false);
                    yield break;
                }

                if (NetworkGordoSlimeManager.IsRewardGenerationUncertain(gordoId))
                    yield break;

                while (NetworkGordoSlimeManager.IsRewardGenerationInProgress(gordoId))
                {
                    if (!ModFacade.Server.IsRunning || !rewards || !rewards._eat)
                        yield break;

                    yield return new WaitForSecondsRealtime(0.25f);
                }

                if (NetworkGordoSlimeManager.HasGivenRewards(gordoId))
                {
                    if (rewards && rewards._eat)
                        rewards._eat.gameObject.SetActive(false);
                    yield break;
                }

                if (NetworkGordoSlimeManager.IsRewardGenerationUncertain(gordoId) ||
                    !NetworkGordoSlimeManager.HasPendingHostReward(gordoId))
                    yield break;

                failedAttempts++;
                if (failedAttempts == 1 || (failedAttempts & (failedAttempts - 1)) == 0)
                    OwnedLog.LogWarning(
                        $"Gordo reward for {gordoId} remains pending after {failedAttempts} safe retry attempt(s).");

                yield return new WaitForSecondsRealtime(
                    GordoRewardCapturePolicy.RecoveryDelaySeconds(failedAttempts));
            }
        }
        finally
        {
            RecoveriesInProgress.Remove(gordoId);
        }
    }
}
