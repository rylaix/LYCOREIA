// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections.Generic;

namespace Lycoreia.Text
{
    public static class WheelText
    {
        static readonly Dictionary<string, string> Ru = new Dictionary<string, string>
        {
            // ready-made jobs (CoopWheelRules presets)
            ["Feed this corral"] = "Накормить этот загон", ["Collect the plorts here"] = "Собрать здесь плорты", ["Upgrade this corral"] = "Улучшить этот загон",
            ["Feed the chickens here"] = "Покормить здесь куриц", ["Collect the chickens here"] = "Собрать здесь куриц",
            ["Harvest this garden"] = "Собрать урожай в саду", ["Plant something here"] = "Посадить здесь что-нибудь",
            ["Empty this silo"] = "Опустошить этот силос", ["Fill this silo"] = "Наполнить этот силос",
            ["Check on the pond"] = "Проверить пруд", ["Burn the leftovers"] = "Сжечь остатки", ["Build something here"] = "Построить здесь что-нибудь",
            ["Feed this Gordo"] = "Накормить этого гордо", ["Meet me here"] = "Встретимся здесь", ["Explore over here"] = "Исследуем здесь",
            // ready-made notes
            ["Don't feed these slimes"] = "Не корми этих слаймов", ["Keep this corral for largos"] = "Этот загон для ларго",
            ["Leave the chickens to breed"] = "Пусть куры размножаются", ["Leave this crop alone"] = "Не трогай эти растения",
            ["Plorts for selling"] = "Эти плорты на продажу", ["Puddle slimes only"] = "Только лужицы-слаймы", ["Ash for the puddles"] = "Пепел для лужиц",
            ["Reserved"] = "Занято", ["Feeding this one: don't pop it"] = "Кормим его, не дай ему лопнуть", ["Look here!"] = "Смотри сюда!",
            // wheel entries
            ["write your own..."] = "написать своё...", ["Remove the note"] = "Убрать заметку",
        };

        static readonly (string en, string ru)[] Prefixes = { ("Job: ", "Задание: "), ("Note: ", "Заметка: "), ("I'll do: ", "Берусь: "), ("I'll do it: ", "Беру: "), ("Drop: ", "Отказаться: "), ("Remove job: ", "Убрать задание: "), ("Done: ", "Готово: ") };

        /// <summary>Wheel entry label (Core builds it in English) in the given language; player-written text is never translated.</summary>
        public static string Label(string lang, string english)
        {
            if (lang != "ru" || string.IsNullOrEmpty(english)) return english;
            if (Ru.TryGetValue(english, out var whole)) return whole;
            foreach (var (en, ru) in Prefixes)
                if (english.StartsWith(en, System.StringComparison.Ordinal))
                {
                    var rest = english.Substring(en.Length);
                    // a translated preset reads lowercase after the colon; player-written text is left exactly as typed
                    return ru + (Ru.TryGetValue(rest, out var r) ? LowerFirst(r) : rest);
                }
            return english;
        }

        static string LowerFirst(string t) => t.Length > 0 && char.IsUpper(t[0]) && !(t.Length > 1 && char.IsUpper(t[1])) ? char.ToLowerInvariant(t[0]) + t.Substring(1) : t;

        public static string Label(string english) => Label(CoopText.RenderLang(), english);

        public static string ClaimedBy(string name) => CoopText.Pick("Taken by " + name, "Взял: " + name);
        public static string Unclaimed() => CoopText.Pick("Nobody has taken it", "Пока никто не взял");
        public static string NoteBy(string name) => CoopText.Pick("Note by " + name, "Заметка: " + name);
        public static string You() => CoopText.Pick("you", "ты");
        public static string Title() => CoopText.Pick("Co-op", "Вместе");
        public static string ReleaseHint(string glyph) => CoopText.Pick("Release " + glyph + " on an option to choose it, or in the centre to cancel", "Отпусти " + glyph + " на пункте, чтобы выбрать, или в центре, чтобы отменить");
        public static string PadReleaseHint(string glyph) => CoopText.Pick("Release " + glyph + " to choose, right stick to aim", "Выбирай правым стиком и отпусти " + glyph);
        public static string NewJobAt(string place) => CoopText.Pick("New job at " + place, "Новое задание: " + place);
        public static string NoteFor(string place) => CoopText.Pick("Note for " + place, "Заметка: " + place);
        public static string BoxHint() => CoopText.Pick("Enter to save, Esc to cancel", "Enter: сохранить, Esc: отмена");
    }
}
