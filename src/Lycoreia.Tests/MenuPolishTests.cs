// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using Lycoreia.Coop;
using Lycoreia.Text;

namespace Lycoreia.Tests
{
    static class MenuPolishTests
    {
        public static void Register()
        {
            T.Test("menu polish/whats new gate: once per version, never on a fresh install", () =>
            {
                T.Eq(WhatsNewDecision.Show, WhatsNewGate.Decide("2.0.9", "2.1.0-rc3", false), "upgrade shows");
                T.Eq(WhatsNewDecision.Show, WhatsNewGate.Decide(null, "2.1.0-rc3", false), "no record but not fresh (2.0 user) shows");
                T.Eq(WhatsNewDecision.MarkSilently, WhatsNewGate.Decide(null, "2.1.0-rc3", true), "fresh install is only recorded");
                T.Eq(WhatsNewDecision.MarkSilently, WhatsNewGate.Decide("", "2.1.0-rc3", true), "empty record on a fresh install");
                T.Eq(WhatsNewDecision.Nothing, WhatsNewGate.Decide("2.1.0-rc3", "2.1.0-rc3", false), "same version");
                T.Eq(WhatsNewDecision.Nothing, WhatsNewGate.Decide("2.0", "", false), "no current version");
                T.Eq(WhatsNewDecision.Show, WhatsNewGate.Decide("2.1.0-rc2", "2.1.0-rc3", true), "a record beats the fresh flag");
            });
            T.Test("menu polish/seen flags: round trip through a file, tolerant of junk", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "lyc-seen-" + Guid.NewGuid().ToString("N") + ".txt");
                try
                {
                    SeenFlags.Configure(path);
                    T.True(SeenFlags.Get("hint.wheel") == null, "unset"); T.True(!SeenFlags.Has("hint.wheel"), "has before");
                    SeenFlags.Set("hint.wheel", "1"); SeenFlags.Set("whatsnew.version", "2.1.0-rc3");
                    SeenFlags.Configure(path);                                   // forget memory, read the file again
                    T.Eq("1", SeenFlags.Get("hint.wheel"), "wheel"); T.Eq("2.1.0-rc3", SeenFlags.Get("whatsnew.version"), "version"); T.True(SeenFlags.Has("hint.wheel"), "has");
                    var m = SeenFlags.Parse("a=1\r\n\r\njunk\n=x\n b = two words \n"); T.Eq(2, m.Count, "junk skipped"); T.Eq("1", m["a"], "a"); T.Eq("two words", m["b"], "b trimmed");
                    File.WriteAllText(path, "\x01 not a flag file");
                    SeenFlags.Configure(path); T.True(SeenFlags.Get("hint.wheel") == null, "garbage reads as empty");
                }
                finally { SeenFlags.Configure(null); try { File.Delete(path); } catch { } }
            });
            T.Test("menu polish/job kinds: anchor and wording pick the icon", () =>
            {
                T.Eq("gordo", JobKinds.Of("gordo:abc", "Feed this Gordo", true), "gordo anchor");
                T.Eq("silo", JobKinds.Of("plot:1", "Empty this silo", true), "silo");
                T.Eq("coop", JobKinds.Of("plot:2", "Collect the chickens here", true), "chickens before corral");
                T.Eq("garden", JobKinds.Of("plot:3", "Harvest this garden", true), "garden");
                T.Eq("corral", JobKinds.Of("plot:4", "Feed this corral", true), "corral");
                T.Eq("incinerator", JobKinds.Of("plot:5", "Burn the leftovers", true), "incinerator");
                T.Eq("pond", JobKinds.Of("plot:6", "Check on the pond", true), "pond");
                T.Eq("plot", JobKinds.Of("plot:7", "Build something here", true), "plot by anchor");
                T.Eq("spot", JobKinds.Of("", "Meet me here", true), "spot");
                T.Eq("generic", JobKinds.Of("", "Anything", false), "generic"); T.Eq("generic", JobKinds.Of(null, null, false), "nulls");
                foreach (var k in JobKinds.All) T.True(!string.IsNullOrEmpty(k), "kind name");
            });
            T.Test("menu polish/release notes: Russian and Spanish lists have the English shape, titles are short", () =>
            {
                foreach (var other in new[] { WhatsNew.SectionsRu, WhatsNew.SectionsEs })
                {
                    T.Eq(WhatsNew.Sections.Count, other.Count, "sections");
                    for (int i = 0; i < WhatsNew.Sections.Count; i++)
                    {
                        T.True(!string.IsNullOrEmpty(other[i].Tab) && other[i].Tab.Length <= 18, "short tab name " + other[i].Tab);
                        T.Eq(WhatsNew.Sections[i].Items.Count, other[i].Items.Count, "items of section " + i);
                        for (int j = 0; j < WhatsNew.Sections[i].Items.Count; j++)
                        {
                            T.Eq(WhatsNew.Sections[i].Items[j].TryIt.Length == 0, other[i].Items[j].TryIt.Length == 0, "try-it presence " + i + "/" + j);
                            T.True(other[i].Items[j].Title.Length <= 28 && other[i].Items[j].Title.Split(' ').Length <= 4, "card title stays short: " + other[i].Items[j].Title);
                            T.True(other[i].Items[j].Text != WhatsNew.Sections[i].Items[j].Text, "translated: " + other[i].Items[j].Text);
                        }
                    }
                }
                T.True(ReferenceEquals(WhatsNew.Sections, WhatsNew.For("en")), "en"); T.True(ReferenceEquals(WhatsNew.Sections, WhatsNew.For("de")), "other");
                T.True(ReferenceEquals(WhatsNew.SectionsRu, WhatsNew.For("ru")), "ru"); T.True(ReferenceEquals(WhatsNew.SectionsEs, WhatsNew.For("es")), "es");
            });
            T.Test("menu polish/every menu string is translated to Spanish and Russian", () =>
            {
                // A string that stays English in another language is a missed translation; these few are the same in every language.
                var same = new HashSet<string> { "Port", "IP" };
                try
                {
                    var props = typeof(MenuText).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Where(p => p.PropertyType == typeof(string)).ToList();
                    CoopText.SetLangForTests("en"); var en = props.ToDictionary(p => p.Name, p => (string)p.GetValue(null));
                    foreach (var lang in new[] { "ru", "es" })
                    {
                        CoopText.SetLangForTests(lang);
                        foreach (var p in props) { var v = (string)p.GetValue(null); if (!same.Contains(en[p.Name])) T.True(v != en[p.Name], lang + " MenuText." + p.Name + " is still English: " + v); }
                    }
                    CoopText.SetLangForTests("es");
                    T.Eq("Alimentar este corral", WheelText.Label("es", "Feed this corral"), "wheel es"); T.Eq("Tarea: alimentar este corral", WheelText.Label("es", "Job: Feed this corral"), "wheel es prefix");
                    T.Eq("Tarea: my own text", WheelText.Label("es", "Job: my own text"), "player text untouched");
                    T.Eq("Tomada por Bo", WheelText.ClaimedBy("Bo"), "claimed es");
                    T.Eq("¡Expedición Fiebre del Plort completada! El rancho gana 1500 newbucks.", CoopNoticeText.Localize("es", "Expedition Plort Rush complete! The ranch earns 1500 newbucks."), "notice es with the expedition name translated");
                    T.Eq("Экспедиция «Плортовая лихорадка» завершена! Ранчо получает нью-баксы: 1500.", CoopNoticeText.Localize("ru", "Expedition Plort Rush complete! The ranch earns 1500 newbucks."), "notice ru with the expedition name translated");
                }
                finally { CoopText.SetLangForTests(null); }
            });
            T.Test("menu polish/board strings: language follows CoopText, 'ago' steps, recap line", () =>
            {
                try
                {
                    CoopText.SetLangForTests("en");
                    T.Eq("just now", MenuText.Ago(5), "now"); T.Eq("5 min ago", MenuText.Ago(300), "min"); T.Eq("3 h ago", MenuText.Ago(3 * 3600 + 5), "h"); T.Eq("2 d ago", MenuText.Ago(2 * 86400), "d");
                    T.Eq("done by Bo, 5 min ago", MenuText.DoneBy("Bo", MenuText.Ago(300)), "done by");
                    T.True(MenuText.JobsEmptyHint("[MMB]").Contains("[MMB]"), "glyph in the empty hint");
                    T.Eq("nothing yet", MenuText.RecapLine(null), "empty recap");
                    var stats = new Dictionary<CoopStat, long> { [CoopStat.PlortsSold] = 3, [CoopStat.JobsDone] = 1 };
                    T.Eq("3 plorts sold, 1 jobs done", MenuText.RecapLine(stats), "recap en");
                    T.Eq("Plort Rush", MenuText.ExpName(Expeditions.Find(1)), "expedition en");
                    CoopText.SetLangForTests("ru");
                    T.Eq("только что", MenuText.Ago(5), "now ru"); T.Eq("5 мин назад", MenuText.Ago(300), "min ru");
                    T.Eq("пока ничего", MenuText.RecapLine(null), "empty recap ru"); T.Eq("продано плортов: 3, выполнено заданий: 1", MenuText.RecapLine(stats), "recap ru");
                    foreach (var d in Expeditions.Catalog) { T.True(MenuText.ExpName(d) != d.Name && MenuText.ExpGoal(d) != d.Goal, "expedition " + d.Id + " translated"); }
                    T.True(MenuText.HintWheel("[X]").Contains("[X]") && MenuText.HintBoost.Length > 0, "hints");
                }
                finally { CoopText.SetLangForTests(null); }
            });
            T.Test("menu polish/no long dashes in any user text, en or ru", () =>
            {
                bool NoDash(string v) => v != null && v.IndexOf((char)0x2014) < 0 && v.IndexOf((char)0x2013) < 0;
                try
                {
                    foreach (var lang in new[] { "en", "ru", "es" })
                    {
                        CoopText.SetLangForTests(lang);
                        foreach (var p in typeof(MenuText).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                            if (p.PropertyType == typeof(string)) T.True(NoDash((string)p.GetValue(null)), lang + " MenuText." + p.Name + " has a long dash");
                        foreach (var sec in WhatsNew.For(lang))
                        {
                            T.True(NoDash(sec.Title), "title dash");
                            foreach (var it in sec.Items) T.True(NoDash(it.Title + it.Text + it.TryIt), lang + " whats-new dash: " + it.Text);
                        }
                        T.True(NoDash(WheelText.Label(lang, "Look here!")), "wheel dash");
                    }
                    CoopText.SetLangForTests("ru");
                    T.Eq("2 нью-бакса", "2 " + MenuText.Newbucks(2), "plural few"); T.Eq("21 нью-бакс", "21 " + MenuText.Newbucks(21), "plural one"); T.Eq("11 нью-баксов", "11 " + MenuText.Newbucks(11), "plural teens"); T.Eq("500 нью-баксов", "500 " + MenuText.Newbucks(500), "plural many");
                }
                finally { CoopText.SetLangForTests(null); }
            });
            T.Test("menu polish/update texts: banner message and toast from the status", () =>
            {
                try
                {
                    CoopText.SetLangForTests("en");
                    var u = new UpdateStatus { Kind = UpdateKind.UpdateAvailable, Installed = "2.1.0", Newest = "2.2.0", Message = "x" };
                    u.Offering.Add(new PublishedVersion("GitHub", "2.2.0", "https://example.invalid/g")); u.Offering.Add(new PublishedVersion("Nexus Mods", "2.2.0", "https://example.invalid/n"));
                    T.Eq("Lycoreia 2.2.0 is available (you have 2.1.0). Download it from GitHub or Nexus Mods. Every player in a session needs the same version.", MenuText.UpdateMessage(u), "message");
                    MenuText.UpdateToast(u, out var title, out var body); T.Eq("Update available", title, "toast title"); T.True(body.Contains("2.2.0") && body.Contains("2.1.0"), "toast body");
                    var nover = new UpdateStatus { Kind = UpdateKind.UpdateAvailable, Message = "own text" }; T.Eq("own text", MenuText.UpdateMessage(nover), "no version falls back to the status text");
                    var ahead = new UpdateStatus { Kind = UpdateKind.AheadOfPublic, Installed = "2.2.0", Newest = "2.1.0" }; MenuText.UpdateToast(ahead, out title, out body); T.Eq("Newer than the public release", title, "ahead title");
                    CoopText.SetLangForTests("ru"); MenuText.UpdateToast(u, out title, out body); T.Eq("Доступно обновление", title, "ru title");
                }
                finally { CoopText.SetLangForTests(null); }
            });
        }
    }
}
