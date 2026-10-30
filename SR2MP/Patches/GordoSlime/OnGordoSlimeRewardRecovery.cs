using System.Collections;
using HarmonyLib;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.GordoSlime;

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
        if (!Main.Server.IsRunning || !rewards || string.IsNullOrWhiteSpace(gordoId) ||
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
            SrLogger.LogWarning(
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
                if (!Main.Server.IsRunning || !rewards || !rewards._eat)
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
                    SrLogger.LogWarning(
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
                    if (!Main.Server.IsRunning || !rewards || !rewards._eat)
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
                    SrLogger.LogWarning(
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
