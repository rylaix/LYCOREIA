using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using SR2MP.Packets.Actor;
using SR2MP.Shared.Utils;

namespace SR2MP.Shared.Managers;

internal readonly struct ArbiterCandidate
{
    internal readonly string PlayerId;
    internal readonly float Distance;
    internal readonly bool IsHost;

    internal ArbiterCandidate(string playerId, float distance, bool isHost)
    {
        PlayerId = playerId;
        Distance = distance;
        IsHost = isHost;
    }
}

/// <summary>
/// Host-only ownership decisions with hysteresis (OWNERSHIP-EPOCH-PLAN steps 5-7). Clients and the host's
/// own proximity/wake/slime/resource paths only register interest; this class decides and grants through
/// RequestOwnershipTransfer/ApplyOwnership, which bumps the epoch and broadcasts. Capture prepare/commit
/// (vacuum/hold intent) is a separate fast path and is only observed here as "engaged".
/// </summary>
internal static class OwnershipArbiter
{
    internal const float HoldSeconds = 1.5f;
    /// <summary>A challenger must be at most this fraction of the owner's distance (>= 25 % closer).</summary>
    internal const float ChallengerRatio = 0.75f;
    internal const float InterestTtlSeconds = 3f;
    internal const float TickSeconds = 0.5f;
    internal const float TieEpsilon = 0.01f;
    internal const int MaxInterestActors = 4096;
    internal const int MaxGrantRecords = 8192;

    private struct InterestEntry
    {
        internal float ReportedDistance;
        internal float LastSeen;
    }

    // actor -> player -> interest. Bounded by MaxInterestActors (and one entry per connected player).
    private static readonly Dictionary<long, Dictionary<string, InterestEntry>> Interest = new();
    private static readonly Dictionary<long, float> GrantedAt = new();

    // ---------------------------------------------------------------- pure rules

    /// <summary>
    /// Returns the player who should own the actor, or null to keep things as they are. A return equal
    /// to <paramref name="owner"/> means "released by the owner and the owner is also the best candidate".
    /// </summary>
    internal static string? Decide(
        string? owner, bool ownerConnected, bool released, bool engaged,
        float secondsSinceGrant, float ownerDistance, IReadOnlyList<ArbiterCandidate> candidates)
    {
        var ownerFree = string.IsNullOrEmpty(owner) || !ownerConnected || released;
        if (!ownerFree)
        {
            if (engaged || secondsSinceGrant < HoldSeconds)
                return null;
        }

        var found = false;
        ArbiterCandidate best = default;
        foreach (var candidate in candidates)
        {
            if (!ownerFree && string.Equals(candidate.PlayerId, owner, StringComparison.Ordinal))
                continue;

            if (!found || IsBetter(candidate, best))
            {
                best = candidate;
                found = true;
            }
        }

        if (!found)
            return null;

        if (ownerFree)
            return best.PlayerId;

        return best.Distance <= ownerDistance * ChallengerRatio ? best.PlayerId : null;
    }

    private static bool IsBetter(ArbiterCandidate candidate, ArbiterCandidate best)
    {
        if (candidate.Distance < best.Distance - TieEpsilon)
            return true;
        // Ties go to the host.
        return Math.Abs(candidate.Distance - best.Distance) <= TieEpsilon && candidate.IsHost && !best.IsHost;
    }

    // ---------------------------------------------------------------- interest bookkeeping (host)

    internal static void RegisterInterest(string playerId, long actorId, float reportedDistance, float now)
    {
        if (!Main.Server.IsRunning || actorId == 0 || string.IsNullOrEmpty(playerId))
            return;

        if (!Interest.TryGetValue(actorId, out var perPlayer))
        {
            if (Interest.Count >= MaxInterestActors)
            {
                PurgeExpired(now);
                if (Interest.Count >= MaxInterestActors)
                    return;
            }

            perPlayer = new Dictionary<string, InterestEntry>(2);
            Interest[actorId] = perPlayer;
        }

        perPlayer[playerId] = new InterestEntry { ReportedDistance = reportedDistance, LastSeen = now };
    }

    private static void PurgeExpired(float now)
    {
        List<long>? emptied = null;
        foreach (var (actorId, perPlayer) in Interest)
        {
            List<string>? stale = null;
            foreach (var (playerId, entry) in perPlayer)
            {
                if (now - entry.LastSeen > InterestTtlSeconds || !IsPlayerConnected(playerId))
                    (stale ??= new List<string>()).Add(playerId);
            }

            if (stale != null)
                foreach (var playerId in stale)
                    perPlayer.Remove(playerId);

            if (perPlayer.Count == 0)
                (emptied ??= new List<long>()).Add(actorId);
        }

        if (emptied != null)
            foreach (var actorId in emptied)
                Interest.Remove(actorId);
    }

    internal static void NoteGrant(long actorId, float now)
    {
        if (GrantedAt.Count >= MaxGrantRecords)
        {
            // Old records only matter inside the hold window; anything older behaves as "no record".
            var stale = new List<long>();
            foreach (var (id, at) in GrantedAt)
                if (now - at > HoldSeconds)
                    stale.Add(id);
            foreach (var id in stale)
                GrantedAt.Remove(id);
            if (GrantedAt.Count >= MaxGrantRecords)
                GrantedAt.Clear();
        }

        GrantedAt[actorId] = now;
    }

    internal static void Reset()
    {
        Interest.Clear();
        GrantedAt.Clear();
    }

    private static bool IsPlayerConnected(string playerId)
        => playerId == LocalID || PlayerManager.CheckPlayerExists(playerId);

    // ---------------------------------------------------------------- evaluation (host)

    internal static void Tick(float now)
    {
        if (!Main.Server.IsRunning)
            return;

        PurgeExpired(now);
        if (Interest.Count == 0)
            return;

        foreach (var actorId in Interest.Keys.ToArray())
            Evaluate(actorId, now);
    }

    internal static void EvaluateNow(long actorId)
    {
        if (Main.Server.IsRunning)
            Evaluate(actorId, UnityEngine.Time.unscaledTime);
    }

    private static void Evaluate(long actorId, float now)
    {
        try
        {
            if (!Interest.TryGetValue(actorId, out var perPlayer) || perPlayer.Count == 0)
                return;

            if (!ActorManager.Actors.TryGetValue(actorId, out var model) || model == null)
            {
                Interest.Remove(actorId);
                return;
            }

            var owner = ActorManager.OwnershipByActorId.GetValueOrDefault(actorId, string.Empty);
            var ownerConnected = !string.IsNullOrEmpty(owner) && IsPlayerConnected(owner);
            var released = ownerConnected && NetworkActorManager.WasReleasedByOwner(actorId, owner);
            var engaged = ownerConnected && IsEngaged(actorId, owner);
            var sinceGrant = GrantedAt.TryGetValue(actorId, out var grantedAt) ? now - grantedAt : float.MaxValue;

            var actorPosition = GetActorPosition(model);
            var ownerDistance = ownerConnected
                ? DistanceTo(owner, actorPosition, float.PositiveInfinity)
                : float.PositiveInfinity;
            // A connected owner within vacuum range of its actor may be holding, pulling or have just thrown it
            // (no per-owner hold/throw signal reaches the host), and an unknown owner position is not proof of
            // distance: never contest either. Released actors are still reassigned.
            if (ownerConnected && !released &&
                (float.IsPositiveInfinity(ownerDistance) || ownerDistance < OwnerEngagedRadius))
                engaged = true;

            var candidates = new List<ArbiterCandidate>(perPlayer.Count);
            foreach (var (playerId, entry) in perPlayer)
            {
                if (now - entry.LastSeen > InterestTtlSeconds || !IsPlayerConnected(playerId))
                    continue;

                // Prefer the host's own view of positions over the distance the guest reported.
                var distance = DistanceTo(playerId, actorPosition, entry.ReportedDistance);
                candidates.Add(new ArbiterCandidate(playerId, distance, playerId == LocalID));
            }

            var winner = Decide(owner, ownerConnected, released, engaged, sinceGrant, ownerDistance, candidates);
            if (winner == null)
                return;

            if (string.Equals(winner, owner, StringComparison.Ordinal) && !released)
                return;

            Interest.Remove(actorId);

#if SR2MP_DEEPLOG || SR2MP_TEST
            ActorDiag.Log("arbiter-grant", actorId,
                $"owner={owner}; winner={winner}; released={released}; ownerConnected={ownerConnected}; " +
                $"sinceGrant={sinceGrant:F2}; ownerDistance={ownerDistance:F1}; candidates={candidates.Count}",
                ownerHint: owner, perActorMilliseconds: 250);
#endif

            // RequestOwnershipTransfer applies on the host (epoch bump + NoteGrant) and broadcasts.
            NetworkActorManager.RequestOwnershipTransfer(new ActorTransferPacket
            {
                ActorId = model.actorId,
                OwnerId = winner,
                PreviousOwnerId = owner
            });
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"OwnershipArbiter.Evaluate({actorId}) failed: {ex.Message}");
        }
    }

    private const float OwnerEngagedRadius = 8f;

    private static bool IsEngaged(long actorId, string owner)
    {
        // Capture reservation covers a guest owner's vacuum/hold intent. Thrown-by-owner has no hook yet.
        if (NetworkActorCaptureManager.HasServerCaptureReservation(actorId))
            return true;

        if (owner != LocalID)
            return false;

        try
        {
            var player = SceneContext.Instance?.Player;
            if (!player || !ActorManager.Actors.TryGetValue(actorId, out var model) || model == null)
                return false;

            var gameObject = model.GetGameObject();
            if (!gameObject)
                return false;

            var vacItem = player.GetComponent<PlayerItemController>()?._vacuumItem;
            return vacItem && vacItem._held == gameObject;
        }
        catch
        {
            return false;
        }
    }

    private static Vector3? GetActorPosition(Il2CppMonomiPark.SlimeRancher.DataModel.IdentifiableModel model)
    {
        var gameObject = model.GetGameObject();
        if (gameObject)
            return gameObject.transform.position;
        return model.lastPosition;
    }

    private static float DistanceTo(string playerId, Vector3? actorPosition, float fallback)
    {
        if (actorPosition == null)
            return fallback;

        Vector3? playerPosition = null;
        if (playerId == LocalID)
        {
            var player = SceneContext.Instance?.player;
            if (player)
                playerPosition = player.transform.position;
        }
        else if (PlayerObjects.TryGetValue(playerId, out var playerObject) && playerObject)
        {
            playerPosition = playerObject.transform.position;
        }

        return playerPosition == null ? fallback : Vector3.Distance(playerPosition.Value, actorPosition.Value);
    }

#if SR2MP_TEST
    internal static bool VerifyRules()
    {
        const string host = "HOST", a = "A", b = "B";
        var none = Array.Empty<ArbiterCandidate>();
        ArbiterCandidate C(string id, float d) => new(id, d, id == host);

        // Unowned / released / disconnected: nearest wins, nobody -> null.
        if (Decide("", false, false, false, 99f, float.PositiveInfinity, none) != null) return false;
        if (Decide("", false, false, false, 99f, float.PositiveInfinity, new[] { C(a, 30f), C(b, 10f) }) != b) return false;
        if (Decide(a, false, false, false, 0f, float.PositiveInfinity, new[] { C(b, 10f) }) != b) return false;
        if (Decide(a, true, true, false, 0f, 20f, new[] { C(a, 20f), C(b, 50f) }) != a) return false;
        if (Decide(a, true, true, true, 0f, 20f, new[] { C(b, 50f) }) != b) return false;

        // Host preferred on ties, regardless of order.
        if (Decide("", false, false, false, 99f, float.PositiveInfinity, new[] { C(a, 10f), C(host, 10f) }) != host) return false;
        if (Decide("", false, false, false, 99f, float.PositiveInfinity, new[] { C(host, 10f), C(a, 10f) }) != host) return false;
        if (Decide("", false, false, false, 99f, float.PositiveInfinity, new[] { C(a, 9f), C(host, 10f) }) != a) return false;

        // Hold time: even a much closer challenger waits.
        if (Decide(a, true, false, false, 1.49f, 100f, new[] { C(b, 1f) }) != null) return false;
        if (Decide(a, true, false, false, 1.5f, 100f, new[] { C(b, 1f) }) != b) return false;

        // 25 % rule: needs distance <= 0.75 * owner distance.
        if (Decide(a, true, false, false, 5f, 100f, new[] { C(b, 80f) }) != null) return false;
        if (Decide(a, true, false, false, 5f, 100f, new[] { C(b, 75f) }) != b) return false;
        if (Decide(a, true, false, false, 5f, 100f, new[] { C(a, 1f) }) != null) return false;

        // Engaged owner never loses to a challenger.
        if (Decide(a, true, false, true, 60f, 100f, new[] { C(b, 1f) }) != null) return false;

        // Host wins a qualifying challenge tie against a guest challenger.
        if (Decide(a, true, false, false, 5f, 100f, new[] { C(b, 50f), C(host, 50f) }) != host) return false;

        return true;
    }
#endif
}
