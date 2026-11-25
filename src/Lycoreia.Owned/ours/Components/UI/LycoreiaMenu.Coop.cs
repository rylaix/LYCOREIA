// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppInterop.Runtime.Attributes;
using Il2CppTMPro;
using Lycoreia.Coop;
using Lycoreia.Game.Coop;
using Lycoreia.Text;

namespace Lycoreia.Owned.Components.UI;

internal sealed partial class LycoreiaMenu
{
    private enum CoopTab : byte { Jobs, Notes, Expeditions, Recap }
    private CoopTab coopTab = CoopTab.Jobs;
    private int whatsNewSection, whatsNewPage;
    private const int WhatsNewPerPage = 4;

    [HideFromIl2Cpp]
    private string CoopSignature() => $"{BoardKey()}|{coopTab}|{whatsNewSection}|{whatsNewPage}|{CoopRuntime.LastRecap.Count}|{CoopText.RenderLang()}";

    /// <summary>
    /// What the visible board page shows, without the parts that tick ("menu blinks"): the menu used to key on CoopRuntime.Revision
    /// (bumped by every 2 s stat report and every expedition progress step) and on the expedition clock, so it was destroyed and rebuilt
    /// every second or two. Pages without board content ignore the board; the expedition clock, progress and team counts update in place.
    /// </summary>
    [HideFromIl2Cpp]
    private string BoardKey()
    {
        if (page != Page.Coop) return string.Empty;
        var b = CoopRuntime.Board;
        switch (coopTab)
        {
            case CoopTab.Jobs:
                return string.Join(";", b.Jobs.Select(j => $"{j.Id},{j.Done},{j.ClaimedBy},{j.CreatedBy},{j.HasPosition},{j.Text}")) + "|" + RecentTick();
            case CoopTab.Notes:
                return string.Join(";", b.Notes.Select(n => $"{n.Id},{n.UpdatedUtc},{n.Scene},{n.Author},{n.Text}")) + "|" + Lycoreia.Game.Avatars.RemoteAvatars.LocalSceneGroup();
            case CoopTab.Expeditions:
                var r = b.Expedition;
                return (r == null ? "-" : $"{r.DefId},{r.StartedUtc},{r.Outcome},{r.Target},{r.Reward},{string.Join(",", r.ByPlayer.Keys.OrderBy(k => k))}" +
                        (r.Outcome == ExpeditionOutcome.Running ? string.Empty : $",{r.Progress}")) +
                       $"|{b.History.Count}|{Lycoreia.Mod.Session?.Players.Count}";
            default:
                return string.Join(";", b.Recap.Select(p => p.Key + ":" + string.Join(",", p.Value.Select(s => $"{s.Key}={s.Value}"))));
        }
    }

    private UiText? expProgressLine, expTeamLine;

    /// <summary>Expedition clock, progress and per-player counts written into the already rendered rows (no rebuild, no blink).</summary>
    [HideFromIl2Cpp]
    private void UpdateLiveLines()
    {
        try
        {
            if (page != Page.Coop || coopTab != CoopTab.Expeditions) return;
            var run = CoopRuntime.Board.Expedition;
            if (run == null || run.Outcome != ExpeditionOutcome.Running) return;
            long left = Math.Max(0, run.EndsUtc - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            SetLive(expProgressLine, MenuText.ExpProgress(run.Progress, run.Target, left, run.Reward));
            if (run.ByPlayer.Count > 0)
                SetLive(expTeamLine, MenuText.Team(string.Join(", ", run.ByPlayer.OrderByDescending(p => p.Value).Select(p => $"{CoopRuntime.NameOf(p.Key)} {p.Value}"))));
        }
        catch (Exception ex) { OwnedLog.LogWarning("menu live lines: " + ex.Message); }
    }

    [HideFromIl2Cpp]
    private static void SetLive(UiText? line, string text)
    {
        if (line?.Rendered == null || !line.Rendered) return;
        var tmp = line.Rendered.GetComponent<TextMeshProUGUI>();
        if (tmp && tmp.text != text) tmp.text = text;
    }

    /// <summary>"Recently done" shows how long ago: refresh the text once a minute while the board is open.</summary>
    private static long RecentTick() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;

    /// <summary>Opens the menu on the "What's new" page (the popup's "Read everything" button).</summary>
    internal static void OpenWhatsNew()
    {
        try
        {
            OpenMenu();
            var menu = _instance;
            if (menu == null || !_isOpen) return;
            menu.page = Page.WhatsNew; menu.whatsNewSection = 0; menu.whatsNewPage = 0; menu.Rebuild();
        }
        catch (Exception ex) { OwnedLog.LogWarning($"Could not open the what's new page: {ex.Message}"); }
    }

    [HideFromIl2Cpp]
    private void AddWhatsNewAndRecapButtons(List<UiNode> rows)
    {
        rows.Add(Button(MenuText.WhatsNewButton(BuildInfo.Version), () => Navigate(Page.WhatsNew)));
        if (CoopRuntime.LastRecap.Count > 0)
            rows.Add(Button(MenuText.LastSessionButton, () => Navigate(Page.Recap), alternative: true));
    }

    // ---------------------------------------------------------------- what's new
    [HideFromIl2Cpp]
    private void AddWhatsNewPage(List<UiNode> rows)
    {
        var sections = WhatsNew.For(CoopText.RenderLang());
        rows.Add(Heading(MenuText.WhatsNewHeading(BuildInfo.Version)));
        var tabs = sections.Select((s, i) => (s.Tab ?? s.Title, i == whatsNewSection, (Action)(() => { whatsNewSection = i; whatsNewPage = 0; Rebuild(); }))).ToArray();
        rows.Add(Tabs(tabs));
        var section = sections[Math.Clamp(whatsNewSection, 0, sections.Count - 1)];
        // rc6: cards in a 2-column grid, 4 per page (the menu does not scroll: a long list used to shrink the whole panel)
        int pages = Math.Max(1, (section.Items.Count + WhatsNewPerPage - 1) / WhatsNewPerPage);
        whatsNewPage = Math.Clamp(whatsNewPage, 0, pages - 1);
        rows.Add(WhatsNewCards.Grid(section.Items.Skip(whatsNewPage * WhatsNewPerPage).Take(WhatsNewPerPage).ToList(), RowWidth, showTry: true));
        if (pages > 1)
            rows.Add(Tabs(
                (MenuText.PrevPage, false, () => { if (whatsNewPage > 0) { whatsNewPage--; Rebuild(); } }),
                (MenuText.PageOf(whatsNewPage + 1, pages), true, () => { }),
                (MenuText.NextPage, false, () => { if (whatsNewPage < pages - 1) { whatsNewPage++; Rebuild(); } })));
        rows.Add(Line(MenuText.FoundProblem, UiColor.TextWarning, TryItSize));
        rows.Add(Button(MenuText.Back, () => Navigate(Page.Home), alternative: true));
    }

    // ---------------------------------------------------------------- last session recap (after leaving)
    [HideFromIl2Cpp]
    private void AddRecapPage(List<UiNode> rows)
    {
        rows.Add(Heading(MenuText.LastSession));
        if (CoopRuntime.LastRecap.Count == 0) rows.Add(Line(MenuText.NothingRecorded));
        foreach (var (name, line) in CoopRuntime.LastRecap) rows.Add(Line($"{name}: {line}"));
        rows.Add(Button(MenuText.Back, () => Navigate(Page.Home), alternative: true));
    }

    // ---------------------------------------------------------------- co-op board
    [HideFromIl2Cpp]
    private void AddCoopPage(List<UiNode> rows)
    {
        rows.Add(Heading(MenuText.BoardTitle));
        rows.Add(Tabs(
            (MenuText.TabJobs, coopTab == CoopTab.Jobs, () => SetCoopTab(CoopTab.Jobs)),
            (MenuText.TabNotes, coopTab == CoopTab.Notes, () => SetCoopTab(CoopTab.Notes)),
            (MenuText.TabExpeditions, coopTab == CoopTab.Expeditions, () => SetCoopTab(CoopTab.Expeditions)),
            (MenuText.TabRecap, coopTab == CoopTab.Recap, () => SetCoopTab(CoopTab.Recap))));
        switch (coopTab)
        {
            case CoopTab.Jobs: AddJobsTab(rows); break;
            case CoopTab.Notes: AddNotesTab(rows); break;
            case CoopTab.Expeditions: AddExpeditionsTab(rows); break;
            default: AddRecapTab(rows); break;
        }
        rows.Add(Button(MenuText.Back, () => Navigate(Page.Home), alternative: true));
    }

    private void SetCoopTab(CoopTab tab) { coopTab = tab; notice = string.Empty; Rebuild(); }

    /// <summary>The wheel is opened by holding the ping button (middle mouse), so the hint names the player's own glyph for it.</summary>
    private static string WheelGlyph => InputGlyphs.Label("MMB");

    [HideFromIl2Cpp]
    private void AddJobsTab(List<UiNode> rows)
    {
        var board = CoopRuntime.Board; string me = Lycoreia.Mod.LocalId;
        var open = board.Jobs.Where(j => !j.Done).OrderBy(j => j.ClaimedBy == me ? 0 : j.ClaimedBy.Length == 0 ? 1 : 2).ThenBy(j => j.Id).Take(7).ToList();
        if (open.Count == 0)
        {
            rows.Add(Line(MenuText.JobsEmpty));
            rows.Add(Line(MenuText.JobsEmptyHint(WheelGlyph), UiColor.TextCategory));   // empty state: say how to start
        }
        else rows.Add(Line(MenuText.WheelHint(WheelGlyph), UiColor.TextCategory));
        foreach (var j in open)
        {
            string state = j.ClaimedBy.Length == 0 ? MenuText.JobOpen : j.ClaimedBy == me ? MenuText.JobYou : CoopRuntime.NameOf(j.ClaimedBy);
            rows.Add(JobRow(JobKinds.Of(j.Anchor, j.Text, j.HasPosition), $"{j.Text}  <alpha=#BB>({state}{(j.HasPosition ? MenuText.JobOnCompass : "")})"));
            int id = j.Id;
            var actions = new List<(string, bool, Action)>();
            if (j.ClaimedBy.Length == 0) actions.Add((MenuText.ActClaim, false, () => CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.JobClaim, Id = id })));
            else if (j.ClaimedBy == me) actions.Add((MenuText.ActDrop, false, () => CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.JobUnclaim, Id = id })));
            actions.Add((MenuText.ActDone, false, () => CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.JobDone, Id = id })));
            if (j.CreatedBy == me || Lycoreia.Mod.IsHost) actions.Add((MenuText.ActRemove, false, () => CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.JobRemove, Id = id })));
            rows.Add(Tabs(actions.ToArray()));
        }
        int more = board.Jobs.Count(j => !j.Done) - open.Count; if (more > 0) rows.Add(Line(MenuText.More(more), UiColor.TextCategory));

        var done = board.Jobs.Where(j => j.Done).OrderByDescending(j => j.DoneUtc).Take(4).ToList();
        if (done.Count > 0)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            rows.Add(Line(MenuText.RecentlyDone, UiColor.TextCategory));
            foreach (var j in done)
            {
                string who = CoopRuntime.NameOf(j.ClaimedBy);
                rows.Add(JobRow(JobKinds.Of(j.Anchor, j.Text, j.HasPosition), $"<alpha=#CC>{j.Text}  {MenuText.DoneBy(who, MenuText.Ago(Math.Max(0, now - j.DoneUtc)))}", 40f));
            }
        }
    }

    // One fixed size per row style (no per-row autosize, which made neighbouring rows differ): headline/body, and the smaller "try it" line.
    private const float BodySize = 24f, TryItSize = 20f, TryItInset = 28f;

    /// <summary>A wrapped text row at a fixed font size, with a left inset (no leading spaces); its height follows the number of lines.</summary>
    [HideFromIl2Cpp]
    private static UiText Line(string text, UiColor color = UiColor.TextGeneral, float size = BodySize, float inset = 0f, float width = RowWidth)
    {
        var node = MenuPolish.Fixed(text, width - inset, size, color, TextAlignmentOptions.MidlineLeft, new Vector2(inset / 2f, 0f), size + 14f);
        return node;
    }

    /// <summary>One list row: the job kind's icon, then the text.</summary>
    [HideFromIl2Cpp]
    private static UiPanel JobRow(string kind, string text, float height = 56f)
    {
        float icon = height - 10f;
        var iconNode = JobIcons.Icon(kind, icon, new Vector2(-RowWidth / 2f + icon / 2f + 4f, 0f));
        float textW = RowWidth - icon - 20f, size = height < 50f ? 20f : BodySize;
        var label = MenuPolish.Fixed(text, textW, size, UiColor.TextGeneral, TextAlignmentOptions.MidlineLeft, new Vector2(icon / 2f + 6f, 0f), height);
        height = label.Size.y;   // a long (Russian) job text wraps: the row grows instead of overflowing
        return new UiPanel { Color = UiColor.Transparent, Size = new Vector2(RowWidth, height), Children = new List<UiNode> { iconNode, label } };
    }

    [HideFromIl2Cpp]
    private void AddNotesTab(List<UiNode> rows)
    {
        int scene = Lycoreia.Game.Avatars.RemoteAvatars.LocalSceneGroup();
        var notes = CoopRuntime.Board.Notes.OrderBy(n => n.Scene == scene ? 0 : 1).ThenByDescending(n => n.UpdatedUtc).Take(8).ToList();
        if (notes.Count == 0)
        {
            rows.Add(Line(MenuText.NotesEmpty));
            rows.Add(Line(MenuText.NotesEmptyHint(WheelGlyph), UiColor.TextCategory));
        }
        else rows.Add(Line(MenuText.WheelHint(WheelGlyph), UiColor.TextCategory));
        foreach (var n in notes)
        {
            int id = n.Id;
            rows.Add(Row(Line($"• {n.Text}  <alpha=#BB>{CoopRuntime.NameOf(n.Author)}{(n.Scene == scene ? "" : MenuText.Elsewhere)}", UiColor.TextGeneral, BodySize, 0f, 640f),
                Button(MenuText.ActRemove, () => CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.NoteRemove, Id = id }), 200f, alternative: true)));
        }
        if (CoopRuntime.Board.Notes.Count > notes.Count) rows.Add(Line(MenuText.More(CoopRuntime.Board.Notes.Count - notes.Count), UiColor.TextCategory));
    }

    [HideFromIl2Cpp]
    private void AddExpeditionsTab(List<UiNode> rows)
    {
        var run = CoopRuntime.Board.Expedition; var def = run != null ? Expeditions.Find(run.DefId) : null;
        int players = Math.Max(1, Lycoreia.Mod.Session?.Players.Count ?? 1);
        if (run != null && def != null && run.Outcome == ExpeditionOutcome.Running)
        {
            long left = Math.Max(0, run.EndsUtc - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            rows.Add(Line(MenuText.ExpRunning(MenuText.ExpName(def), MenuText.ExpGoal(def))));
            rows.Add(expProgressLine = Line(MenuText.ExpProgress(run.Progress, run.Target, left, run.Reward)));
            if (run.ByPlayer.Count > 0) rows.Add(expTeamLine = Line(MenuText.Team(string.Join(", ", run.ByPlayer.OrderByDescending(p => p.Value).Select(p => $"{CoopRuntime.NameOf(p.Key)} {p.Value}"))), UiColor.TextCategory));
            rows.Add(Button(MenuText.CallOff, () => CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.ExpeditionAbandon }), alternative: true));
            return;
        }
        if (run != null && def != null)
            rows.Add(Line(run.Outcome == ExpeditionOutcome.Succeeded ? MenuText.LastDone(MenuText.ExpName(def), run.Reward)
                : MenuText.LastOther(MenuText.ExpName(def), run.Progress, run.Target, run.Outcome.ToString().ToLowerInvariant()), UiColor.TextCategory));
        rows.Add(Line(MenuText.PickOne, UiColor.TextCategory));
        foreach (var d in CoopRuntime.Board.Offers())
        {
            int id = d.Id; var (goal, reward) = CoopRuntime.Board.Preview(d, players); int level = CoopRuntime.Board.Level(d.Id);
            rows.Add(Button(MenuText.ExpOffer(MenuText.ExpName(d), level, MenuText.ExpGoal(d), goal, d.Minutes, reward), () =>
                CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.ExpeditionStart, Id = id })));
        }
        int won = CoopRuntime.Board.History.Count(h => h.Outcome == ExpeditionOutcome.Succeeded);
        if (CoopRuntime.Board.History.Count > 0) rows.Add(Line(MenuText.RanchRecord(won, CoopRuntime.Board.History.Count), UiColor.TextCategory));
    }

    [HideFromIl2Cpp]
    private void AddRecapTab(List<UiNode> rows)
    {
        var recap = CoopRuntime.Board.Recap;
        if (recap.Count == 0) rows.Add(Line(MenuText.RecapEmpty, UiColor.TextCategory));
        foreach (var p in recap.OrderBy(p => p.Key == Lycoreia.Mod.LocalId ? 0 : 1)) rows.Add(Line($"{CoopRuntime.NameOf(p.Key)}: {MenuText.RecapLine(p.Value)}"));
        rows.Add(Line(MenuText.Together(CoopRuntime.Board.Total(CoopStat.PlortsSold), CoopRuntime.Board.Total(CoopStat.MoneyEarned)), UiColor.TextCategory));
    }
}
