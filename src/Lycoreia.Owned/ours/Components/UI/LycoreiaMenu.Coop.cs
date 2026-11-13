// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppInterop.Runtime.Attributes;
using Lycoreia.Coop;
using Lycoreia.Game.Coop;

namespace Lycoreia.Owned.Components.UI;

internal sealed partial class LycoreiaMenu
{
    private enum CoopTab : byte { Jobs, Notes, Expeditions, Recap }
    private CoopTab coopTab = CoopTab.Jobs;
    private int whatsNewSection;

    [HideFromIl2Cpp]
    private string CoopSignature() => $"{CoopRuntime.Revision}|{coopTab}|{whatsNewSection}|{CoopRuntime.LastRecap.Count}|{ExpeditionClock()}";

    /// <summary>The menu redraws once a second while an expedition runs (its clock).</summary>
    private static long ExpeditionClock()
    {
        var run = CoopRuntime.Board.Expedition;
        return run != null && run.Outcome == ExpeditionOutcome.Running ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : 0;
    }

    [HideFromIl2Cpp]
    private void AddWhatsNewAndRecapButtons(List<UiNode> rows)
    {
        rows.Add(Button($"What's new in {BuildInfo.Version}?", () => Navigate(Page.WhatsNew)));
        if (CoopRuntime.LastRecap.Count > 0)
            rows.Add(Button("Last session recap", () => Navigate(Page.Recap), alternative: true));
    }

    // ---------------------------------------------------------------- what's new
    [HideFromIl2Cpp]
    private void AddWhatsNewPage(List<UiNode> rows)
    {
        rows.Add(Heading($"What's new in Lycoreia {BuildInfo.Version}"));
        var tabs = WhatsNew.Sections.Select((s, i) => (s.Title, i == whatsNewSection, (Action)(() => { whatsNewSection = i; Rebuild(); }))).ToArray();
        rows.Add(Tabs(tabs));
        var section = WhatsNew.Sections[Math.Clamp(whatsNewSection, 0, WhatsNew.Sections.Count - 1)];
        foreach (var (text, tryIt) in section.Items)
        {
            rows.Add(Label("• <b>" + text + "</b>", UiColor.TextCategory));                         // the change itself stands out
            if (!string.IsNullOrEmpty(tryIt)) rows.Add(Label("<size=78%><alpha=#AA>   Try it: " + tryIt + "</size>"));   // how to try it: small and quiet
        }
        rows.Add(Label("Found a problem? Send UserData/Lycoreia/latest.log from every player.", UiColor.TextWarning));
        rows.Add(Button("Back", () => Navigate(Page.Home), alternative: true));
    }

    // ---------------------------------------------------------------- last session recap (after leaving)
    [HideFromIl2Cpp]
    private void AddRecapPage(List<UiNode> rows)
    {
        rows.Add(Heading("Last session"));
        if (CoopRuntime.LastRecap.Count == 0) rows.Add(Label("Nothing recorded yet."));
        foreach (var (name, line) in CoopRuntime.LastRecap) rows.Add(Label($"<b>{name}</b>: {line}"));
        rows.Add(Button("Back", () => Navigate(Page.Home), alternative: true));
    }

    // ---------------------------------------------------------------- co-op board
    [HideFromIl2Cpp]
    private void AddCoopPage(List<UiNode> rows)
    {
        rows.Add(Heading("Co-op board"));
        rows.Add(Tabs(
            ("Jobs", coopTab == CoopTab.Jobs, () => SetCoopTab(CoopTab.Jobs)),
            ("Notes", coopTab == CoopTab.Notes, () => SetCoopTab(CoopTab.Notes)),
            ("Expeditions", coopTab == CoopTab.Expeditions, () => SetCoopTab(CoopTab.Expeditions)),
            ("Recap", coopTab == CoopTab.Recap, () => SetCoopTab(CoopTab.Recap))));
        switch (coopTab)
        {
            case CoopTab.Jobs: AddJobsTab(rows); break;
            case CoopTab.Notes: AddNotesTab(rows); break;
            case CoopTab.Expeditions: AddExpeditionsTab(rows); break;
            default: AddRecapTab(rows); break;
        }
        rows.Add(Button("Back", () => Navigate(Page.Home), alternative: true));
    }

    private void SetCoopTab(CoopTab tab) { coopTab = tab; notice = string.Empty; Rebuild(); }

    private const string WheelHint = "<size=85%>In the world: <b>hold the middle mouse button</b> while looking at a corral, garden, silo, Gordo or any spot to pin a job or leave a note there.</size>";

    [HideFromIl2Cpp]
    private void AddJobsTab(List<UiNode> rows)
    {
        rows.Add(Label(WheelHint, UiColor.TextCategory));
        var board = CoopRuntime.Board; string me = Lycoreia.Mod.LocalId;
        var open = board.Jobs.Where(j => !j.Done).OrderBy(j => j.ClaimedBy == me ? 0 : j.ClaimedBy.Length == 0 ? 1 : 2).ThenBy(j => j.Id).Take(7).ToList();
        if (open.Count == 0) rows.Add(Label("No open jobs yet."));
        foreach (var j in open)
        {
            string state = j.ClaimedBy.Length == 0 ? "open" : j.ClaimedBy == me ? "<b>you</b>" : CoopRuntime.NameOf(j.ClaimedBy);
            rows.Add(Label($"• <b>{j.Text}</b>  <size=80%>({state}{(j.HasPosition ? ", on compass" : "")})</size>"));
            int id = j.Id;
            var actions = new List<(string, bool, Action)>();
            if (j.ClaimedBy.Length == 0) actions.Add(("I'll do it", false, () => CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.JobClaim, Id = id })));
            else if (j.ClaimedBy == me) actions.Add(("Drop it", false, () => CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.JobUnclaim, Id = id })));
            actions.Add(("Done", false, () => CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.JobDone, Id = id })));
            if (j.CreatedBy == me || Lycoreia.Mod.IsHost) actions.Add(("Remove", false, () => CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.JobRemove, Id = id })));
            rows.Add(Tabs(actions.ToArray()));
        }
        int more = board.Jobs.Count(j => !j.Done) - open.Count; if (more > 0) rows.Add(Label($"...and {more} more", UiColor.TextCategory));
        var done = board.Jobs.Where(j => j.Done).OrderByDescending(j => j.DoneUtc).Take(3).ToList();
        if (done.Count > 0) rows.Add(Label("Recently done: " + string.Join(" · ", done.Select(j => $"{j.Text} ({CoopRuntime.NameOf(j.ClaimedBy)})")), UiColor.TextCategory));
    }

    [HideFromIl2Cpp]
    private void AddNotesTab(List<UiNode> rows)
    {
        rows.Add(Label(WheelHint, UiColor.TextCategory));
        int scene = Lycoreia.Game.Avatars.RemoteAvatars.LocalSceneGroup();
        var notes = CoopRuntime.Board.Notes.OrderBy(n => n.Scene == scene ? 0 : 1).ThenByDescending(n => n.UpdatedUtc).Take(8).ToList();
        if (notes.Count == 0) rows.Add(Label("No notes yet."));
        foreach (var n in notes)
        {
            int id = n.Id;
            rows.Add(Row(Label($"• <b>{n.Text}</b>  <size=80%>{CoopRuntime.NameOf(n.Author)}{(n.Scene == scene ? "" : ", elsewhere")}</size>", width: 640f),
                Button("Remove", () => CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.NoteRemove, Id = id }), 200f, alternative: true)));
        }
        if (CoopRuntime.Board.Notes.Count > notes.Count) rows.Add(Label($"...and {CoopRuntime.Board.Notes.Count - notes.Count} more", UiColor.TextCategory));
    }

    [HideFromIl2Cpp]
    private void AddExpeditionsTab(List<UiNode> rows)
    {
        var run = CoopRuntime.Board.Expedition; var def = run != null ? Expeditions.Find(run.DefId) : null;
        int players = Math.Max(1, Lycoreia.Mod.Session?.Players.Count ?? 1);
        if (run != null && def != null && run.Outcome == ExpeditionOutcome.Running)
        {
            long left = Math.Max(0, run.EndsUtc - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            rows.Add(Label($"<b>{def.Name}</b>: {def.Goal}"));
            rows.Add(Label($"{run.Progress} / {run.Target}  ·  {left / 60}:{left % 60:00} left  ·  reward {run.Reward} newbucks"));
            if (run.ByPlayer.Count > 0) rows.Add(Label("Team: " + string.Join(", ", run.ByPlayer.OrderByDescending(p => p.Value).Select(p => $"{CoopRuntime.NameOf(p.Key)} {p.Value}")), UiColor.TextCategory));
            rows.Add(Button("Call off the expedition", () => CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.ExpeditionAbandon }), alternative: true));
            return;
        }
        if (run != null && def != null)
            rows.Add(Label(run.Outcome == ExpeditionOutcome.Succeeded ? $"Last: {def.Name} complete! (+{run.Reward} newbucks)" : $"Last: {def.Name} {run.Progress}/{run.Target} ({run.Outcome.ToString().ToLowerInvariant()})", UiColor.TextCategory));
        rows.Add(Label("Pick an expedition. Everyone's progress counts; the goal grows with the team.", UiColor.TextCategory));
        foreach (var d in CoopRuntime.Board.Offers())
        {
            int id = d.Id; var (goal, reward) = CoopRuntime.Board.Preview(d, players); int level = CoopRuntime.Board.Level(d.Id);
            rows.Add(Button($"{d.Name}{(level > 0 ? $" {level + 1}" : "")}: {d.Goal} ({goal}) in {d.Minutes} min · +{reward}", () =>
                CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.ExpeditionStart, Id = id })));
        }
        int won = CoopRuntime.Board.History.Count(h => h.Outcome == ExpeditionOutcome.Succeeded);
        if (CoopRuntime.Board.History.Count > 0) rows.Add(Label($"This ranch: {won} of {CoopRuntime.Board.History.Count} expeditions completed.", UiColor.TextCategory));
    }

    [HideFromIl2Cpp]
    private void AddRecapTab(List<UiNode> rows)
    {
        var recap = CoopRuntime.Board.Recap;
        if (recap.Count == 0) rows.Add(Label("Nothing yet this session. Go sell some plorts!", UiColor.TextCategory));
        foreach (var p in recap.OrderBy(p => p.Key == Lycoreia.Mod.LocalId ? 0 : 1)) rows.Add(Label($"<b>{CoopRuntime.NameOf(p.Key)}</b>: {RecapText.Line(p.Value)}"));
        rows.Add(Label($"Together: {CoopRuntime.Board.Total(CoopStat.PlortsSold)} plorts sold, {CoopRuntime.Board.Total(CoopStat.MoneyEarned)} newbucks earned.", UiColor.TextCategory));
    }
}
