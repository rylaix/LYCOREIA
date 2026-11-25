// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Text;

namespace Lycoreia.Text
{
    public static class CoopWorldText
    {
        /// <summary>World labels are rich text; player-written text must not be able to open a tag.</summary>
        public static string Plain(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace('<', (char)0x2039);
        public static string Note(string text, string author) => CoopText.Pick("Note", "Заметка", "Nota") + ": " + Plain(text) + "\n<size=70%>" + Plain(author) + "</size>";
        public static string Job(string text, string who) => CoopText.Pick("Job", "Задание", "Tarea") + ": " + Plain(text) + "\n<size=70%>" + Plain(who) + "</size>";
        /// <summary>Far away: no owner line.</summary>
        public static string JobShort(string text) => CoopText.Pick("Job", "Задание", "Tarea") + ": " + Plain(text);
        public static string OpenJob => CoopText.Pick("open job", "свободное задание", "tarea libre");
        public static string OnIt(string name) => string.Format(CoopText.Pick("{0} is on it", "Выполняет: {0}", "{0} se encarga"), name);
        public static string Complete => CoopText.Pick("complete!", "готово!", "¡completada!");
        public static string TimeUp => CoopText.Pick("time is up", "время вышло", "se acabó el tiempo");
        public static string Left(string clock) => string.Format(CoopText.Pick("{0} left", "осталось {0}", "quedan {0}"), clock);
        public static string JobMapName => CoopText.Pick("Co-op job", "Задание", "Tarea");
    }
}
