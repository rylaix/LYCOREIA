// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Lycoreia.Text
{
    public static class CoopText
    {
        static string _lang = "en"; static double _checkedAt = -100; static bool _logged; static string _override;
        static readonly Dictionary<string, string> _nativeCache = new Dictionary<string, string>();
        static readonly HashSet<string> _nativeMiss = new HashSet<string>();

        /// <summary>Two-letter language of the game's current locale ("en", "ru", ...), "en" when it cannot be read. Re-checked every 2 s, so it follows a language change.</summary>
        /// <summary>Set once by the game layer on the main thread; off that thread Lang returns the cached value (the lookup calls Unity).</summary>
        public static int MainThreadId;
        public static string Lang
        {
            get
            {
                if (_override != null) return _override;
                if (MainThreadId != 0 && System.Environment.CurrentManagedThreadId != MainThreadId) return _lang;
                double now = Environment.TickCount64 / 1000.0;
                if (now - _checkedAt < 2) return _lang;
                _checkedAt = now;
                string code = null;
                try { code = NativeLocaleCode(); } catch (Exception e) { if (!_logged) { _logged = true; Log.Warn("[text] locale not readable, using en: " + e.Message); } }
                var lang = Normalize(code);
                if (lang != _lang) { Log.Info($"[text] language {_lang} -> {lang} (locale '{code ?? "?"}')"); _lang = lang; _nativeCache.Clear(); }
                return _lang;
            }
        }

        /// <summary>Tests only: forces a language (null releases it).</summary>
        public static void SetLangForTests(string lang) { _override = lang == null ? null : Normalize(lang); }

        /// <summary>"ru-RU" / "ru_RU" / "Russian (ru)" -> "ru"; unknown or empty -> "en".</summary>
        public static string Normalize(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "en";
            var c = code.Trim().ToLowerInvariant();
            if (c.StartsWith("russian")) return "ru";
            if (c.StartsWith("english")) return "en";
            var m = Regex.Match(c, "^[a-z]{2,3}");
            if (!m.Success) return "en";
            var l = m.Value; return l.Length == 2 ? l : (l == "rus" ? "ru" : (l == "eng" ? "en" : "en"));
        }

        /// <summary>The English text, or the Russian one when the game language is Russian (and ru is given).</summary>
        /// <summary>
        /// Set by the UI layer: true when text of that language can be drawn (fonts have its glyphs). When it says no, Pick falls back to
        /// English instead of drawing blank glyphs (rc3: Russian text rendered with a Latin-only font). Null = assume yes (tests).
        /// </summary>
        public static Func<string, bool> CanRender;
        static bool _renderMissLogged;

        /// <summary>The text in the game's language: English, Russian or Spanish (rc6). Every call gives all three, so nothing falls back silently.</summary>
        public static string Pick(string en, string ru, string es) => PickFor(RenderLang(), en, ru, es);

        /// <summary>The language Pick actually uses: Lang, or "en" when its script cannot be drawn.</summary>
        public static string RenderLang()
        {
            var lang = Lang;
            if (lang == "en" || CanRender == null) return lang;
            bool ok;
            try { ok = CanRender(lang); } catch { ok = false; }
            if (ok) return lang;
            if (!_renderMissLogged) { _renderMissLogged = true; Log.Warn($"[text] no font for '{lang}' yet, co-op text in English"); }
            return "en";
        }

        public static string PickFor(string lang, string en, string ru, string es) =>
            lang == "ru" && !string.IsNullOrEmpty(ru) ? ru : lang == "es" && !string.IsNullOrEmpty(es) ? es : en;
        /// <summary>The languages the mod speaks; anything else is shown in English.</summary>
        public static readonly string[] Supported = { "en", "ru", "es" };

        /// <summary>
        /// A word of the game's own string tables, "TableName/EntryKey" (e.g. "UI/m_key_foo"), localised to the current game language;
        /// <paramref name="fallback"/> when the table, key or localization system is unavailable. Results are cached per language; a miss is remembered.
        /// </summary>
        public static string Native(string tableAndKey, string fallback)
        {
            if (string.IsNullOrEmpty(tableAndKey) || _nativeMiss.Contains(tableAndKey)) return fallback;
            if (_nativeCache.TryGetValue(tableAndKey, out var hit)) return hit;
            try
            {
                var v = NativeLookup(tableAndKey);
                if (!string.IsNullOrEmpty(v)) { _nativeCache[tableAndKey] = v; return v; }
            }
            catch (Exception e) { Log.Warn($"[text] native string {tableAndKey} failed: {e.Message}"); }
            _nativeMiss.Add(tableAndKey); Log.Info($"[text] native string {tableAndKey} not found, using fallback");
            return fallback;
        }

        // Il2Cpp-touching parts are separate methods so the pure parts above stay testable without the game.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static string NativeLocaleCode()
        {
            var locale = UnityEngine.Localization.Settings.LocalizationSettings.SelectedLocale;
            return locale != null ? locale.Identifier.Code : null;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static string NativeLookup(string tableAndKey)
        {
            int slash = tableAndKey.IndexOf('/'); if (slash <= 0 || slash == tableAndKey.Length - 1) return null;
            var db = UnityEngine.Localization.Settings.LocalizationSettings.StringDatabase; if (db == null) return null;
            var table = db.GetTable(UnityEngine.Localization.Tables.TableReference.TableReferenceFromString(tableAndKey.Substring(0, slash))); if (table == null) return null;
            var entry = table.GetEntry(tableAndKey.Substring(slash + 1)); return entry?.GetLocalizedString();
        }
    }

    /// <summary>
    /// Re-renders the host's English co-op board notices ("Bo pinned a job: ...") in the local game language. The wire carries the English line
    /// (every seat runs the same build), so each seat translates it for itself. Unknown lines pass through unchanged.
    /// Russian: present tense and "Name: text" patterns, so no gendered verb forms.
    /// </summary>
    public static class CoopNoticeText
    {
        static readonly (Regex re, string en, string ru, string es)[] Rules =
        {
            (new Regex("^(.+?) pinned a job: (.*)$", RegexOptions.Singleline), "{0} pinned a job: {1}", "Новое задание от {0}: {1}", "{0} fijó una tarea: {1}"),
            (new Regex("^(.+?) is on it: (.*)$", RegexOptions.Singleline), "{0} is on it: {1}", "{0} берётся за дело: {1}", "{0} se encarga: {1}"),
            (new Regex("^(.+?) finished: (.*)$", RegexOptions.Singleline), "{0} finished: {1}", "{0} закрывает задание: {1}", "{0} terminó: {1}"),
            (new Regex("^(.+?) left a note: (.*)$", RegexOptions.Singleline), "{0} left a note: {1}", "Заметка от {0}: {1}", "{0} dejó una nota: {1}"),
            (new Regex("^(.+?) started the expedition (.+?): (.*) \\((\\d+)\\) within (\\d+) minutes!$", RegexOptions.Singleline), "{0} started the expedition {1}: {2} ({3}) within {4} minutes!", "{0} запускает экспедицию «{1}»: {2} ({3}) за {4} мин!", "¡{0} inició la expedición {1}: {2} ({3}) en {4} minutos!"),
            (new Regex("^(.+?) called off the expedition\\.$"), "{0} called off the expedition.", "{0} отменяет экспедицию.", "{0} canceló la expedición."),
            (new Regex("^(.+?): halfway there \\((\\d+)/(\\d+)\\)!$"), "{0}: halfway there ({1}/{2})!", "{0}: половина позади ({1}/{2})!", "{0}: ¡a mitad de camino ({1}/{2})!"),
            (new Regex("^Expedition (.+?) complete! The ranch earns (\\d+) newbucks\\.$"), "Expedition {0} complete! The ranch earns {1} newbucks.", "Экспедиция «{0}» завершена! Ранчо получает нью-баксы: {1}.", "¡Expedición {0} completada! El rancho gana {1} newbucks."),
            (new Regex("^Time is up: (.+?) ended at (\\d+)/(\\d+)\\.$"), "Time is up: {0} ended at {1}/{2}.", "Время вышло: экспедиция «{0}» остановилась на {1}/{2}.", "Se acabó el tiempo: {0} terminó en {1}/{2}."),
        };

        public enum Kind { Info, Success, Fail }

        /// <summary>Expedition names (the wire and the board defs carry the English name). One table for the board and the notices.</summary>
        static readonly System.Collections.Generic.Dictionary<string, (string ru, string es)> ExpeditionNames = new()
        {
            ["Plort Rush"] = ("Плортовая лихорадка", "Fiebre del Plort"), ["Wild Roundup"] = ("Охота на слаймов", "Gran Redada"),
            ["Gordo Feast"] = ("Пир для гордо", "Festín de Gordos"), ["Harvest Run"] = ("Сбор урожая", "Gran Cosecha"),
            ["Treasure Trail"] = ("Тропа сокровищ", "Ruta del Tesoro"), ["Market Day"] = ("Базарный день", "Día de Mercado"),
            // goals
            ["Sell plorts at the Plort Market"] = ("Продавай плорты на плорт-маркете", "Vende plorts en el Mercado de Plorts"),
            ["Vacuum up slimes"] = ("Лови слаймов вакпаком", "Aspira slimes"), ["Feed Gordo slimes"] = ("Корми гордо", "Alimenta a los Gordos"),
            ["Vacuum up fruit, veggies and meat"] = ("Собирай фрукты, овощи и мясо", "Aspira frutas, verduras y carne"),
            ["Open treasure pods"] = ("Открывай сокровищницы", "Abre vainas de tesoro"), ["Earn newbucks from sales"] = ("Зарабатывай нью-баксы на продажах", "Gana newbucks con las ventas"),
        };
        public static string ExpeditionName(string lang, string english) =>
            english != null && ExpeditionNames.TryGetValue(english, out var t) ? CoopText.PickFor(lang, english, t.ru, t.es) : english;

        /// <summary>Localises an English board notice for <paramref name="lang"/>; returns the input when no rule matches.</summary>
        public static string Localize(string lang, string english, out string title, out string body, out Kind kind)
        {
            title = null; body = english; kind = Kind.Info;
            if (string.IsNullOrEmpty(english)) return english;
            foreach (var (re, en, ru, es) in Rules)
            {
                var m = re.Match(english); if (!m.Success) continue;
                var args = new object[m.Groups.Count - 1]; for (int i = 1; i < m.Groups.Count; i++) args[i - 1] = ExpeditionName(lang, m.Groups[i].Value);
                var line = string.Format(CoopText.PickFor(lang, en, ru, es), args);
                // toast = short headline + detail: split at the first ": " the template itself provides
                int cut = line.IndexOf(": ", StringComparison.Ordinal);
                if (cut > 0 && cut < line.Length - 2 && !en.StartsWith("Expedition ") && !en.StartsWith("Time is up")) { title = line.Substring(0, cut); body = line.Substring(cut + 2); }
                else { title = line; body = null; }
                if (en.StartsWith("Expedition ")) kind = Kind.Success; else if (en.StartsWith("Time is up")) kind = Kind.Fail;
                return line;
            }
            title = english; body = null; return english;
        }

        public static string Localize(string lang, string english) => Localize(lang, english, out _, out _, out _);
    }
}
