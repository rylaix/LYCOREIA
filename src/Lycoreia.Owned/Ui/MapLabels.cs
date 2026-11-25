// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppInterop.Runtime.Attributes;
using Il2CppMonomiPark.SlimeRancher.UI.Map;
using Il2CppTMPro;
using Lycoreia.Coop;
using Lycoreia.Game.Coop;
using Lycoreia.Text;
using UnityEngine.UI;

namespace Lycoreia.Owned.Components.UI;

internal sealed class MapLabels : MonoBehaviour
{
    public MapLabels(IntPtr ptr) : base(ptr) { }

    private static MapLabels? instance;
    private static readonly Color Gold = new(1f, 0.78f, 0.28f, 1f);

    private sealed class Hit { internal MapMarker Marker = null!; internal RectTransform Rt = null!; internal Image? Img; internal int JobId; }
    private readonly List<Hit> hits = new();
    private readonly HashSet<int> logged = new();
    private float nextScan, lastDiag;
    private int lastFound = -1, lastMatched = -1, unmatchedScans;
    private bool warnedNoMatch;
    private RectTransform? panel, rect;
    private TextMeshProUGUI? text;
    private int shownJob = -1;
    private float[] xs = new float[64], ys = new float[64];

    /// <summary>Creates the host once (child of the Lycoreia canvas). Safe to call repeatedly.</summary>
    internal static void Ensure()
    {
        try
        {
            if (instance) return;
            if (!Il2CppInterop.Runtime.Injection.ClassInjector.IsTypeRegisteredInIl2Cpp<MapLabels>())
                Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<MapLabels>();
            var go = new GameObject("LycoreiaMapLabels");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(UiCanvas.Root, false);
            UiNode.Stretch(rt);
            instance = go.AddComponent<MapLabels>();
            OwnedLog.LogMessage("map labels: hover label host created");
        }
        catch (Exception e) { try { OwnedLog.LogWarning("map labels host: " + e); } catch { } }
    }

    private RectTransform Rect => rect ? rect! : (rect = GetComponent<RectTransform>())!;

    private void Update()
    {
        try
        {
            var snap = CoopWorld.MapMarkerSnapshot();
            if (snap.Count == 0) { Hide(); hits.Clear(); return; }
            float now = Time.unscaledTime;
            if (now >= nextScan) { nextScan = now + 0.25f; Scan(snap); }
            if (hits.Count == 0) { Hide(); return; }
            ApplyIcons();
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) { Hide(); return; }
            var mp = mouse.position.ReadValue();
            if (hits.Count > xs.Length) { xs = new float[hits.Count * 2]; ys = new float[hits.Count * 2]; }
            for (int i = 0; i < hits.Count; i++)
            {
                var h = hits[i];
                if (!h.Rt) { xs[i] = ys[i] = -99999f; continue; }
                var canvas = h.Rt.GetComponentInParent<Canvas>();
                Camera? cam = canvas && canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.rootCanvas.worldCamera : null;
                var sp = RectTransformUtility.WorldToScreenPoint(cam, h.Rt.position);
                xs[i] = sp.x; ys[i] = sp.y;
            }
            int idx = MapHover.Nearest(xs, ys, hits.Count, mp.x, mp.y, MapHover.Radius(Screen.height));
            if (idx < 0) { Hide(); return; }
            Show(hits[idx].JobId, mp);
        }
        catch (Exception e) { Hide(); WarnOnce("map labels: " + e.Message); }
    }

    private string? warned;
    private void WarnOnce(string s) { if (warned == s) return; warned = s; try { OwnedLog.LogWarning(s); } catch { } }

    [HideFromIl2Cpp]
    private void Scan(List<(IntPtr ptr, int job)> snap)
    {
        hits.Clear();
        var all = Object.FindObjectsOfType<MapMarker>();
        int found = 0;
        if (all != null)
            foreach (var m in all)
            {
                if (!m || !m.gameObject.activeInHierarchy) continue;
                found++;
                var src = m._markerSource; if (src == null) continue;
                IntPtr p = src.Pointer; int job = -1;
                foreach (var s in snap) if (s.ptr == p) { job = s.job; break; }
                if (job < 0) continue;
                var rt = m._rectTransform; if (!rt) rt = m.GetComponent<RectTransform>();
                hits.Add(new Hit { Marker = m, Rt = rt, Img = m._image, JobId = job });
            }
        // diagnostics: on change only, and one warning when the map shows markers but none of ours matched
        if ((found != lastFound || hits.Count != lastMatched) && Time.unscaledTime - lastDiag > 1f)
        {
            lastDiag = Time.unscaledTime; lastFound = found; lastMatched = hits.Count;
            if (found > 0) try { OwnedLog.LogMessage($"map labels: map open, {found} map markers, {hits.Count} of {snap.Count} job markers drawn"); } catch { }
        }
        if (found > 0 && hits.Count == 0) { if (++unmatchedScans >= 8 && !warnedNoMatch) { warnedNoMatch = true; try { OwnedLog.LogWarning($"map labels: map has {found} markers but none matches our {snap.Count} job markers (source pointer mismatch), hover label disabled"); } catch { } } }
        else unmatchedScans = 0;
    }

    /// <summary>The native image shows the map's waypoint sprite; draw our gold flag instead so jobs read as different from the player waypoint.</summary>
    [HideFromIl2Cpp]
    private void ApplyIcons()
    {
        var flag = JobIcon.MapFlag();
        foreach (var h in hits)
        {
            var img = h.Img; if (!img) continue;
            if (img!.sprite != flag) img.sprite = flag;
            if (img.color != Gold) img.color = Gold;
        }
    }

    [HideFromIl2Cpp]
    private void Show(int jobId, Vector2 screen)
    {
        var job = CoopRuntime.Board.Jobs.Find(j => j.Id == jobId);
        if (job == null || job.Done) { Hide(); return; }
        Build();
        if (panel == null || text == null) return;
        string me = Mod.LocalId;
        string who = job.ClaimedBy.Length > 0 ? CoopRuntime.NameOf(job.ClaimedBy) : "";
        string s = MapText.Label(job.Text, job.CreatedBy.Length > 0 ? CoopRuntime.NameOf(job.CreatedBy) : "", who, job.ClaimedBy.Length > 0 && job.ClaimedBy == me);
        if (logged.Add(jobId)) try { OwnedLog.LogMessage($"map labels: hover on job {jobId}"); } catch { }
        if (text.text != s) { text.text = s; text.ForceMeshUpdate(); }
        float w = 420f, h = Mathf.Max(70f, text.GetPreferredValues(s, w - 2f * UiPlates.PadCard.x, 0f).y + 2f * UiPlates.PadCard.y);   // measured at the final width
        panel.sizeDelta = new Vector2(w, h);
        var root = Rect.rect;
        float k = root.width / Mathf.Max(1f, Screen.width);
        float x = Mathf.Clamp(screen.x * k + 22f, 0f, Mathf.Max(0f, root.width - w));
        float y = Mathf.Clamp(screen.y * k - 22f, h, root.height);
        panel.anchoredPosition = new Vector2(x, y);
        if (!panel.gameObject.activeSelf) panel.gameObject.SetActive(true);
        shownJob = jobId;
    }

    private void Hide() { if (panel && panel!.gameObject.activeSelf) panel.gameObject.SetActive(false); shownJob = -1; }

    [HideFromIl2Cpp]
    private void Build()
    {
        if (panel) return;
        panel = UiNode.AddChild(Rect, "JobHoverLabel");
        panel.anchorMin = panel.anchorMax = Vector2.zero; panel.pivot = new Vector2(0f, 1f);
        var plate = panel.gameObject.AddComponent<Image>(); plate.raycastTarget = false; UiPlates.Apply(plate, PlateKind.Card, UiPlates.FillStrong);
        var t = UiNode.AddChild(panel, "Text");
        t.anchorMin = Vector2.zero; t.anchorMax = Vector2.one; var pad = UiPlates.PadCard; t.offsetMin = pad; t.offsetMax = -pad;
        text = t.gameObject.AddComponent<TextMeshProUGUI>();
        var font = UiFonts.Default; if (font) text.font = font;
        text.fontSize = 22f; text.color = UiTheme.GeneralText; text.raycastTarget = false; text.richText = true;
        text.alignment = TextAlignmentOptions.TopLeft; text.enableWordWrapping = true; text.overflowMode = TextOverflowModes.Overflow;
        var g = panel.gameObject.AddComponent<CanvasGroup>(); g.blocksRaycasts = false; g.interactable = false;
        panel.gameObject.SetActive(false);
    }
}
