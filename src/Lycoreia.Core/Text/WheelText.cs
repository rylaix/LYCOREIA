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
            ["Feed this Gordo"] = "Накормить этого гордо", ["Meet me here"] = "Встретимся здесь", ["Explore over here"] = "Исследовать тут",
            // ready-made notes
            ["Don't feed these slimes"] = "Не корми этих слаймов", ["Keep this corral for largos"] = "Этот загон для ларго",
            ["Leave the chickens to breed"] = "Пусть куры размножаются", ["Leave this crop alone"] = "Не трогай эти растения",
            ["Plorts for selling"] = "Эти плорты на продажу", ["Puddle slimes only"] = "Только для слаймов-лужиц", ["Ash for the puddles"] = "Пепел для лужиц",
            ["Reserved"] = "Занято", ["Feeding this one: don't pop it"] = "Кормим, не дай ему лопнуть", ["Look here!"] = "Смотри сюда!",
            // wheel entries
            ["write your own..."] = "написать своё...", ["Remove the note"] = "Убрать заметку",
        };

        // Spanish: the game's own words (Corral, Jardín, Silo, Estanque, Gordo, Slime Charco), informal "tú".
        static readonly Dictionary<string, string> Es = new Dictionary<string, string>
        {
            ["Feed this corral"] = "Alimentar este corral", ["Collect the plorts here"] = "Recoger los plorts de aquí", ["Upgrade this corral"] = "Mejorar este corral",
            ["Feed the chickens here"] = "Alimentar a las gallinas de aquí", ["Collect the chickens here"] = "Recoger las gallinas de aquí",
            ["Harvest this garden"] = "Cosechar este jardín", ["Plant something here"] = "Plantar algo aquí",
            ["Empty this silo"] = "Vaciar este silo", ["Fill this silo"] = "Llenar este silo",
            ["Check on the pond"] = "Revisar el estanque", ["Burn the leftovers"] = "Quemar los restos", ["Build something here"] = "Construir algo aquí",
            ["Feed this Gordo"] = "Alimentar a este Gordo", ["Meet me here"] = "Nos vemos aquí", ["Explore over here"] = "Explorar por aquí",
            ["Don't feed these slimes"] = "No alimentes a estos slimes", ["Keep this corral for largos"] = "Este corral es para largos",
            ["Leave the chickens to breed"] = "Deja que las gallinas se reproduzcan", ["Leave this crop alone"] = "No toques este cultivo",
            ["Plorts for selling"] = "Plorts para vender", ["Puddle slimes only"] = "Solo slimes charco", ["Ash for the puddles"] = "Ceniza para los charcos",
            ["Reserved"] = "Reservado", ["Feeding this one: don't pop it"] = "Lo estamos alimentando: no lo revientes", ["Look here!"] = "¡Mira aquí!",
            ["write your own..."] = "escribir la tuya...", ["Remove the note"] = "Quitar la nota",
        };

        static readonly (string en, string ru, string es)[] Prefixes = { ("Job: ", "Задание: ", "Tarea: "), ("Note: ", "Заметка: ", "Nota: "), ("I'll do: ", "Берусь: ", "Me encargo: "), ("I'll do it: ", "Беру: ", "Me encargo: "), ("Drop: ", "Отказаться: ", "Dejar: "), ("Remove job: ", "Убрать задание: ", "Quitar tarea: "), ("Done: ", "Готово: ", "Hecho: ") };

        /// <summary>Wheel entry label (Core builds it in English) in the given language; player-written text is never translated.</summary>
        public static string Label(string lang, string english)
        {
            if ((lang != "ru" && lang != "es") || string.IsNullOrEmpty(english)) return english;
            var table = lang == "ru" ? Ru : Es;
            if (table.TryGetValue(english, out var whole)) return whole;
            foreach (var (en, ru, es) in Prefixes)
                if (english.StartsWith(en, System.StringComparison.Ordinal))
                {
                    var rest = english.Substring(en.Length);
                    // a translated preset reads lowercase after the colon; player-written text is left exactly as typed
                    return (lang == "ru" ? ru : es) + (table.TryGetValue(rest, out var r) ? LowerFirst(r) : rest);
                }
            return english;
        }

        static string LowerFirst(string t) => t.Length > 0 && char.IsUpper(t[0]) && !(t.Length > 1 && char.IsUpper(t[1])) ? char.ToLowerInvariant(t[0]) + t.Substring(1) : t;

        public static string Label(string english) => Label(CoopText.RenderLang(), english);

        public static string ClaimedBy(string name) => string.Format(CoopText.Pick("Taken by {0}", "Взято: {0}", "Tomada por {0}"), name);
        public static string Unclaimed() => CoopText.Pick("Nobody has taken it", "Пока никто не взял", "Nadie la ha tomado todavía");
        public static string NoteBy(string name) => string.Format(CoopText.Pick("Note by {0}", "Автор заметки: {0}", "Nota de {0}"), name);
        public static string You() => CoopText.Pick("you", "ты", "tú");
        public static string Title() => CoopText.Pick("Co-op", "Вместе", "Cooperativo");
        public static string ReleaseHint(string glyph) => string.Format(CoopText.Pick("Release {0} on an option to choose it, or in the centre to cancel", "Отпусти {0} на пункте, чтобы выбрать его, или в центре, чтобы отменить", "Suelta {0} sobre una opción para elegirla, o en el centro para cancelar"), glyph);
        public static string PadReleaseHint(string glyph) => string.Format(CoopText.Pick("Release {0} to choose, right stick to aim", "Выбирай правым стиком и отпусти {0}", "Apunta con el stick derecho y suelta {0}"), glyph);
        public static string NewJobAt(string place) => string.Format(CoopText.Pick("New job at {0}", "Новое задание: {0}", "Nueva tarea: {0}"), place);
        public static string NoteFor(string place) => string.Format(CoopText.Pick("Note for {0}", "Заметка: {0}", "Nota: {0}"), place);
        public static string BoxHint() => CoopText.Pick("Enter to save, Esc to cancel", "Enter: сохранить, Esc: отмена", "Enter: guardar, Esc: cancelar");
    }
}
