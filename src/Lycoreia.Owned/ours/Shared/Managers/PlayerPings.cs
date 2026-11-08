// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppTMPro;
using Lycoreia.Owned.Components.UI;
using UnityEngine.InputSystem;

namespace Lycoreia.Owned.Shared.Managers;

/// <summary>
/// World pings (middle mouse): a real object in the world where the player points, a floating gem that points
/// down at the spot with a ground ring and a thin beam, in the player's colour, plus a short chime that plays
/// from the spot itself (3D sound). Each player has at most one ping: a new one replaces the old. A local
/// cooldown and a host-side rate limit keep spam out. Plain static class (no Il2Cpp injection), animated from
/// the multiplayer UI's Update.
/// </summary>
internal static class PlayerPings
{
    private const float LocalCooldown = 1.0f;
    private const float HostMinInterval = 0.75f;   // a bit under LocalCooldown for network jitter
    private const float MaxPingDistance = 300f;    // raycast reach from the camera
    private const float HostReachSlack = 400f;     // host: ping must be this close to the sender
    private const float Lifetime = 8f;
    private const float PopIn = 0.25f;
    private const float FadeOut = 0.6f;
    private const float GemHeight = 1.5f;          // gem centre above the spot
    private const float NearSizeDistance = 20f;    // true size up to here, then it grows to stay visible
    private const float MaxGrow = 6f;

    private sealed class Ping
    {
        public GameObject Root = null!;
        public Transform Gem = null!;
        public Transform Pulse = null!;
        public Transform Beam = null!;
        public Transform Label = null!;
        public Vector3 Position;
        public float BornAt;
    }

    private static readonly Dictionary<string, Ping> Pings = new();
    private static readonly Dictionary<string, float> LastAcceptedAt = new();
    private static float nextPingAt;
    private static Mesh? gemMeshA, gemMeshB, ringMesh;
    private static Shader? unlitShader;
    private static AudioClip? chime;
    private static UnityEngine.Audio.AudioMixerGroup? sfxGroup;
    private static bool sfxGroupSearched, pingFontSearched;
    private static TMP_FontAsset? pingFont;
    private static bool failed;

    internal static void Tick()
    {
        try
        {
            if (!ModFacade.IsNetworkActive)
            {
                ClearAll();
#if LYCOREIA_TEST
                // Test builds: middle mouse pings offline too (to look at the marker alone).
                if (MiddleClicked())
                    TryPing();
                AnimateAll();
#endif
                return;
            }

            if (MiddleClicked())
                TryPing();
            AnimateAll();
        }
        catch (Exception ex)
        {
            if (!failed)
                OwnedLog.LogWarning($"Pings failed: {ex}");
            failed = true;
        }
    }

    private static bool MiddleClicked()
        => !failed && IsGameplayInputFree() && Mouse.current is { } mouse && mouse.middleButton.wasPressedThisFrame;

    private static bool IsGameplayInputFree()
        => !ChatHud.IsOpen && !LycoreiaMenu.IsOpen && !Lycoreia.Game.Hooks.GameMenus.NativePaused &&
           !SystemContext.Instance.SceneLoader.IsSceneLoadInProgress;

    // ---------------------------------------------------------------- sending

    internal static void TryPing()
    {
        var now = UnityEngine.Time.time;
        if (now < nextPingAt)
            return;

        var camera = Camera.main;
        var player = SceneContext.Instance ? SceneContext.Instance.Player : null;
        if (!camera || !player)
            return;

        if (!RaycastFromCamera(camera!.transform, player!.transform, out var position, out var normal))
            return;
        nextPingAt = now + LocalCooldown;

        var sceneGroup = LocalSceneGroup();
        Show(LocalID, position, normal, sceneGroup);
        if (ModFacade.IsNetworkActive)
            ModFacade.SendToAllOrServer(new Lycoreia.Net.Messages.PlayerPing { PlayerId = LocalID, Position = position.ToWire(), Normal = normal.ToWire(), SceneGroup = sceneGroup });
        DiagLog.LogDiagnostic("friend-fun", "ping-sent", $"pos={position}; scene={sceneGroup}");
    }

    private static bool RaycastFromCamera(Transform camera, Transform player, out Vector3 position, out Vector3 normal)
    {
        position = default;
        normal = Vector3.up;
        var hits = Physics.RaycastAll(camera.position, camera.forward, MaxPingDistance, Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
        var best = float.MaxValue;
        foreach (var hit in hits)
        {
            // Skip our own body / vac.
            if (hit.collider && hit.collider.transform.IsChildOf(player))
                continue;
            if (hit.distance < best)
            {
                best = hit.distance;
                position = hit.point;
                normal = hit.normal;
            }
        }
        return best < float.MaxValue;
    }

    private static int LocalSceneGroup()
    {
        try
        {
            return Lycoreia.Game.SceneGroups.IdOf(SystemContext.Instance.SceneLoader._currentSceneGroup);   // scene group ids
        }
        catch
        {
            return -1;
        }
    }

    // ---------------------------------------------------------------- receiving

    /// <summary>Sanity checks; on the host also the per-player rate limit and distance to the sender.</summary>
    internal static bool AcceptRemote(Lycoreia.Net.Messages.PlayerPing packet, bool isHost)
    {
        if (!IsFinite(packet.Position.ToUnity()) || !IsFinite(packet.Normal.ToUnity()))
            return false;
        if (!isHost)
            return true;

        var now = UnityEngine.Time.time;
        if (LastAcceptedAt.TryGetValue(packet.PlayerId, out var last) && now - last < HostMinInterval)
        {
            DiagLog.LogDiagnostic("friend-fun", "ping-rate-limited", $"from={packet.PlayerId}");
            return false;
        }
        if (PlayerObjects.TryGetValue(packet.PlayerId, out var sender) && sender &&
            Vector3.Distance(sender.transform.position, packet.Position.ToUnity()) > HostReachSlack)
        {
            DiagLog.LogDiagnostic("friend-fun", "ping-rejected", $"from={packet.PlayerId}; pos={packet.Position}");
            return false;
        }
        LastAcceptedAt[packet.PlayerId] = now;
        return true;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    /// <summary>Shows (or moves) this player's ping. Only one ping per player exists at any time.</summary>
    internal static void Show(string playerId, Vector3 position, Vector3 normal, int sceneGroup)
    {
        Remove(playerId);
        if (sceneGroup != LocalSceneGroup())
            return;

        var (name, color) = NameAndColor(playerId);
        var ping = Build(position, normal.sqrMagnitude > 0.5f ? normal.normalized : Vector3.up, name, color);
        Pings[playerId] = ping;
        PlayChime(ping.Root);
        DiagLog.LogDiagnostic("friend-fun", "ping-shown", $"from={playerId}; pos={position}");
        // the remote avatar points from RemoteAvatars.ShowPing
#if LYCOREIA_TEST
        OwnedLog.LogMessage($"[test-ping] shown from={playerId} pos={position} normal={normal} shader={(unlitShader ? unlitShader!.name : "none")} mixer={(sfxGroup ? sfxGroup!.name : "none")}");
#endif
    }

    private static (string Name, Color Color) NameAndColor(string playerId)
    {
        string name;
        if (playerId == LocalID)
            name = ModFacade.Username;
        else
            name = Roster.GetPlayer(playerId)?.Name ?? string.Empty;

        var color = Color.white;
        var tag = name.IndexOf("<color=#", StringComparison.OrdinalIgnoreCase);
        if (tag < 0 || !ColorUtility.TryParseHtmlString(name.Substring(tag + 7, Math.Min(7, name.Length - tag - 7)), out color))
        {
            var remote = Roster.GetPlayer(playerId);
            color = remote != null ? Roster.ColorOf(remote) : new Color(0.95f, 0.45f, 1f);
        }
        return (name, color);
    }

    private static void Remove(string playerId)
    {
        if (Pings.Remove(playerId, out var old) && old.Root)
            Object.Destroy(old.Root);
    }

    private static void ClearAll()
    {
        if (Pings.Count == 0)
            return;
        foreach (var ping in Pings.Values)
            if (ping.Root)
                Object.Destroy(ping.Root);
        Pings.Clear();
        LastAcceptedAt.Clear();
    }

    // ---------------------------------------------------------------- animation

    private static void AnimateAll()
    {
        if (Pings.Count == 0)
            return;

        var now = UnityEngine.Time.time;
        var camera = Camera.main;
        List<string>? expired = null;
        foreach (var (playerId, ping) in Pings)
        {
            var age = now - ping.BornAt;
            if (!ping.Root || age > Lifetime)
            {
                (expired ??= new List<string>()).Add(playerId);
                continue;
            }

            // Pop in with a little overshoot, shrink away at the end.
            var pop = age < PopIn ? EaseOutBack(age / PopIn) : 1f;
            var fade = Mathf.Clamp01((Lifetime - age) / FadeOut);
            var life = pop * fade * fade * (3f - 2f * fade);

            // Real size up close; past NearSizeDistance it grows so it stays readable across a zone.
            var distance = camera ? Vector3.Distance(camera!.transform.position, ping.Position) : 0f;
            var grow = Mathf.Clamp(distance / NearSizeDistance, 1f, MaxGrow);
            ping.Root.transform.localScale = Vector3.one * grow;

            var bob = Mathf.Sin(age * 2.4f) * 0.12f;
            ping.Gem.localPosition = Vector3.up * (GemHeight + bob);
            ping.Gem.localRotation = Quaternion.Euler(0f, age * 90f, 0f);
            ping.Gem.localScale = Vector3.one * life;
            ping.Label.localPosition = Vector3.up * (GemHeight + 0.85f + bob);
            ping.Label.localScale = Vector3.one * life;
            if (camera) ping.Label.rotation = Quaternion.LookRotation(ping.Label.position - camera!.transform.position);
            var beamTop = GemHeight + bob - 0.45f;
            ping.Beam.localPosition = Vector3.up * (beamTop * 0.5f);
            ping.Beam.localScale = new Vector3(0.05f * life, beamTop * 0.5f * fade, 0.05f * life);

            // A ring that ripples out from the spot every 1.3 s.
            var cycle = (age % 1.3f) / 1.3f;
            ping.Pulse.localScale = Vector3.one * (Mathf.Lerp(0.35f, 1.6f, cycle) * life * (1f - cycle * cycle));
        }

        if (expired != null)
            foreach (var playerId in expired)
                Remove(playerId);
    }

    private static float EaseOutBack(float t)
    {
        const float c1 = 1.9f;
        const float c3 = c1 + 1f;
        var x = t - 1f;
        return 1f + c3 * x * x * x + c1 * x * x;
    }

    // ---------------------------------------------------------------- building

    private static Ping Build(Vector3 position, Vector3 normal, string name, Color color)
    {
        EnsureAssets();

        var root = new GameObject("LycoreiaPing");
        root.transform.position = position;
        // Stand upright on floors, tilt a little on slopes, stay upright on walls.
        var up = Vector3.Dot(normal, Vector3.up) > 0.35f ? Vector3.Slerp(Vector3.up, normal, 0.3f) : Vector3.up;
        root.transform.rotation = Quaternion.FromToRotation(Vector3.up, up);

        var bright = Material(color * 2.4f);
        var mid = Material(Color.Lerp(color, Color.white, 0.35f) * 1.4f);
        var soft = Material(color * 1.2f);

        var gem = new GameObject("Gem").transform;
        gem.SetParent(root.transform, false);
        AddRenderer(gem, "FacetsA", gemMeshA!, bright);
        AddRenderer(gem, "FacetsB", gemMeshB!, mid);

        var ring = new GameObject("Ring").transform;
        ring.SetParent(root.transform, false);
        ring.localPosition = Vector3.up * 0.03f;
        ring.localScale = Vector3.one * 0.55f;
        AddRenderer(ring, "RingMesh", ringMesh!, bright);

        var pulse = new GameObject("Pulse").transform;
        pulse.SetParent(root.transform, false);
        pulse.localPosition = Vector3.up * 0.02f;
        AddRenderer(pulse, "PulseMesh", ringMesh!, soft);

        var beamObject = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Object.Destroy(beamObject.GetComponent<Collider>());
        beamObject.name = "Beam";
        beamObject.transform.SetParent(root.transform, false);
        SetupRenderer(beamObject.GetComponent<MeshRenderer>(), soft);

        var labelObject = new GameObject("Label");
        labelObject.transform.SetParent(root.transform, false);
        var label = labelObject.AddComponent<TextMeshPro>();
        label.text = name;
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = 2.2f;
        label.outlineWidth = 0.2f;
        label.outlineColor = new Color32(0, 0, 0, 220);
        if (pingFont) label.font = pingFont;
        // the label faces the camera from Tick (the old look-at component is gone)

        return new Ping
        {
            Root = root,
            Gem = gem,
            Pulse = pulse,
            Beam = beamObject.transform,
            Label = labelObject.transform,
            Position = position,
            BornAt = UnityEngine.Time.time,
        };
    }

    private static void AddRenderer(Transform parent, string name, Mesh mesh, Material material)
    {
        var child = new GameObject(name);
        child.transform.SetParent(parent, false);
        child.AddComponent<MeshFilter>().sharedMesh = mesh;
        SetupRenderer(child.AddComponent<MeshRenderer>(), material);
    }

    private static void SetupRenderer(MeshRenderer renderer, Material material)
    {
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    private static Material Material(Color color)
    {
        var material = new Material(unlitShader!);
        color.a = 1f;
        // HDRP/Unlit takes _UnlitColor; values over 1 glow through the game's bloom. _BaseColor/_Color as fallbacks.
        if (material.HasProperty("_UnlitColor"))
            material.SetColor("_UnlitColor", color);
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", color);
        material.hideFlags = HideFlags.HideAndDontSave;
        return material;
    }

    private static void EnsureAssets()
    {
        if (!pingFontSearched)
        {
            pingFontSearched = true;
            pingFont = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(font => font.name == "Runsell Type - HemispheresCaps2 (Latin)");
        }
        if (!unlitShader)
        {
            unlitShader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            DiagLog.LogDiagnostic("friend-fun", "ping-shader", unlitShader ? unlitShader!.name : "none");
        }
        if (!gemMeshA)
            (gemMeshA, gemMeshB) = BuildGem();
        if (!ringMesh)
            ringMesh = BuildRing(0.82f, 1f, 48);
    }

    /// <summary>
    /// A six-sided gem whose long point faces down at the spot. Alternate facets go into two meshes so the two
    /// colours flash as it spins (unlit has no shading to show the facets otherwise).
    /// </summary>
    private static (Mesh, Mesh) BuildGem()
    {
        const int sides = 6;
        const float radius = 0.3f, top = 0.28f, bottom = -0.5f;
        var a = new List<Vector3>();
        var b = new List<Vector3>();
        for (var i = 0; i < sides; i++)
        {
            var angle0 = i * Mathf.PI * 2f / sides;
            var angle1 = (i + 1) * Mathf.PI * 2f / sides;
            var p0 = new Vector3(Mathf.Cos(angle0) * radius, 0f, Mathf.Sin(angle0) * radius);
            var p1 = new Vector3(Mathf.Cos(angle1) * radius, 0f, Mathf.Sin(angle1) * radius);
            var upper = i % 2 == 0 ? a : b;
            var lower = i % 2 == 0 ? b : a;
            upper.AddRange(new[] { new Vector3(0f, top, 0f), p1, p0 });
            lower.AddRange(new[] { new Vector3(0f, bottom, 0f), p0, p1 });
        }
        return (MakeMesh("PingGemA", a), MakeMesh("PingGemB", b));
    }

    /// <summary>Flat ring (both faces) of outer radius 1 in the XZ plane.</summary>
    private static Mesh BuildRing(float inner, float outer, int segments)
    {
        var vertices = new List<Vector3>();
        for (var i = 0; i < segments; i++)
        {
            var angle0 = i * Mathf.PI * 2f / segments;
            var angle1 = (i + 1) * Mathf.PI * 2f / segments;
            var d0 = new Vector3(Mathf.Cos(angle0), 0f, Mathf.Sin(angle0));
            var d1 = new Vector3(Mathf.Cos(angle1), 0f, Mathf.Sin(angle1));
            Vector3 i0 = d0 * inner, i1 = d1 * inner, o0 = d0 * outer, o1 = d1 * outer;
            vertices.AddRange(new[] { i0, o1, o0, i0, i1, o1 }); // top
            vertices.AddRange(new[] { i0, o0, o1, i0, o1, i1 }); // bottom
        }
        return MakeMesh("PingRing", vertices);
    }

    private static Mesh MakeMesh(string name, List<Vector3> vertices)
    {
        var triangles = new int[vertices.Count];
        for (var i = 0; i < triangles.Length; i++)
            triangles[i] = i;
        var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // ---------------------------------------------------------------- sound

    private const int Rate = 44100;
    private const float ChimeVolume = 0.22f;       // was 0.7 at full level (owner: far too loud next to it)
    private const float ChimeMaxDistance = 250f;
    private static readonly (float Distance, float Gain)[] ChimeRolloff =
    {
        (0f, 1f), (6f, 0.9f), (20f, 0.65f), (60f, 0.42f), (130f, 0.22f), (200f, 0.08f), (250f, 0f),
    };

    private static void PlayChime(GameObject root)
    {
        try
        {
            chime ??= BuildChime();
            var source = root.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 1f;            // fully 3D: heard from the spot
            source.dopplerLevel = 0f;
            source.volume = ChimeVolume;
            source.maxDistance = ChimeMaxDistance;
            try
            {
                // Evened-out falloff: soft up close, still audible across a zone (a plain log rolloff is
                // deafening next to the ping and near silent far away).
                var curve = new AnimationCurve();
                foreach (var (distance, gain) in ChimeRolloff)
                    curve.AddKey(distance / ChimeMaxDistance, gain);
                source.rolloffMode = AudioRolloffMode.Custom;
                source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);
            }
            catch
            {
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.minDistance = 15f;
            }
            var group = SfxGroup();
            if (group)
                source.outputAudioMixerGroup = group;
            source.clip = chime;
            source.Play();
        }
        catch (Exception ex)
        {
            DiagLog.LogDiagnostic("friend-fun", "ping-sound-error", ex.Message);
        }
    }

    /// <summary>The game's sound-effects mixer group, so the chime follows the SFX volume slider.</summary>
    private static UnityEngine.Audio.AudioMixerGroup? SfxGroup()
    {
        if (sfxGroupSearched)
            return sfxGroup;
        sfxGroupSearched = true;
        try
        {
            var groups = Resources.FindObjectsOfTypeAll<UnityEngine.Audio.AudioMixerGroup>();
            foreach (var group in groups)
            {
                var name = group.name;
                if (name.Equals("SFX", StringComparison.OrdinalIgnoreCase) || name.Equals("Sfx", StringComparison.Ordinal) ||
                    name.Equals("SoundEffects", StringComparison.OrdinalIgnoreCase))
                {
                    sfxGroup = group;
                    break;
                }
            }
            DiagLog.LogDiagnostic("friend-fun", "ping-mixer",
                $"chosen={(sfxGroup ? sfxGroup!.name : "none")}; all=[{string.Join(", ", groups.Select(group => group.name))}]");
        }
        catch (Exception ex)
        {
            DiagLog.LogDiagnostic("friend-fun", "ping-mixer", "error: " + ex.Message);
        }
        return sfxGroup;
    }

    /// <summary>Two soft bell notes rising a fifth (A5 then E6), about 0.9 s.</summary>
    private static AudioClip BuildChime()
    {
        const float seconds = 0.9f;
        var count = Mathf.CeilToInt(seconds * Rate);
        var data = new float[count];
        for (var i = 0; i < count; i++)
        {
            var t = i / (float)Rate;
            data[i] = 0.55f * Bell(t, 880f) + 0.5f * Bell(t - 0.11f, 1318.5f);
        }
        var peak = data.Max(Math.Abs);
        if (peak > 0f)
            for (var i = 0; i < count; i++)
                data[i] *= 0.6f / peak;
        var clip = AudioClip.Create("lycoreia-ping", count, 1, Rate, false);
        clip.SetData(data, 0);
        clip.hideFlags = HideFlags.HideAndDontSave;
        return clip;
    }

    private static float Bell(float t, float frequency)
    {
        if (t < 0f)
            return 0f;
        var attack = Mathf.Clamp01(t / 0.012f);
        var w = 2f * Mathf.PI * frequency * t;
        return attack * (Mathf.Sin(w) * Mathf.Exp(-t / 0.32f) +
                         0.18f * Mathf.Sin(2f * w) * Mathf.Exp(-t / 0.1f) +
                         0.05f * Mathf.Sin(3.01f * w) * Mathf.Exp(-t / 0.05f));
    }
}
