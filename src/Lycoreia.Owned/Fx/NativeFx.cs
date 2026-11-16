// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Text;

namespace Lycoreia.Owned.Fx;

/// <summary>
/// Pure name scoring (unit tested): higher is a better match, -1 for none. Names are split into CamelCase / underscore / digit words
/// and a wanted word must START a word ("ping" matches PingMedium, not envFieldsLapping01). Voice-over (VO*) and ambient (env*)
/// cues are penalised, a leading "Player" is a bonus, and an exact name from the prefer list beats everything.
/// </summary>
public static class FxNames
{
    /// <summary>Lower-case words of a name: PlayerJumpCloud -> player, jump, cloud; VOTwinJump -> vo, twin, jump; envFieldsLapping01 -> env, fields, lapping, 01.</summary>
    public static List<string> Words(string? name)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(name)) return words;
        var s = name!;
        var cur = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (!char.IsLetterOrDigit(ch)) { Flush(); continue; }
            if (cur.Length > 0)
            {
                var prev = s[i - 1];
                var split = (char.IsUpper(ch) && char.IsLower(prev))                                   // fooBar
                            || (char.IsDigit(ch) != char.IsDigit(prev))                                // foo01
                            || (char.IsUpper(ch) && char.IsUpper(prev) && i + 1 < s.Length && char.IsLower(s[i + 1]));   // VOTwin -> VO, Twin
                if (split) Flush();
            }
            cur.Append(char.ToLowerInvariant(ch));
        }
        Flush();
        return words;
        void Flush() { if (cur.Length > 0) { words.Add(cur.ToString()); cur.Clear(); } }
    }

    /// <summary>True when the word starts one of the name's words, or several consecutive words ("newitem" in NewItemPop).</summary>
    public static bool HasWord(List<string> words, string word)
    {
        if (words.Count == 0 || string.IsNullOrEmpty(word)) return false;
        var joined = string.Concat(words);
        var start = 0;
        foreach (var w in words)
        {
            if (string.CompareOrdinal(joined, start, word, 0, word.Length) == 0 && start + word.Length <= joined.Length) return true;
            start += w.Length;
        }
        return false;
    }

    public static int Score(string? name, string[] want, string[] avoid, string[]? prefer = null)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        if (prefer != null)
            for (var p = 0; p < prefer.Length; p++)
                if (string.Equals(name, prefer[p], StringComparison.OrdinalIgnoreCase)) return 200 - p;
        var words = Words(name);
        var best = -1;
        for (var i = 0; i < want.Length; i++)
            if (HasWord(words, want[i]))
            {
                best = 100 - i * 10;
                break;
            }
        if (best < 0) return -1;
        foreach (var bad in avoid)
            if (HasWord(words, bad))
                best -= 30;
        if (words.Count > 0)
        {
            if (words[0] == "vo") best -= 60;                         // slime voice-over (VOTwinJump)
            else if (words[0].StartsWith("env", StringComparison.Ordinal)) best -= 60;   // ambient environment loops (envFieldsLapping01)
            else if (words[0] == "player") best += 8;
        }
        best -= Math.Min(name!.Length / 8, 5);   // among equals prefer the shorter, more generic name
        return best < 0 ? 0 : best;
    }
}

internal enum FxCue : byte { Ping, Jump }

internal static class NativeFx
{
    private static readonly string[][] CueWant =
    {
        /* Ping */ new[] { "ping", "waypoint", "beacon", "marker", "notification", "newitem" },
        /* Jump */ new[] { "jump", "jetpack", "hop", "boost", "launch" },
    };
    private static readonly string[][] CueAvoid =
    {
        /* Ping */ new[] { "pause", "lab", "gadget", "slime", "gordo", "tarr", "plort", "vac", "ambient", "music", "lapping" },
        /* Jump */ new[] { "land", "slime", "gordo", "tarr", "chick", "hen", "largo", "plort", "vac", "pad", "ring", "ambient", "music", "ui",
                           "batty", "flutter", "angler", "twin", "pink", "rock", "phosphor", "tabby", "hunter", "saber", "cotton", "boom", "crystal", "dervish", "ringtail", "yolky" },
    };
    /// <summary>Exact native names tried first (owner logs 2026-11-15: ResourceDetector_PingMedium, PlayerJump* family, FX_SmokeDust_Medium exist).</summary>
    internal static readonly string[][] CuePrefer =
    {
        /* Ping */ new[] { "ResourceDetector_PingMedium", "ResourceDetector_PingClose", "ResourceDetector_PingFar" },
        /* Jump */ new[] { "PlayerJumpCloud", "PlayerJumpMoss", "PlayerJumpStone", "PlayerJumpGlass" },
    };
    private static readonly string[] PuffWant = { "smokedust", "jetpack", "dust", "puff", "smoke", "jump", "land" };
    private static readonly string[] PuffAvoid = { "slime", "gordo", "tarr", "vac", "plort", "rain", "fire", "water", "lava", "ui", "lightning", "explosion", "blast", "radiant", "door", "impact" };
    private static readonly string[] PuffPrefer = { "FX_SmokeDust_Medium", "FX_SmokeDust" };

    private static readonly SECTR_AudioCue?[] cues = new SECTR_AudioCue?[2];
    private static readonly int[] cueTries = new int[2];
    private static readonly float[] cueRetryAt = new float[2];
    private static GameObject? puffPrefab;
    private static int puffTries;
    private static float puffRetryAt;
    private static Texture2D? softDot;
    private static Material? fallbackMaterial;
    private static bool materialTried;

    /// <summary>Plays the native cue at a world position. False when no native cue exists (caller may use its own sound).</summary>
    internal static bool PlayCue(FxCue kind, Vector3 position)
    {
        try
        {
            var i = (int)kind;
            if (!cues[i] && cueTries[i] < 3 && Time.unscaledTime >= cueRetryAt[i])
                ResolveCue(kind);
            var cue = cues[i];
            if (!cue)
                return false;
            SECTR_AudioSystem.Play(cue, position, false);
            return true;
        }
        catch (Exception ex)
        {
            cueTries[(int)kind] = 99;
            OwnedLog.LogWarning($"fx cue {kind} disabled: {ex.Message}");
            return false;
        }
    }

    private static void ResolveCue(FxCue kind)
    {
        var i = (int)kind;
        cueTries[i]++;
        cueRetryAt[i] = Time.unscaledTime + 20f;
        var found = new List<(int Score, string Name, SECTR_AudioCue Cue)>();
        try
        {
            foreach (var cue in Resources.FindObjectsOfTypeAll<SECTR_AudioCue>())
            {
                if (!cue) continue;
                var score = FxNames.Score(cue.name, CueWant[i], CueAvoid[i], CuePrefer[i]);
                if (score >= 0) found.Add((score, cue.name, cue));
            }
        }
        catch (Exception ex)
        {
            OwnedLog.LogWarning($"fx cue {kind} lookup failed: {ex.Message}");
        }
        found.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (found.Count > 0)
            cues[i] = found[0].Cue;
        var top = new StringBuilder();
        for (var n = 0; n < Math.Min(6, found.Count); n++)
            top.Append(found[n].Name).Append('(').Append(found[n].Score).Append(") ");
        OwnedLog.LogMessage($"fx cue {kind}: {(cues[i] ? "native '" + cues[i]!.name + "'" : "none found, fallback")} (try {cueTries[i]}); candidates: {top}");
    }

    /// <summary>A small dust puff at a world position: a native particle prefab when one is found, else an own ParticleSystem.</summary>
    internal static void Puff(Vector3 position)
    {
        try
        {
            if (!puffPrefab && puffTries < 3 && Time.unscaledTime >= puffRetryAt)
                ResolvePuff();
            if (puffPrefab)
            {
                var go = Object.Instantiate(puffPrefab!, position, Quaternion.identity);
                go.SetActive(true);
                foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
                    if (ps) ps.Play(true);
                Object.Destroy(go, 4f);
                return;
            }
            OwnPuff(position);
        }
        catch (Exception ex)
        {
            puffTries = 99;
            puffPrefab = null;
            OwnedLog.LogWarning($"fx puff failed: {ex.Message}");
        }
    }

    private static void ResolvePuff()
    {
        puffTries++;
        puffRetryAt = Time.unscaledTime + 20f;
        var found = new List<(int Score, string Name, GameObject Go)>();
        try
        {
            foreach (var ps in Resources.FindObjectsOfTypeAll<ParticleSystem>())
            {
                if (!ps) continue;
                var go = ps.gameObject;
                if (go.scene.IsValid() || go.transform.parent) continue;   // prefab assets that are their own root only
                var score = FxNames.Score(go.name, PuffWant, PuffAvoid, PuffPrefer);
                if (score >= 0) found.Add((score, go.name, go));
            }
        }
        catch (Exception ex)
        {
            OwnedLog.LogWarning($"fx puff lookup failed: {ex.Message}");
        }
        found.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (found.Count > 0)
            puffPrefab = found[0].Go;
        var top = new StringBuilder();
        for (var n = 0; n < Math.Min(6, found.Count); n++)
            top.Append(found[n].Name).Append('(').Append(found[n].Score).Append(") ");
        OwnedLog.LogMessage($"fx puff: {(puffPrefab ? "native prefab '" + puffPrefab!.name + "'" : "none found, own particles")} (try {puffTries}); candidates: {top}");
    }

    private static void OwnPuff(Vector3 position)
    {
        var mat = FallbackMaterial();
        if (!mat) return;
        var go = new GameObject("LycoreiaPuff");
        go.transform.position = position;
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ps.loop = false;
        ps.playOnAwake = false;
        ps.enableEmission = false;                              // only the explicit Emit calls below
        ps.simulationSpace = ParticleSystemSimulationSpace.World;
        ps.gravityModifier = 0f;
        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = mat;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        ps.Play();
        // a ring of soft dust puffs flying outward along the ground, each with its own size and lifetime
        const int count = 14;
        for (var i = 0; i < count; i++)
        {
            var angle = (i + UnityEngine.Random.value * 0.6f) * Mathf.PI * 2f / count;
            var dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            var speed = 0.8f + UnityEngine.Random.value * 1.2f;
            ps.Emit(position + dir * 0.25f + Vector3.up * 0.05f, dir * speed + Vector3.up * (0.2f + UnityEngine.Random.value * 0.5f),
                0.28f + UnityEngine.Random.value * 0.22f, 0.35f + UnityEngine.Random.value * 0.25f, new Color32(255, 255, 255, 180));
        }
        Object.Destroy(go, 2f);
    }

    private static Material? FallbackMaterial()
    {
        if (fallbackMaterial || materialTried)
            return fallbackMaterial;
        materialTried = true;
        try
        {
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Particles/Standard Unlit") ?? Shader.Find("HDRP/Unlit");
            if (!shader)
            {
                OwnedLog.LogWarning("fx puff: no particle shader; no puff");
                return null;
            }
            softDot = BuildDot();
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, mainTexture = softDot };
            if (mat.HasProperty("_UnlitColorMap")) mat.SetTexture("_UnlitColorMap", softDot);
            fallbackMaterial = mat;
            OwnedLog.LogMessage($"fx puff material: {shader.name}");
        }
        catch (Exception ex)
        {
            OwnedLog.LogWarning($"fx puff material failed: {ex.Message}");
        }
        return fallbackMaterial;
    }

    private static Texture2D BuildDot()
    {
        const int n = 32;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var d = Mathf.Sqrt((x - 15.5f) * (x - 15.5f) + (y - 15.5f) * (y - 15.5f)) / 15.5f;
                var a = Mathf.Clamp01(1f - d);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
            }
        tex.Apply();
        return tex;
    }
}
