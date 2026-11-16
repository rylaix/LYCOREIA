// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppTMPro;
using Lycoreia.Text;

namespace Lycoreia.Owned.Components.UI;

internal static class MenuPolish
{
    private static bool failedLogged;

    /// <summary>Called every frame by MultiplayerUI.Update.</summary>
    internal static void Tick()
    {
        Run("update banner", UpdateBanner.Tick);
        Run("whats-new popup", WhatsNewPopup.Tick);
        Run("hint cards", HintCards.Tick);
        Run("text outlines", TmpOutline.Pump);
    }

    private static void Run(string what, Action body)
    {
        try { body(); }
        catch (Exception e)
        {
            if (failedLogged) return;
            failedLogged = true;
            try { OwnedLog.LogWarning($"menu polish ({what}) failed: {e.GetType().Name}: {e.Message}"); } catch { }
        }
    }

    /// <summary>True while the native main menu has the focus (the same test the Multiplayer launcher uses) and no world is loaded.</summary>
    internal static bool MainMenuUsable
    {
        get
        {
            try
            {
                var scene = SceneContext.Instance;
                if (scene != null && scene.Player != null) return false;
                return Il2CppMonomiPark.SlimeRancher.UI.Framework.Displays.UIDisplayManager.Instance?._lastState?.TopFocus?.Name == "MainMenu";
            }
            catch { return false; }
        }
    }

    internal static UiText Label(string text, float width, float height, float size, UiColor color = UiColor.TextGeneral,
        TextAlignmentOptions align = TextAlignmentOptions.Left, Vector2? at = null)
        => new() { Text = text, Size = new Vector2(width, height), FontSize = size, Color = color, Alignment = align, Position = at ?? Vector2.zero, AutoSize = true, AutoSizeMin = 14f, AutoSizeMax = size };

    /// <summary>A wrapped row at one fixed size (no per-row autosize), so rows of one style never differ; height from LayoutText.</summary>
    internal static UiText Fixed(string text, float width, float size, UiColor color = UiColor.TextGeneral, TextAlignmentOptions align = TextAlignmentOptions.TopLeft,
        Vector2? at = null, float minHeight = 0f)
        => new() { Text = text, Size = new Vector2(width, LayoutText.RowHeight(text, width, size, minHeight)), FontSize = size, Color = color, Alignment = align, Position = at ?? Vector2.zero, FixedWrap = true };

    internal static UiButton Button(string text, float width, float height, Action onClick, bool alternative, Vector2 at)
        => new()
        {
            Size = new Vector2(width, height), Position = at, CornerRadius = 14, Style = alternative ? UiButtonStyle.Alternative : UiButtonStyle.Normal, OnClick = onClick,
            Children = new List<UiNode> { new UiText { Text = text, Alignment = TextAlignmentOptions.Center, FontSize = 26f, Color = UiColor.TextButton, Anchors = new Vector4(0f, 0f, 1f, 1f) } },
        };

    /// <summary>Makes every graphic under the object ignore the pointer (cards and toasts must never eat clicks).</summary>
    internal static void NoRaycast(Transform root)
    {
        try { foreach (var g in root.GetComponentsInChildren<UnityEngine.UI.Graphic>(true)) if (g) g.raycastTarget = false; } catch { }
    }
}
