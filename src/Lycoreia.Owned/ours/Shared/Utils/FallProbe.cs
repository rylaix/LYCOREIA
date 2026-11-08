// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppMonomiPark.SlimeRancher.Damage;
using HarmonyLib;

namespace Lycoreia.Owned.Shared.Utils;

/// <summary>
/// Diagnostics for "fell through the ground" (owner 2026-11-01: a guest fell through the ground 3x on the path from
/// the ranch house west to Rainbow Fields, x 380-470 / z 330, while the host was in another zone; never again
/// after the host's /resync). While connected it remembers where the local player last stood on something and
/// what that was; when the player falls far below it, or dies, it logs what killed them and what collider
/// should have been underneath (casting down from high above that spot), so the next report names the culprit.
/// Logging only, no gameplay change.
/// </summary>
internal static class FallProbe
{
    private const float SampleInterval = 0.25f;
    private const float FallReportDrop = 12f;

    private static float nextSampleAt;
    private static Vector3 lastGrounded;
    private static string lastGroundName = "<none>";
    private static Collider? lastGroundCollider;
    private static float lastGroundedAt = -1f;
    private static float nextFallReportAt;
    private static readonly Queue<Vector3> Path = new();

    internal static void Pump()
    {
        var now = UnityEngine.Time.unscaledTime;
        if (now < nextSampleAt)
            return;
        nextSampleAt = now + SampleInterval;
        try
        {
            if (!ModFacade.Server.IsRunning && !ModFacade.Client.IsConnected)
                return;
            var player = SceneContext.Instance ? SceneContext.Instance.player : null;
            if (!player)
                return;
            var position = player.transform.position;
            Path.Enqueue(position);
            while (Path.Count > 16)
                Path.Dequeue();

            if (TryGround(player.transform, position + Vector3.up, 3f, out var hit))
            {
                lastGrounded = position;
                lastGroundName = Describe(hit.collider);
                lastGroundCollider = hit.collider;
                lastGroundedAt = now;
                return;
            }

            if (lastGroundedAt > 0f && lastGrounded.y - position.y > FallReportDrop && now >= nextFallReportAt)
            {
                nextFallReportAt = now + 10f;
                Report("fall-detected", player.transform, position, null);
            }
        }
        catch (Exception ex)
        {
            DiagLog.LogDiagnostic("player", "fall-probe-error", ex.Message, "fall-probe-error", 30_000);
        }
    }

    internal static void OnDeath(string source)
    {
        try
        {
            var player = SceneContext.Instance ? SceneContext.Instance.player : null;
            if (!player || (!ModFacade.Server.IsRunning && !ModFacade.Client.IsConnected))
                return;
            Report("player-death", player.transform, player.transform.position, source);
        }
        catch (Exception ex)
        {
            OwnedLog.LogWarning($"Fall probe death report failed: {ex.Message}");
        }
    }

    private static void Report(string eventName, Transform player, Vector3 position, string? source)
    {
        var now = UnityEngine.Time.unscaledTime;
        // What is under the spot the player fell from / died at, cast from high above (ignores triggers).
        var aboveFall = DescribeColumn(player, lastGrounded);
        var aboveHere = DescribeColumn(player, position);
        var path = string.Join(" ", Path.Select(p => p.ToString("F0")));
        var details = $"source={source ?? "-"}; pos={position:F1}; lastGround={lastGrounded:F1} on '{lastGroundName}' " +
                      $"{(lastGroundedAt > 0f ? now - lastGroundedAt : -1f):F1}s ago; columnAtLastGround=[{aboveFall}]; " +
                      $"columnHere=[{aboveHere}]; groundNow=[{DescribeGroundState()}]; path={path}";
        DiagLog.LogDiagnostic("player", eventName, details);
        if (source != null)
            OwnedLog.LogMessage($"[FallProbe] {eventName}: {details}");
    }

    /// <summary>What became of the collider the player last stood on: destroyed, disabled, or switched off by an
    /// inactive parent (named with its components, which is what toggled it).</summary>
    private static string DescribeGroundState()
    {
        try
        {
            var collider = lastGroundCollider;
            if (collider == null)
                return "none-recorded";
            if (!collider)
                return "destroyed";
            var go = collider.gameObject;
            var text = $"enabled={collider.enabled}; activeSelf={go.activeSelf}; activeInHierarchy={go.activeInHierarchy}";
            var t = go.transform;
            while (t)
            {
                if (!t.gameObject.activeSelf)
                {
                    var components = string.Join(",", t.GetComponents<Component>()
                        .Where(c => c).Select(c => c.GetIl2CppType().Name).Take(8));
                    text += $"; inactiveAncestor={t.name} [{components}]";
                    break;
                }
                t = t.parent;
            }
            return text;
        }
        catch (Exception ex)
        {
            return "error " + ex.Message;
        }
    }

    private static bool TryGround(Transform player, Vector3 from, float distance, out RaycastHit result)
    {
        result = default;
        var hits = Physics.RaycastAll(from, Vector3.down, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        var best = float.MaxValue;
        foreach (var hit in hits)
        {
            if (!hit.collider || hit.collider.transform.IsChildOf(player) || hit.distance >= best)
                continue;
            best = hit.distance;
            result = hit;
        }
        return best < float.MaxValue;
    }

    private static string DescribeColumn(Transform player, Vector3 at)
    {
        var hits = Physics.RaycastAll(at + Vector3.up * 60f, Vector3.down, 160f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        var parts = hits
            .Where(hit => hit.collider && !hit.collider.transform.IsChildOf(player))
            .OrderBy(hit => hit.distance)
            .Take(4)
            .Select(hit => $"{Describe(hit.collider)}@y{hit.point.y:F1}");
        var text = string.Join(", ", parts);
        return text.Length == 0 ? "nothing" : text;
    }

    private static string Describe(Collider collider)
    {
        var go = collider.gameObject;
        return $"{go.name}|{go.scene.name}|layer={go.layer}|{collider.GetIl2CppType().Name}";
    }
}

[HarmonyPatch(typeof(PlayerDeathHandler), nameof(PlayerDeathHandler.OnDeath))]
internal static class PlayerDeathProbePatch
{
    public static void Prefix(DamageSourceDefinition source, GameObject sourceGameObject)
    {
        var sourceName = (source ? source.name : "<null>") + "/" + (sourceGameObject ? sourceGameObject.name : "<null>");
        FallProbe.OnDeath(sourceName);
    }
}
