// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Text;

namespace Lycoreia.Tests
{
    static class CoopTextTests
    {
        static void Case(string lang, string english, string line, string title, string body, CoopNoticeText.Kind kind)
        {
            var r = CoopNoticeText.Localize(lang, english, out var t, out var b, out var k);
            T.Eq(line, r, lang + " line of '" + english + "'"); T.Eq(title, t, lang + " title"); T.Eq(body, b, lang + " body"); T.Eq(kind, k, lang + " kind");
        }

        public static void Register()
        {
            T.Test("coop text/normalize: locale codes map to two-letter languages", () =>
            {
                T.Eq("ru", CoopText.Normalize("ru-RU"), "ru-RU"); T.Eq("ru", CoopText.Normalize("ru_RU"), "ru_RU");
                T.Eq("ru", CoopText.Normalize("Russian (ru)"), "Russian (ru)"); T.Eq("ru", CoopText.Normalize("rus"), "rus");
                T.Eq("en", CoopText.Normalize(""), "empty"); T.Eq("en", CoopText.Normalize(null), "null"); T.Eq("de", CoopText.Normalize("de-DE"), "de-DE");
                T.Eq("en", CoopText.Normalize("English (United States)"), "English");
            });
            T.Test("coop text/pickfor: ru and es only for their language and when the text exists", () =>
            {
                T.Eq("привет", CoopText.PickFor("ru", "hi", "привет", "hola"), "ru"); T.Eq("hi", CoopText.PickFor("ru", "hi", "", "hola"), "empty ru falls back");
                T.Eq("hi", CoopText.PickFor("ru", "hi", null, "hola"), "null ru falls back"); T.Eq("hi", CoopText.PickFor("en", "hi", "привет", "hola"), "en");
                T.Eq("hola", CoopText.PickFor("es", "hi", "привет", "hola"), "es"); T.Eq("hi", CoopText.PickFor("es", "hi", "привет", ""), "empty es falls back");
                T.Eq("hi", CoopText.PickFor("de", "hi", "привет", "hola"), "other language");
                T.Eq("es", CoopText.Normalize("es-419"), "es-419"); T.Eq("es", CoopText.Normalize("es-ES"), "es-ES");
            });
            T.Test("coop text/pick: follows the forced test language", () =>
            {
                try { CoopText.SetLangForTests("ru"); T.Eq("да", CoopText.Pick("yes", "да", "sí"), "ru"); T.Eq("ru", CoopText.Lang, "lang"); CoopText.SetLangForTests("en"); T.Eq("yes", CoopText.Pick("yes", "да", "sí"), "en"); CoopText.SetLangForTests("es"); T.Eq("sí", CoopText.Pick("yes", "да", "sí"), "es"); }
                finally { CoopText.SetLangForTests(null); }
            });
            T.Test("coop text/pick: English when the language's script cannot be drawn (rc3 blank-glyph bug)", () =>
            {
                try
                {
                    CoopText.SetLangForTests("ru");
                    CoopText.CanRender = l => false; T.Eq("yes", CoopText.Pick("yes", "да", "sí"), "no cyrillic font -> en"); T.Eq("en", CoopText.RenderLang(), "render lang");
                    CoopText.CanRender = l => throw new System.Exception("x"); T.Eq("yes", CoopText.Pick("yes", "да", "sí"), "hook throws -> en");
                    CoopText.CanRender = l => true; T.Eq("да", CoopText.Pick("yes", "да", "sí"), "font ok -> ru");
                }
                finally { CoopText.CanRender = null; CoopText.SetLangForTests(null); }
            });
            T.Test("coop notice/en: every rule, title/body/kind", () =>
            {
                var I = CoopNoticeText.Kind.Info;
                Case("en", "Bo pinned a job: Feed 5 pinks", "Bo pinned a job: Feed 5 pinks", "Bo pinned a job", "Feed 5 pinks", I);
                Case("en", "Bo is on it: Feed 5 pinks", "Bo is on it: Feed 5 pinks", "Bo is on it", "Feed 5 pinks", I);
                Case("en", "Bo finished: Feed 5 pinks", "Bo finished: Feed 5 pinks", "Bo finished", "Feed 5 pinks", I);
                Case("en", "Bo left a note: gone fishing", "Bo left a note: gone fishing", "Bo left a note", "gone fishing", I);
                Case("en", "Bo started the expedition Dash: Collect 50 carrots (50) within 20 minutes!", "Bo started the expedition Dash: Collect 50 carrots (50) within 20 minutes!", "Bo started the expedition Dash", "Collect 50 carrots (50) within 20 minutes!", I);
                Case("en", "Bo called off the expedition.", "Bo called off the expedition.", "Bo called off the expedition.", null, I);
                Case("en", "Dash: halfway there (25/50)!", "Dash: halfway there (25/50)!", "Dash", "halfway there (25/50)!", I);
                Case("en", "Expedition Dash complete! The ranch earns 500 newbucks.", "Expedition Dash complete! The ranch earns 500 newbucks.", "Expedition Dash complete! The ranch earns 500 newbucks.", null, CoopNoticeText.Kind.Success);
                Case("en", "Time is up: Dash ended at 30/50.", "Time is up: Dash ended at 30/50.", "Time is up: Dash ended at 30/50.", null, CoopNoticeText.Kind.Fail);
            });
            T.Test("coop notice/ru: every rule is translated, with the same split", () =>
            {
                var I = CoopNoticeText.Kind.Info;
                Case("ru", "Bo pinned a job: Feed 5 pinks", "Новое задание от Bo: Feed 5 pinks", "Новое задание от Bo", "Feed 5 pinks", I);
                Case("ru", "Bo is on it: X", "Bo берётся за дело: X", "Bo берётся за дело", "X", I);
                Case("ru", "Bo finished: X", "Bo закрывает задание: X", "Bo закрывает задание", "X", I);
                Case("ru", "Bo left a note: hi", "Заметка от Bo: hi", "Заметка от Bo", "hi", I);
                Case("ru", "Bo started the expedition Dash: Collect (50) within 20 minutes!", "Bo запускает экспедицию «Dash»: Collect (50) за 20 мин!", "Bo запускает экспедицию «Dash»", "Collect (50) за 20 мин!", I);
                Case("ru", "Bo called off the expedition.", "Bo отменяет экспедицию.", "Bo отменяет экспедицию.", null, I);
                Case("ru", "Dash: halfway there (25/50)!", "Dash: половина позади (25/50)!", "Dash", "половина позади (25/50)!", I);
                Case("ru", "Expedition Dash complete! The ranch earns 500 newbucks.", "Экспедиция «Dash» завершена! Ранчо получает нью-баксы: 500.", "Экспедиция «Dash» завершена! Ранчо получает нью-баксы: 500.", null, CoopNoticeText.Kind.Success);
                Case("ru", "Time is up: Dash ended at 30/50.", "Время вышло: экспедиция «Dash» остановилась на 30/50.", "Время вышло: экспедиция «Dash» остановилась на 30/50.", null, CoopNoticeText.Kind.Fail);
                Case("de", "Bo finished: X", "Bo finished: X", "Bo finished", "X", I);
            });
            T.Test("coop notice: unmatched lines and empty input pass through", () =>
            {
                Case("ru", "Something else entirely", "Something else entirely", "Something else entirely", null, CoopNoticeText.Kind.Info);
                var r = CoopNoticeText.Localize("ru", "", out var t, out var b, out var k); T.Eq("", r, "empty"); T.Eq(null, t, "title"); T.Eq("", b, "body");
                T.Eq("Hello", CoopNoticeText.Localize("ru", "Hello"), "short form");
            });
        }
    }
}
