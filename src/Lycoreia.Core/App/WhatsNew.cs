// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections.Generic;

namespace Lycoreia
{
    public static class WhatsNew
    {
        public sealed class Section { public string Title; public List<(string text, string tryIt)> Items = new List<(string, string)>(); }

        public static readonly List<Section> Sections = new List<Section>
        {
            new Section { Title = "New: co-op features", Items =
            {
                ("Boost jump: players are now solid. Stand on a friend and jump to reach much higher.", "Have a friend stand still, jump onto their head, then jump again."),
                ("Co-op wheel: hold the ping button (middle mouse) on a corral, garden, silo, Gordo or any spot to pin jobs for each other, claim them and tick them off. Jobs show on the compass and above the spot.", "Hold the middle mouse button while looking at a corral and pick \"Job: Feed this corral\"."),
                ("Expeditions: timed goals you play together (Plort Rush, Gordo Feast, Treasure Trail...). Finishing one pays the shared ranch.", "Pause > Multiplayer > Co-op board > Expeditions > Harvest Run, then vacuum up food together."),
                ("Slime notes: leave a note on a corral or a Gordo. Everyone sees it floating above it.", "Hold the middle mouse button on a Gordo and pick a note, or \"write your own...\"."),
                ("Session recap: what everyone did this session (plorts sold, slimes caught, money earned, jobs done).", "Pause > Multiplayer > Co-op board > Recap, or the main menu after you leave."),
                ("Update notice: the main menu tells you when GitHub or Nexus Mods has a newer version.", "Shown at the top of the main menu as soon as the game starts."),
            } },
            new Section { Title = "Fixed", Items =
            {
                ("Silos, plort collectors and auto-feeders now sync, and plorts are no longer doubled by the collectors.", "Collect plorts with a corral collector; both players see the same silo count."),
                ("Gates, ranch expansions, plort statue doors, puzzle slots and prisma barriers opened by one player open for everyone.", "Open a gate or buy an expansion; the other player sees it open."),
                ("Paddocks and plot upgrades from saves made before playing together now appear for the guest too.", "Join a save that already has corrals."),
                ("Ringtails no longer multiply at dawn and dusk.", "Watch Ringtails turn to stone and wake up with both players nearby."),
                ("Food eaten by a slime disappears for everyone (no more endless chickens).", "Shoot a chicken into the other player's corral."),
                ("Remote players' heads no longer spin; avatars no longer spin during short lag.", ""),
                ("The host returning to the main menu now ends the session cleanly.", ""),
                ("Fewer errors near unloaded doors; plorts can no longer show up slime-sized.", ""),
            } },
        };

        // Russian: the game's own terms (see rewrite/research-ru-glossary.tsv), informal "ты" like the game's tutorials. Same sections and item order as the English list (a test checks the shape).
        public static readonly List<Section> SectionsRu = new List<Section>
        {
            new Section { Title = "Новое в совместной игре", Items =
            {
                ("Прыжок с головы друга: игроки теперь твёрдые. Встань на друга и прыгни, чтобы взлететь гораздо выше.", "пусть друг постоит на месте, запрыгни ему на голову и прыгни ещё раз."),
                ("Совместное колесо: зажми кнопку пинга (средняя кнопка мыши), глядя на загон, сад, силос, гордо или любое место, чтобы оставить друзьям задание, взять его на себя и отметить выполненным. Задания видны на компасе и над самим местом.", "зажми среднюю кнопку мыши, глядя на загон, и выбери «Задание: накормить этот загон»."),
                ("Экспедиции: общие цели на время (Золотая лихорадка, Пир для Гордо, Тропа сокровищ...). За выполненную экспедицию награду получает общее ранчо.", "меню паузы > Мультиплеер > Общая доска > Экспедиции > Сбор урожая, затем вместе собирайте еду."),
                ("Заметки: оставь заметку на загоне или у гордо, и все увидят её над ним.", "зажми среднюю кнопку мыши, глядя на гордо, и выбери заметку или «написать своё...»."),
                ("Итоги сессии: что сделал каждый игрок (проданные плорты, пойманные слаймы, заработанные нью-баксы, выполненные задания).", "меню паузы > Мультиплеер > Общая доска > Итоги, а после выхода из игры они есть в главном меню."),
                ("Уведомление об обновлении: главное меню скажет, если на GitHub или Nexus Mods вышла новая версия.", "оно появляется вверху главного меню сразу после запуска игры."),
            } },
            new Section { Title = "Исправлено", Items =
            {
                ("Силосы, плортосборники и автокормушки теперь синхронизируются, а плорты в сборниках больше не удваиваются.", "собери плорты плортосборником в загоне: у обоих игроков в силосе будет одинаковое число."),
                ("Врата, расширения ранчо, врата плортов, ячейки головоломок и призматические барьеры, открытые одним игроком, теперь открываются у всех.", "открой врата или купи расширение: второй игрок увидит, что оно открылось."),
                ("Загоны и улучшения участков из сохранений, созданных до совместной игры, теперь появляются и у гостя.", "подключись к сохранению, где уже есть загоны."),
                ("Кольцехвостые слаймы больше не размножаются на рассвете и закате.", "понаблюдай, как кольцехвосты каменеют и просыпаются, пока оба игрока рядом."),
                ("Еда, съеденная слаймом, исчезает у всех, так что бесконечных куриц больше нет.", "выстрели курицей в загон другого игрока."),
                ("Головы других игроков больше не вертятся, а аватары не крутятся при коротких лагах.", ""),
                ("Когда хост выходит в главное меню, сессия теперь завершается корректно.", ""),
                ("Меньше ошибок рядом с незагруженными дверями, а плорты больше не появляются размером со слайма.", ""),
            } },
        };

        /// <summary>The release notes in the given game language ("ru" or anything else = English).</summary>
        public static List<Section> For(string lang) => lang == "ru" ? SectionsRu : Sections;
    }
}
