// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Coop;

namespace Lycoreia.Text
{
    public static class MenuText
    {
        static string P(string en, string ru) => CoopText.Pick(en, ru);

        // ---- shared
        public static string Back => P("Back", "Назад");
        public static string TabJobs => P("Jobs", "Задания");
        public static string TabNotes => P("Notes", "Заметки");
        public static string TabExpeditions => P("Expeditions", "Экспедиции");
        public static string TabRecap => P("Recap", "Итоги");
        public static string BoardTitle => P("Co-op board", "Общая доска");

        // ---- board: jobs
        public static string WheelHint(string glyph) => string.Format(P(
            "<size=85%>In the world: <b>hold {0}</b> while looking at a corral, garden, silo, Gordo or any spot to pin a job or leave a note there.</size>",
            "<size=85%>В мире: <b>зажми {0}</b>, глядя на загон, сад, силос, гордо или любое место, чтобы закрепить там задание или оставить заметку.</size>"), glyph);
        public static string JobsEmpty => P("No open jobs yet.", "Заданий пока нет.");
        public static string JobsEmptyHint(string glyph) => string.Format(P(
            "<size=85%><alpha=#BB>Nothing to do? Hold {0} in the world while looking at a corral or a garden and pick a job for your friends.</size>",
            "<size=85%><alpha=#BB>Нечем заняться? Зажми {0}, глядя на загон или сад, и выбери задание для друзей.</size>"), glyph);
        public static string JobOpen => P("open", "свободно");
        public static string JobYou => P("you", "ты");
        public static string JobOnCompass => P(", on compass", ", на компасе");
        public static string ActClaim => P("I'll do it", "Беру");
        public static string ActDrop => P("Drop it", "Отказаться");
        public static string ActDone => P("Done", "Готово");
        public static string ActRemove => P("Remove", "Убрать");
        public static string More(int n) => string.Format(P("...and {0} more", "...и ещё {0}"), n);
        public static string RecentlyDone => P("Recently done", "Недавно выполнено");
        public static string DoneBy(string who, string ago) => string.Format(P("done by {0}, {1}", "выполнено: {0}, {1}"), who, ago);

        /// <summary>"just now", "5 min ago", "3 h ago", "2 d ago".</summary>
        public static string Ago(long seconds)
        {
            if (seconds < 60) return P("just now", "только что");
            if (seconds < 3600) return string.Format(P("{0} min ago", "{0} мин назад"), seconds / 60);
            if (seconds < 86400) return string.Format(P("{0} h ago", "{0} ч назад"), seconds / 3600);
            return string.Format(P("{0} d ago", "{0} дн. назад"), seconds / 86400);
        }

        // ---- board: notes
        public static string NotesEmpty => P("No notes yet.", "Заметок пока нет.");
        public static string NotesEmptyHint(string glyph) => string.Format(P(
            "<size=85%><alpha=#BB>Hold {0} while looking at a corral or a Gordo and pick a note: everyone sees it floating above.</size>",
            "<size=85%><alpha=#BB>Зажми {0}, глядя на загон или гордо, и выбери заметку: она будет висеть над ним для всех игроков.</size>"), glyph);
        public static string Elsewhere => P(", elsewhere", ", в другом месте");

        // ---- board: expeditions
        public static string ExpName(ExpeditionDef d) => d == null ? "" : P(d.Name, d.Id switch
        {
            1 => "Золотая лихорадка", 2 => "Охота на слаймов", 3 => "Пир для Гордо", 4 => "Сбор урожая", 5 => "Тропа сокровищ", 6 => "День рынка", _ => d.Name,
        });
        public static string ExpGoal(ExpeditionDef d) => d == null ? "" : P(d.Goal, d.Id switch
        {
            1 => "Продавай плорты на плорт-маркете", 2 => "Лови слаймов вакпаком", 3 => "Корми гордо слаймами", 4 => "Собирай фрукты, овощи и мясо",
            5 => "Открывай сокровищницы", 6 => "Зарабатывай нью-баксы на продаже", _ => d.Goal,
        });
        public static string ExpRunning(string name, string goal) => $"<b>{name}</b>: {goal}";
        public static string ExpProgress(int progress, int target, long left, int reward) => string.Format(P(
            "{0} / {1}  ·  {2}:{3:00} left  ·  reward {4} {5}", "{0} / {1}  ·  осталось {2}:{3:00}  ·  награда: {4} {5}"), progress, target, left / 60, left % 60, reward, Newbucks(reward));
        public static string Team(string list) => P("Team: ", "Команда: ") + list;
        public static string CallOff => P("Call off the expedition", "Отменить экспедицию");
        public static string LastDone(string name, int reward) => string.Format(P("Last: {0} complete! (+{1} {2})", "Последняя: «{0}» завершена (+{1} {2})"), name, reward, Newbucks(reward));
        public static string LastOther(string name, int progress, int target, string outcome) => string.Format(P("Last: {0} {1}/{2} ({3})", "Последняя: «{0}» {1}/{2} ({3})"), name, progress, target, Outcome(outcome));
        static string Outcome(string o) => P(o, o switch { "failed" => "провалена", "abandoned" => "отменена", "succeeded" => "выполнена", "running" => "идёт", _ => o });
        public static string PickOne => P("Pick an expedition. Everyone's progress counts; the goal grows with the team.", "Выбери экспедицию. Засчитывается прогресс каждого, а цель растёт вместе с командой.");
        public static string ExpOffer(string name, int level, string goal, int target, int minutes, int reward) => string.Format(P(
            "{0}{1}: {2} ({3}) in {4} min · +{5}", "{0}{1}: {2} ({3}) за {4} мин · +{5}"), name, level > 0 ? " " + (level + 1) : "", goal, target, minutes, reward);
        public static string RanchRecord(int won, int total) => string.Format(P("This ranch: {0} of {1} expeditions completed.", "На этом ранчо завершено экспедиций: {0} из {1}."), won, total);

        // ---- board: recap
        public static string RecapEmpty => P("Nothing yet this session. Go sell some plorts!", "За эту сессию пока ничего. Самое время продать плорты!");
        public static string Together(long plorts, long money) => string.Format(P("Together: {0} plorts sold, {1} newbucks earned.", "Вместе продано плортов: {0}, заработано нью-баксов: {1}."), plorts, money);
        static string StatLabel(CoopStat s) => s switch
        {
            CoopStat.PlortsSold => P("plorts sold", "продано плортов"), CoopStat.MoneyEarned => P("newbucks earned", "заработано нью-баксов"),
            CoopStat.SlimesVacuumed => P("slimes vacuumed", "поймано слаймов"), CoopStat.ProduceVacuumed => P("food gathered", "собрано еды"),
            CoopStat.GordoFood => P("Gordo bites fed", "порций для гордо"), CoopStat.PodsOpened => P("treasure pods opened", "открыто сокровищниц"),
            CoopStat.JobsDone => P("jobs done", "выполнено заданий"), _ => s.ToString(),
        };
        /// <summary>English "3 plorts sold"; Russian "продано плортов: 3" (no plural forms to get wrong).</summary>
        static string StatPart(CoopStat s, long v) => CoopText.RenderLang() == "ru" ? StatLabel(s) + ": " + v : v + " " + StatLabel(s);
        static readonly CoopStat[] RecapOrder = { CoopStat.PlortsSold, CoopStat.MoneyEarned, CoopStat.SlimesVacuumed, CoopStat.ProduceVacuumed, CoopStat.GordoFood, CoopStat.PodsOpened, CoopStat.JobsDone };
        public static string RecapLine(IReadOnlyDictionary<CoopStat, long> stats)
        {
            var parts = stats == null ? new List<string>() : RecapOrder.Where(s => stats.TryGetValue(s, out var v) && v > 0).Select(s => StatPart(s, stats[s])).ToList();
            return parts.Count == 0 ? P("nothing yet", "пока ничего") : string.Join(", ", parts);
        }

        // ---- main menu buttons and pages
        public static string WhatsNewButton(string version) => string.Format(P("What's new in {0}?", "Что нового в {0}?"), version);
        public static string LastSessionButton => P("Last session recap", "Итоги прошлой сессии");
        public static string WhatsNewHeading(string version) => string.Format(P("What's new in Lycoreia {0}", "Что нового в Lycoreia {0}"), version);
        public static string TryIt(string how) => P("Try it: ", "Как проверить: ") + how;
        public static string FoundProblem => P("Found a problem? Send UserData/Lycoreia/latest.log from every player.", "Что-то не так? Пришли UserData/Lycoreia/latest.log от каждого игрока.");
        public static string LastSession => P("Last session", "Прошлая сессия");
        public static string NothingRecorded => P("Nothing recorded yet.", "Пока ничего не записано.");

        // ---- what's new popup
        public static string PopupRead => P("Read everything", "Читать всё");
        public static string PopupGotIt => P("Got it", "Понятно");
        public static string PopupMore(int n) => string.Format(P("...and {0} more changes", "...и ещё изменений: {0}"), n);

        // ---- update notices (banner and in-game toast)
        public static string OpenSource(string source) => string.Format(P("Open {0}", "Открыть {0}"), source);

        /// <summary>The localised update line; the status' own English message when it has no version to show.</summary>
        public static string UpdateMessage(UpdateStatus u)
        {
            if (u == null) return "";
            if (string.IsNullOrEmpty(u.Newest) || string.IsNullOrEmpty(u.Installed)) return u.Message ?? "";
            if (u.Kind == UpdateKind.UpdateAvailable)
                return string.Format(P("Lycoreia {0} is available (you have {1}). Download it from {2}. Every player in a session needs the same version.",
                    "Вышла Lycoreia {0} (у тебя {1}). Скачай её: {2}. Версия должна быть одинаковой у всех игроков в сессии."), u.Newest, u.Installed, string.Join(P(" or ", " или "), u.Offering.Select(s => s.Source)));
            if (u.Kind == UpdateKind.AheadOfPublic)
                return string.Format(P("You are running Lycoreia {0}, which is newer than the public release {1}. Other players need this exact build to play with you.",
                    "У тебя Lycoreia {0}, она новее публичного релиза {1}. Чтобы играть с тобой, остальным нужна именно эта сборка."), u.Installed, u.Newest);
            return u.Message ?? "";
        }

        /// <summary>In-game toast for the same status: a short title and one detail line.</summary>
        public static void UpdateToast(UpdateStatus u, out string title, out string body)
        {
            bool avail = u != null && u.Kind == UpdateKind.UpdateAvailable;
            title = avail ? P("Update available", "Доступно обновление") : P("Newer than the public release", "Новее публичного релиза");
            if (u == null || string.IsNullOrEmpty(u.Newest)) { body = u?.Message; return; }
            body = avail
                ? string.Format(P("Lycoreia {0} is out (you have {1}). Links are in the Multiplayer menu.", "Вышла Lycoreia {0} (у тебя {1}). Ссылки в меню «Мультиплеер»."), u.Newest, u.Installed)
                : string.Format(P("Your build {0} is ahead of {1}. Others need this exact build.", "Твоя сборка {0} новее {1}. Остальным нужна именно она."), u.Installed, u.Newest);
        }

        // ---- one-time hint cards
        public static string HintTitle => P("Tip", "Подсказка");
        public static string HintWheel(string glyph) => string.Format(P("Hold {0} for the co-op wheel: pin jobs and notes for your friends.", "Зажми {0}, чтобы открыть совместное колесо: с ним можно закреплять задания и заметки для друзей."), glyph);
        public static string HintBoost => P("Jump off a friend's head for a boost.", "Оттолкнись от головы друга, чтобы прыгнуть выше.");

        // ---- plural helper (Russian one / few / many)
        /// <summary>Russian plural form for n: 1 (21, 101) one; 2-4 (22-24) few; the rest many.</summary>
        public static string Plural(long n, string one, string few, string many)
        {
            long a = Math.Abs(n) % 100, b = a % 10;
            if (a >= 11 && a <= 14) return many;
            return b == 1 ? one : (b >= 2 && b <= 4 ? few : many);
        }
        public static string Newbucks(long n) => CoopText.RenderLang() == "ru" ? Plural(n, "нью-бакс", "нью-бакса", "нью-баксов") : "newbucks";

        // ---- Multiplayer menu (LycoreiaMenu.cs)
        public static string Close => P("Close", "Закрыть");
        public static string Cancel => P("Cancel", "Отмена");
        public static string StillStarting => P("The multiplayer UI is still starting. Try again in a moment.", "Меню мультиплеера ещё запускается. Попробуй чуть позже.");
        public static string Connecting => P("Connecting to the host...", "Подключение к хосту...");
        public static string MayCloseMenu => P("This menu can be closed; it continues in the background.", "Меню можно закрыть: подключение продолжится в фоне.");
        public static string PlayingAs(string name) => string.Format(P("Playing as <b>{0}</b>", "Твой ник: <b>{0}</b>"), name);
        public static string HostWorld => P("Host a world", "Открыть мир для друзей");
        public static string JoinFriend => P("Join a friend", "Присоединиться к другу");
        public static string HostThisWorld => P("Host this world", "Открыть этот мир для друзей");
        public static string JoinWorld => P("Join a world", "Присоединиться к миру");
        public static string Settings => P("Settings", "Настройки");
        public static string ModeCode => P("Join code (automatic)", "Код (авто)");
        public static string ModePort => P("Fixed port", "Свой порт");
        public static string HostAutoInfo => P("Opens a port on your router and gives you a code to share.", "Порт на роутере откроется сам, а друзьям останется только ввести код.");
        public static string Port => P("Port", "Порт");
        public static string PickWorld => P("Pick the world to host", "Выбери мир, который хочешь открыть");
        public static string NoSaves => P("No saves yet. Start a new game first, then host from the pause menu.", "Сохранений пока нет. Сначала начни новую игру, а открыть мир можно будет из меню паузы.");
        public static string SaveNumber(string n) => P("Save ", "Сохранение ") + n;
        public static string SaveCurrency(long n) => string.Format(P("({0:N0} newbucks)", "(нью-баксы: {0:N0})"), n);
        public static string JoinReplaces => P("Joining replaces this save with the host's world. Use an empty save to join.", "При подключении это сохранение заменится миром хоста. Лучше выбрать пустое.");
        public static string JoinHeading => P("Join a world", "Подключение к миру");
        public static string TabJoinCode => P("Join code", "По коду");
        public static string TabIp => P("IP address", "По IP-адресу");
        public static string JoinCode => P("Join code", "Код");
        public static string JoinCodePlaceholder => P("Paste the host's code", "Вставь код хоста");
        public static string HostIp => P("Host IP", "IP хоста");
        public static string IpPlaceholder => P("e.g. 192.168.1.20", "например, 192.168.1.20");
        public static string PortPlaceholder => P("e.g. 1919", "например, 1919");
        public static string Join => P("Join", "Подключиться");
        public static string SeparateSave => P("Joining uses a separate co-op save, so your own worlds stay untouched.", "Игра по сети идёт на отдельном сохранении, твои миры не пострадают.");
        public static string UseSave(string label) => P("Use ", "Использовать: ") + label;
        public static string HostAutoBlurb => P("Automatic: opens a port on your router and gives you a join code to share.", "Автоматически: порт на роутере откроется сам, а ты получишь код для друзей.");
        public static string HostAuto => P("Host automatically", "Открыть автоматически");
        public static string CancelAuto => P("Cancel automatic setup", "Отменить автонастройку");
        public static string OrHostPort => P("Or host on a port", "Или открыть на своём порту");
        public static string StartServer => P("Start server", "Запустить сервер");
        public static string YouAreHosting => P("You are hosting", "Твой мир открыт для друзей");
        public static string Hidden => P("hidden (streamer mode)", "скрыт (режим стримера)");
        public static string PortLine(string v) => P("Port: ", "Порт: ") + v;
        public static string CodeLine(string v) => P("Join code: <b>", "Код: <b>") + v + "</b>";
        public static string CopyCode => P("Copy join code", "Скопировать код");
        public static string CodeCopied => P("Join code copied.", "Код скопирован.");
        public static string BoardButton => P("Co-op board: jobs, notes, expeditions", "Общая доска: задания, заметки, экспедиции");
        public static string ResyncAll => P("Resync everyone", "Синхронизировать всех заново");
        public static string ResyncSent => P("Resync sent to all players.", "Повторная синхронизация отправлена всем игрокам.");
        public static string StopServer => P("Stop server", "Остановить сервер");
        public static string Connected => P("Connected", "Подключено");
        public static string RequestResync => P("Request resync", "Запросить синхронизацию");
        public static string ResyncRequested => P("Resync requested from the host.", "Синхронизация запрошена у хоста.");
        public static string Disconnect => P("Disconnect", "Отключиться");
        public static string SettingsHeading(bool firstTime) => firstTime ? P("Welcome! Choose your name", "Добро пожаловать! Выбери имя") : Settings;
        public static string Username => P("Username", "Ник");
        public static string DefaultName => P("Player", "Игрок");
        public static string TabPalette => P("Palette", "Палитра");
        public static string TabHex => P("Hex code", "HEX-код");
        public static string TabPicker => P("Picker", "Выбор");
        public static string TabRandom => P("Random", "Случайный");
        public static string Preview => P("Preview:", "Так будет выглядеть:");
        public static string NameColor => P("Name color (hex)", "Цвет ника (HEX)");
        public static string AllowCheats => P("Allow cheats (host)", "Разрешить читы (хост)");
        public static string DisableAchievements => P("Disable achievements", "Отключить достижения");
        public static string FriendsVac => P("Friends can vac me", "Друзья могут вакать меня");
        public static string FriendsKick => P("Friends can kick me (V)", "Друзья могут пинать меня (V)");
        public static string Save => P("Save", "Сохранить");
        public static string SettingsSaved => P("Settings saved.", "Настройки сохранены.");
        public static string Players(int n) => P("Players (", "Игроки (") + n + ")";
        public static string Unnamed => P("(unnamed player)", "(без имени)");
        public static string WidescreenHeading => P("Widescreen FOV", "Угол обзора для широких экранов");
        public static string WidescreenInfo => P("Field of view is measured across a 16:9 screen; wider screens see more at the sides.", "Угол обзора считается для экрана 16:9, а на более широких экранах по бокам видно больше.");
        public static string WidescreenUse => P("Use these settings (off = game's own FOV)", "Использовать эти настройки (выкл.: угол обзора игры)");
        public static string FovSlider(string min, string max) => P("Field of view (", "Угол обзора (") + min + "-" + max + ")";
        public static string WidescreenHint => P("Lower values show less, but also stretch the screen edges less.", "Чем меньше значение, тем уже обзор, но края экрана растягиваются слабее.");
    }
}
