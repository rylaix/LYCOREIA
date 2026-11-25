// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Text;

namespace Lycoreia.Text
{
    public static class MapText
    {
        public static string Title => CoopText.Pick("Co-op job", "Задание", "Tarea");
        public static string Open => CoopText.Pick("Open - anyone can take it", "Свободно - можно взять", "Libre - cualquiera puede tomarla");
        public static string You => CoopText.Pick("You are on it", "Задание у тебя", "Te encargas tú");
        public static string OnIt(string name) => string.Format(CoopText.Pick("{0} is on it", "Выполняет: {0}", "{0} se encarga"), name);
        public static string By(string name) => string.Format(CoopText.Pick("Pinned by {0}", "Автор: {0}", "Creada por {0}"), name);

        /// <summary>The hover label: a title line, the job text, who claimed it (or that it is open) and who pinned it. Names are passed already resolved.</summary>
        public static string Label(string text, string createdBy, string claimedBy, bool mine)
        {
            string state = mine ? You : string.IsNullOrEmpty(claimedBy) ? Open : OnIt(claimedBy);
            string s = "<b>" + Title + "</b>\n" + (text ?? "") + "\n<size=80%>" + state + "</size>";
            if (!string.IsNullOrEmpty(createdBy)) s += "\n<size=70%>" + By(createdBy) + "</size>";
            return s;
        }
    }
}
