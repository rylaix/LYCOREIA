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
        static string P(string en, string ru, string es) => CoopText.Pick(en, ru, es);

        // ---- shared
        public static string Back => P("Back", "Назад", "Regresar");
        public static string TabJobs => P("Jobs", "Задания", "Tareas");
        public static string TabNotes => P("Notes", "Заметки", "Notas");
        public static string TabExpeditions => P("Expeditions", "Экспедиции", "Expediciones");
        public static string TabRecap => P("Recap", "Итоги", "Resumen");
        public static string BoardTitle => P("Co-op board", "Общая доска", "Tablón cooperativo");

        // ---- board: jobs
        public static string WheelHint(string glyph) => string.Format(P(
            "<size=85%>In the world: <b>hold {0}</b> while looking at a corral, garden, silo, Gordo or any spot to pin a job or leave a note there.</size>", "<size=85%>В игре: <b>зажми {0}</b>, глядя на загон, сад, силос, гордо или любое другое место, чтобы закрепить там задание или оставить заметку.</size>", "<size=85%>En el mundo: <b>mantén {0}</b> mientras miras un corral, un jardín, un silo, un Gordo o cualquier lugar para fijar una tarea o dejar una nota.</size>"), glyph);
        public static string JobsEmpty => P("No open jobs yet.", "Заданий пока нет.", "Aún no hay tareas.");
        public static string JobsEmptyHint(string glyph) => string.Format(P(
            "<size=85%><alpha=#BB>Nothing to do? Hold {0} in the world while looking at a corral or a garden and pick a job for your friends.</size>", "<size=85%><alpha=#BB>Нечем заняться? Зажми {0}, глядя на загон или сад, и выбери задание для друзей.</size>", "<size=85%><alpha=#BB>¿Nada que hacer? Mantén {0} mientras miras un corral o un jardín y elige una tarea para tus amigos.</size>"), glyph);
        public static string JobOpen => P("open", "свободно", "libre");
        public static string JobYou => P("you", "ты", "tú");
        public static string JobOnCompass => P(", on compass", ", на компасе", ", en la brújula");
        public static string ActClaim => P("I'll do it", "Беру", "Me encargo");
        public static string ActDrop => P("Drop it", "Отказаться", "Dejarla");
        public static string ActDone => P("Done", "Готово", "Hecho");
        public static string ActRemove => P("Remove", "Убрать", "Quitar");
        public static string More(int n) => string.Format(P("...and {0} more", "...и ещё {0}", "...y {0} más"), n);
        public static string RecentlyDone => P("Recently done", "Недавно выполнено", "Completadas hace poco");
        public static string DoneBy(string who, string ago) => string.Format(P("done by {0}, {1}", "выполнено: {0}, {1}", "completada por {0}, {1}"), who, ago);

        /// <summary>"just now", "5 min ago", "3 h ago", "2 d ago".</summary>
        public static string Ago(long seconds)
        {
            if (seconds < 60) return P("just now", "только что", "ahora mismo");
            if (seconds < 3600) return string.Format(P("{0} min ago", "{0} мин назад", "hace {0} min"), seconds / 60);
            if (seconds < 86400) return string.Format(P("{0} h ago", "{0} ч назад", "hace {0} h"), seconds / 3600);
            return string.Format(P("{0} d ago", "{0} дн. назад", "hace {0} d"), seconds / 86400);
        }

        // ---- board: notes
        public static string NotesEmpty => P("No notes yet.", "Заметок пока нет.", "Aún no hay notas.");
        public static string NotesEmptyHint(string glyph) => string.Format(P(
            "<size=85%><alpha=#BB>Hold {0} while looking at a corral or a Gordo and pick a note: everyone sees it floating above.</size>", "<size=85%><alpha=#BB>Зажми {0}, глядя на загон или гордо, и выбери заметку: она появится над ним, и её увидят все.</size>", "<size=85%><alpha=#BB>Mantén {0} mientras miras un corral o un Gordo y elige una nota: todos la verán flotando encima.</size>"), glyph);
        public static string Elsewhere => P(", elsewhere", ", в другом месте", ", en otro lugar");

        // ---- board: expeditions
        public static string ExpName(ExpeditionDef d) => d == null ? "" : CoopNoticeText.ExpeditionName(CoopText.RenderLang(), d.Name);
        public static string ExpGoal(ExpeditionDef d) => d == null ? "" : CoopNoticeText.ExpeditionName(CoopText.RenderLang(), d.Goal);
        public static string ExpRunning(string name, string goal) => $"<b>{name}</b>: {goal}";
        public static string ExpProgress(int progress, int target, long left, int reward) => string.Format(P(
            "{0} / {1}  ·  {2}:{3:00} left  ·  reward {4} {5}", "{0} / {1}  ·  осталось {2}:{3:00}  ·  награда: {4} {5}", "{0} / {1}  ·  quedan {2}:{3:00}  ·  recompensa: {4} {5}"), progress, target, left / 60, left % 60, reward, Newbucks(reward));
        public static string Team(string list) => P("Team: ", "Команда: ", "Equipo: ") + list;
        public static string CallOff => P("Call off the expedition", "Отменить экспедицию", "Cancelar la expedición");
        public static string LastDone(string name, int reward) => string.Format(P("Last: {0} complete! (+{1} {2})", "Последняя: «{0}» завершена (+{1} {2})", "Última: ¡{0} completada! (+{1} {2})"), name, reward, Newbucks(reward));
        public static string LastOther(string name, int progress, int target, string outcome) => string.Format(P("Last: {0} {1}/{2} ({3})", "Последняя: «{0}» {1}/{2} ({3})", "Última: {0} {1}/{2} ({3})"), name, progress, target, Outcome(outcome));
        static string Outcome(string o) => P(o, o switch { "failed" => "провалена", "abandoned" => "отменена", "succeeded" => "выполнена", "running" => "идёт", _ => o },
            o switch { "failed" => "fallida", "abandoned" => "cancelada", "succeeded" => "completada", "running" => "en curso", _ => o });
        public static string PickOne => P("Pick an expedition. Everyone's progress counts; the goal grows with the team.", "Выбери экспедицию. Засчитывается вклад каждого, а чем больше команда, тем выше цель.", "Elige una expedición. Cuenta el progreso de todos, y la meta crece con el equipo.");
        public static string ExpOffer(string name, int level, string goal, int target, int minutes, int reward) => string.Format(P(
            "{0}{1}: {2} ({3}) in {4} min · +{5}", "{0}{1}: {2} ({3}) за {4} мин · +{5}", "{0}{1}: {2} ({3}) en {4} min · +{5}"), name, level > 0 ? " " + (level + 1) : "", goal, target, minutes, reward);
        public static string RanchRecord(int won, int total) => string.Format(P("This ranch: {0} of {1} expeditions completed.", "На этом ранчо завершено экспедиций: {0} из {1}.", "Este rancho: {0} de {1} expediciones completadas."), won, total);

        // ---- board: recap
        public static string RecapEmpty => P("Nothing yet this session. Go sell some plorts!", "За эту сессию пока ничего. Самое время продать плорты!", "Nada todavía en esta sesión. ¡Ve a vender plorts!");
        public static string Together(long plorts, long money) => string.Format(P("Together: {0} plorts sold, {1} newbucks earned.", "Вместе продано плортов: {0}, заработано нью-баксов: {1}.", "Juntos: {0} plorts vendidos, {1} newbucks ganados."), plorts, money);
        static string StatLabel(CoopStat s) => s switch
        {
            CoopStat.PlortsSold => P("plorts sold", "продано плортов", "plorts vendidos"), CoopStat.MoneyEarned => P("newbucks earned", "заработано нью-баксов", "newbucks ganados"),
            CoopStat.SlimesVacuumed => P("slimes vacuumed", "поймано слаймов", "slimes aspirados"), CoopStat.ProduceVacuumed => P("food gathered", "собрано еды", "comida recogida"),
            CoopStat.GordoFood => P("Gordo bites fed", "скормлено гордо", "bocados a Gordos"), CoopStat.PodsOpened => P("treasure pods opened", "открыто сокровищниц", "vainas de tesoro abiertas"),
            CoopStat.JobsDone => P("jobs done", "выполнено заданий", "tareas completadas"), _ => s.ToString(),
        };
        /// <summary>English "3 plorts sold"; Russian "продано плортов: 3" (no plural forms to get wrong).</summary>
        static string StatPart(CoopStat s, long v) => CoopText.RenderLang() == "ru" ? StatLabel(s) + ": " + v : v + " " + StatLabel(s);
        static readonly CoopStat[] RecapOrder = { CoopStat.PlortsSold, CoopStat.MoneyEarned, CoopStat.SlimesVacuumed, CoopStat.ProduceVacuumed, CoopStat.GordoFood, CoopStat.PodsOpened, CoopStat.JobsDone };
        public static string RecapLine(IReadOnlyDictionary<CoopStat, long> stats)
        {
            var parts = stats == null ? new List<string>() : RecapOrder.Where(s => stats.TryGetValue(s, out var v) && v > 0).Select(s => StatPart(s, stats[s])).ToList();
            return parts.Count == 0 ? P("nothing yet", "пока ничего", "nada todavía") : string.Join(", ", parts);
        }

        // ---- main menu buttons and pages
        public static string WhatsNewButton(string version) => string.Format(P("What's new in {0}?", "Что нового в {0}?", "¿Qué hay de nuevo en {0}?"), version);
        public static string LastSessionButton => P("Last session recap", "Итоги прошлой сессии", "Resumen de la última sesión");
        public static string WhatsNewHeading(string version) => string.Format(P("What's new in Lycoreia {0}", "Что нового в Lycoreia {0}", "Novedades de Lycoreia {0}"), version);
        public static string TryIt(string how) => P("Try it: ", "Попробуй: ", "Pruébalo: ") + how;
        public static string FoundProblem => P("Found a problem? Send UserData/Lycoreia/latest.log from every player.", "Что-то не так? Пришли UserData/Lycoreia/latest.log от каждого игрока.", "¿Algún problema? Envía UserData/Lycoreia/latest.log de cada jugador.");
        public static string LastSession => P("Last session", "Прошлая сессия", "Última sesión");
        public static string NothingRecorded => P("Nothing recorded yet.", "Пока ничего не записано.", "Aún no hay nada registrado.");

        // ---- what's new popup
        public static string PopupRead => P("Read everything", "Все изменения", "Ver todo");
        public static string PopupGotIt => P("Got it", "Понятно", "Entendido");
        public static string PrevPage => P("Previous", "Назад", "Anterior");
        public static string NextPage => P("Next", "Дальше", "Siguiente");
        public static string PageOf(int page, int pages) => string.Format(P("{0} of {1}", "{0} из {1}", "{0} de {1}"), page, pages);
        public static string PopupMore(int n) => string.Format(P("...and {0} more changes", "...и ещё изменений: {0}", "...y {0} cambios más"), n);

        // ---- update notices (banner and in-game toast)
        public static string OpenSource(string source) => string.Format(P("Open {0}", "Открыть {0}", "Abrir {0}"), source);

        /// <summary>The localised update line; the status' own English message when it has no version to show.</summary>
        public static string UpdateMessage(UpdateStatus u)
        {
            if (u == null) return "";
            if (string.IsNullOrEmpty(u.Newest) || string.IsNullOrEmpty(u.Installed)) return u.Message ?? "";
            if (u.Kind == UpdateKind.UpdateAvailable)
                return string.Format(P("Lycoreia {0} is available (you have {1}). Download it from {2}. Every player in a session needs the same version.", "Вышла Lycoreia {0} (у тебя {1}). Скачай её: {2}. Версия должна быть одинаковой у всех игроков в сессии.", "Ya está disponible Lycoreia {0} (tienes la {1}). Descárgala en {2}. Todos los jugadores de una sesión necesitan la misma versión."), u.Newest, u.Installed, string.Join(P(" or ", " или ", " o "), u.Offering.Select(s => s.Source)));
            if (u.Kind == UpdateKind.AheadOfPublic)
                return string.Format(P("You are running Lycoreia {0}, which is newer than the public release {1}. Other players need this exact build to play with you.", "У тебя Lycoreia {0}, а публичная версия пока {1}. Чтобы играть с тобой, остальным нужна именно эта сборка.", "Usas Lycoreia {0}, más nueva que la versión pública {1}. Los demás jugadores necesitan exactamente esta versión para jugar contigo."), u.Installed, u.Newest);
            return u.Message ?? "";
        }

        /// <summary>In-game toast for the same status: a short title and one detail line.</summary>
        public static void UpdateToast(UpdateStatus u, out string title, out string body)
        {
            bool avail = u != null && u.Kind == UpdateKind.UpdateAvailable;
            title = avail ? P("Update available", "Доступно обновление", "Actualización disponible") : P("Newer than the public release", "Сборка новее публичной", "Más nueva que la versión pública");
            if (u == null || string.IsNullOrEmpty(u.Newest)) { body = u?.Message; return; }
            body = avail
                ? string.Format(P("Lycoreia {0} is out (you have {1}). Links are in the Multiplayer menu.", "Вышла Lycoreia {0} (у тебя {1}). Ссылки в меню Multiplayer.", "Ya salió Lycoreia {0} (tienes la {1}). Los enlaces están en el menú Multiplayer."), u.Newest, u.Installed)
                : string.Format(P("Your build {0} is ahead of {1}. Others need this exact build.", "Твоя сборка {0} новее {1}. Остальным нужна именно она.", "Tu versión {0} es más nueva que {1}. Los demás necesitan exactamente esta."), u.Installed, u.Newest);
        }

        // ---- one-time hint cards
        public static string HintTitle => P("Tip", "Подсказка", "Consejo");
        public static string HintWheel(string glyph) => string.Format(P("Hold {0} for the co-op wheel: pin jobs and notes for your friends.", "Зажми {0}, чтобы открыть колесо: оставляй друзьям задания и заметки.", "Mantén {0} para abrir la rueda cooperativa: deja tareas y notas a tus amigos."), glyph);
        public static string HintBoost => P("Jump off a friend's head for a boost.", "Оттолкнись от головы друга, чтобы прыгнуть выше.", "Salta desde la cabeza de un amigo para llegar más alto.");

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
        public static string Close => P("Close", "Закрыть", "Cerrar");
        public static string Cancel => P("Cancel", "Отмена", "Cancelar");
        public static string StillStarting => P("The multiplayer UI is still starting. Try again in a moment.", "Меню мультиплеера ещё запускается. Попробуй чуть позже.", "El menú multijugador aún se está iniciando. Inténtalo de nuevo en un momento.");
        public static string Connecting => P("Connecting to the host...", "Подключение к хосту...", "Conectando con el anfitrión...");
        public static string MayCloseMenu => P("This menu can be closed; it continues in the background.", "Меню можно закрыть: подключение продолжится в фоне.", "Puedes cerrar este menú; la conexión sigue en segundo plano.");
        public static string PlayingAs(string name) => string.Format(P("Playing as <b>{0}</b>", "Твой ник: <b>{0}</b>", "Juegas como <b>{0}</b>"), name);
        public static string HostWorld => P("Host a world", "Открыть мир для друзей", "Abrir un mundo para amigos");
        public static string JoinFriend => P("Join a friend", "Присоединиться к другу", "Unirse a un amigo");
        public static string HostThisWorld => P("Host this world", "Открыть этот мир для друзей", "Abrir este mundo para amigos");
        public static string JoinWorld => P("Join a world", "Присоединиться к миру", "Unirse a un mundo");
        public static string Settings => P("Settings", "Настройки", "Ajustes");
        public static string ModeCode => P("Join code (automatic)", "По коду (авто)", "Código (automático)");
        public static string ModePort => P("Fixed port", "Свой порт", "Puerto fijo");
        public static string HostAutoInfo => P("Gives you a code to share and opens a port on your router. Steam friends join you through Steam, even without router setup.", "Ты получишь код для друзей, а порт на роутере откроется сам. Друзья в Steam зайдут через Steam, даже без настройки роутера.", "Obtienes un código para compartir y se abre un puerto en tu router. Tus amigos de Steam se unen por Steam, incluso sin configurar el router.");
        public static string Port => P("Port", "Порт", "Puerto");
        public static string PickWorld => P("Pick the world to host", "Выбери мир, который хочешь открыть", "Elige el mundo que quieres abrir");
        public static string NoSaves => P("No saves yet. Start a new game first, then host from the pause menu.", "Сохранений пока нет. Сначала начни новую игру, а открыть мир можно будет из меню паузы.", "Aún no hay partidas guardadas. Empieza un juego nuevo y abre el mundo desde el menú de pausa.");
        public static string SaveNumber(string n) => P("Save ", "Сохранение ", "Partida ") + n;
        public static string SaveCurrency(long n) => string.Format(P("({0:N0} newbucks)", "(нью-баксы: {0:N0})", "({0:N0} newbucks)"), n);
        public static string JoinReplaces => P("Joining replaces this save with the host's world. Use an empty save to join.", "При подключении это сохранение заменится миром хоста. Лучше выбрать пустое.", "Al unirte, esta partida se sustituye por el mundo del anfitrión. Usa una partida vacía.");
        public static string JoinHeading => P("Join a world", "Подключение к миру", "Unirse a un mundo");
        public static string TabJoinCode => P("Join code", "По коду", "Con código");
        public static string TabIp => P("IP address", "По IP-адресу", "Con IP");
        public static string JoinCode => P("Join code", "Код", "Código");
        public static string JoinCodePlaceholder => P("Paste the host's code", "Вставь код хоста", "Pega el código del anfitrión");
        public static string HostIp => P("Host IP", "IP хоста", "IP del anfitrión");
        public static string IpPlaceholder => P("e.g. 192.168.1.20", "например, 192.168.1.20", "p. ej. 192.168.1.20");
        public static string PortPlaceholder => P("e.g. 1919", "например, 1919", "p. ej. 1919");
        public static string Join => P("Join", "Подключиться", "Unirse");
        public static string SeparateSave => P("Joining uses a separate co-op save, so your own worlds stay untouched.", "Игра по сети идёт на отдельном сохранении, твои миры не пострадают.", "Al unirte se usa una partida cooperativa aparte, así que tus mundos no se tocan.");
        public static string UseSave(string label) => P("Use ", "Использовать: ", "Usar: ") + label;
        public static string HostAutoBlurb => P("Automatic: gives you a join code and opens a port on your router. Steam friends join you through Steam, even without router setup.", "Автоматически: ты получишь код для друзей, а порт на роутере откроется сам. Друзья в Steam зайдут через Steam, даже без настройки роутера.", "Automático: obtienes un código para compartir y se abre un puerto en tu router. Tus amigos de Steam se unen por Steam, incluso sin configurar el router.");
        public static string HostAuto => P("Host automatically", "Открыть автоматически", "Abrir automáticamente");
        public static string CancelAuto => P("Cancel automatic setup", "Отменить автонастройку", "Cancelar la configuración automática");
        public static string OrHostPort => P("Or host on a port", "Или открыть на своём порту", "O abrir en un puerto");
        public static string StartServer => P("Start server", "Запустить сервер", "Iniciar servidor");
        public static string YouAreHosting => P("You are hosting", "Твой мир открыт для друзей", "Tu mundo está abierto");
        public static string Hidden => P("hidden (streamer mode)", "скрыт (режим стримера)", "oculto (modo streamer)");
        public static string PortLine(string v) => P("Port: ", "Порт: ", "Puerto: ") + v;
        public static string CodeLine(string v) => P("Join code: <b>", "Код: <b>", "Código: <b>") + v + "</b>";

        // ---- why friends cannot reach an automatically hosted world
        public static string HostReachWarning(HostReach reach) => reach switch
        {
            HostReach.CarrierNat => P(
                "Friends over the internet probably cannot join directly: your provider gives the router a shared address (CGNAT), so the opened port never reaches the internet. Steam friends can still join through Steam. Others: use Radmin VPN or ZeroTier (join by their address) or a tunnel such as PlayIt, let a friend with a public IP host, or ask your provider for a public IP.", "Напрямую через интернет к тебе, скорее всего, не зайти: у тебя «серый» IP (провайдер даёт роутеру общий адрес, CGNAT), и открытый порт снаружи не виден. Друзья в Steam всё равно зайдут через Steam. Остальным поможет Radmin VPN или ZeroTier (вход по их адресу), туннель вроде PlayIt, мир у друга с белым IP или белый IP от провайдера.", "Probablemente tus amigos no puedan entrar directamente por internet: tu proveedor le da al router una dirección compartida (CGNAT), así que el puerto abierto no llega a internet. Tus amigos de Steam sí pueden entrar por Steam. Para los demás: usa Radmin VPN o ZeroTier (entrando con su dirección) o un túnel como PlayIt, deja que abra el mundo un amigo con IP pública o pide una IP pública a tu proveedor."),
            HostReach.DoubleNat => P(
                "Friends over the internet probably cannot join directly: your router sits behind another router or modem, so the opened port never reaches the internet. Steam friends can still join through Steam. Others: put the provider's modem in bridge mode, forward the port on both devices, or use Radmin VPN, ZeroTier or PlayIt.", "Напрямую через интернет к тебе, скорее всего, не зайти: твой роутер стоит за другим роутером или модемом, и открытый порт снаружи не виден. Друзья в Steam всё равно зайдут через Steam. Для остальных переведи модем провайдера в режим моста, пробрось порт на обоих устройствах или используй Radmin VPN, ZeroTier или PlayIt.", "Probablemente tus amigos no puedan entrar directamente por internet: tu router está detrás de otro router o módem, así que el puerto abierto no llega a internet. Tus amigos de Steam sí pueden entrar por Steam. Para los demás: pon el módem del proveedor en modo puente, redirige el puerto en ambos aparatos o usa Radmin VPN, ZeroTier o PlayIt."),
            HostReach.VpnOrProxy => P(
                "This PC goes online through a VPN or proxy, but the port was opened on your home router. The code uses the router's address; if friends still cannot join, turn the VPN off while hosting or exclude the game from it.", "Этот ПК выходит в интернет через VPN или прокси, а порт открыт на домашнем роутере. Код указывает на адрес роутера; если друзья всё равно не могут зайти, выключи VPN на время игры или добавь игру в исключения.", "Este PC sale a internet por una VPN o un proxy, pero el puerto se abrió en el router de tu casa. El código usa la dirección del router; si tus amigos siguen sin poder entrar, desactiva la VPN mientras juegas o excluye el juego de ella."),
            _ => "",
        };
        // ---- rc6: Steam connect visible everywhere
        public static string TabSteam => P("Steam friends", "Друзья в Steam", "Amigos de Steam");
        public static string SteamStatusReady => P("<b>Steam connect is on:</b> friends join you through Steam, no router setup needed.", "<b>Подключение через Steam включено:</b> друзья заходят к тебе через Steam, роутер настраивать не нужно.", "<b>Conexión por Steam activada:</b> tus amigos se unen por Steam, sin configurar el router.");
        public static string SteamStatusOff(string why) => string.Format(P("Steam connect is off ({0}). Join codes and IP still work.", "Подключение через Steam недоступно ({0}). Коды и IP работают как раньше.", "La conexión por Steam no está disponible ({0}). Los códigos y la IP siguen funcionando."), why);
        public static string SteamStatusStarting => P("Steam connect is starting...", "Подключение через Steam запускается...", "Iniciando la conexión por Steam...");
        public static string SteamJoinHelp => P(
            "Your Steam friends who host a world show up here. Ask your friend to open Multiplayer > Host a world; they appear within a few seconds.", "Здесь появляются друзья в Steam, у которых открыт мир. Попроси друга нажать Multiplayer > Открыть мир для друзей, и он появится здесь через несколько секунд.", "Aquí aparecen tus amigos de Steam con un mundo abierto. Pide a tu amigo que pulse Multiplayer > Abrir un mundo para amigos; aparecerá en unos segundos.");
        public static string SteamNobodyHosting => P("No Steam friend is hosting right now.", "Сейчас ни у кого из друзей в Steam мир не открыт.", "Ahora mismo ningún amigo de Steam tiene un mundo abierto.");
        public static string SteamNotHere(string why) => string.Format(P("Steam is not available here ({0}). Use the join code or IP tab.", "Steam здесь недоступен ({0}). Используй вкладку с кодом или IP.", "Steam no está disponible aquí ({0}). Usa la pestaña de código o de IP."), why);
        public static string ConnectingVia(string route) => route switch
        {
            "steam" => P("Connecting through Steam...", "Подключение через Steam...", "Conectando por Steam..."),
            "lan" => P("Connecting over your home network...", "Подключение по домашней сети...", "Conectando por tu red doméstica..."),
            "punch" => P("Connecting over the internet (needs the host to let you in)...", "Подключение через интернет (хост должен тебя впустить)...", "Conectando por internet (el anfitrión tiene que dejarte entrar)..."),
            _ => P("Connecting over the internet...", "Подключение через интернет...", "Conectando por internet..."),
        };
        public static string ConnectedVia(string route) => route switch
        {
            "steam" => P("Connected through Steam", "Подключено через Steam", "Conectado por Steam"),
            "lan" => P("Connected over your home network", "Подключено по домашней сети", "Conectado por tu red doméstica"),
            _ => P("Connected over the internet", "Подключено через интернет", "Conectado por internet"),
        };
        public static string SteamHostChat => P("Your world is open through Steam too: Steam friends can join from Multiplayer > Join a friend > Steam friends.", "Мир открыт и через Steam: друзья в Steam зайдут через Multiplayer > Присоединиться к другу > Друзья в Steam.", "Tu mundo también está abierto por Steam: tus amigos de Steam pueden unirse desde Multiplayer > Unirse a un amigo > Amigos de Steam.");
        public static string FriendHostingToastTitle(string name) => string.Format(P("{0} opened their world", "{0} открывает мир", "{0} abrió su mundo"), name);
        public static string FriendHostingToastBody => P("Join through Steam: Multiplayer > Join a friend > Steam friends.", "Зайти через Steam: Multiplayer > Присоединиться к другу > Друзья в Steam.", "Únete por Steam: Multiplayer > Unirse a un amigo > Amigos de Steam.");

        // ---- 2026-11-23: Steam connections and hole punching (let-in codes)
        public static string LetInChat(string code) => string.Format(P(
            "To get in, send the host this let-in code (already copied): {0}. The host pastes it on their hosting page.", "Чтобы зайти, отправь хосту этот код входа (он уже скопирован): {0}. Хост вставит его на странице хостинга.", "Para entrar, envía al anfitrión este código de acceso (ya está copiado): {0}. El anfitrión lo pega en su página de anfitrión."), code);
        public static string RouterFailedButOpen(string error) => string.Format(P(
            "The router port could not be opened ({0}). Your world is open anyway: Steam friends join with the code, others send you a let-in code.", "Не удалось открыть порт на роутере ({0}). Мир всё равно открыт: друзья в Steam зайдут по коду, остальные пришлют тебе код входа.", "No se pudo abrir el puerto del router ({0}). Tu mundo está abierto igualmente: los amigos de Steam entran con el código y los demás te envían un código de acceso."), error);
        public static string NeedsSteam => P("This code only works through Steam, and Steam is not available here.", "Этот код работает только через Steam, а Steam здесь недоступен.", "Este código solo funciona por Steam, y Steam no está disponible aquí.");
        public static string SteamHostLine => P("Steam friends see your world on their Join page and can join without any setup.", "Друзья в Steam увидят твой мир на странице подключения и зайдут без всяких настроек.", "Tus amigos de Steam ven tu mundo en su página para unirse y pueden entrar sin configurar nada.");
        public static string CodePending => P("Preparing the join code...", "Готовим код...", "Preparando el código...");
        public static string LetInHeading => P("Let a friend in", "Впустить друга", "Dejar entrar a un amigo");
        public static string LetInHelp => P(
            "Your provider blocks incoming connections, so friends who join without Steam send you a let-in code. Paste it here.", "Провайдер не пропускает входящие подключения, поэтому друзья без Steam присылают тебе код входа. Вставь его сюда.", "Tu proveedor bloquea las conexiones entrantes, así que los amigos que se unan sin Steam te envían un código de acceso. Pégalo aquí.");
        public static string LetInField => P("Friend's let-in code", "Код входа друга", "Código de acceso del amigo");
        public static string LetInPlaceholder => P("Paste the code your friend sent", "Вставь код, который прислал друг", "Pega el código que te envió tu amigo");
        public static string LetInButton => P("Let in", "Впустить", "Dejar entrar");
        public static string LetInStarted => P("Letting your friend in for a minute. They should get in within a few seconds.", "Впускаем друга: в течение минуты он должен зайти.", "Dejando entrar a tu amigo durante un minuto. Debería entrar en unos segundos.");
        public static string WaitingLetIn => P("Waiting for the host to let you in.", "Ждём, пока хост тебя впустит.", "Esperando a que el anfitrión te deje entrar.");
        public static string YourLetInCode(string code) => string.Format(P("Send the host this let-in code: <b>{0}</b>", "Отправь хосту этот код входа: <b>{0}</b>", "Envía al anfitrión este código de acceso: <b>{0}</b>"), code);
        public static string CopyLetIn => P("Copy let-in code", "Скопировать код входа", "Copiar código de acceso");
        public static string LetInPending => P("Preparing your let-in code...", "Готовим твой код входа...", "Preparando tu código de acceso...");
        public static string FriendsHosting => P("Steam friends with an open world", "Друзья в Steam с открытым миром", "Amigos de Steam con un mundo abierto");
        public static string JoinSteamFriend(string name) => P("Join: ", "Подключиться: ", "Unirse: ") + name;
        public static string FriendOtherVersion(string v) => string.Format(P("Your friend has Lycoreia {0}; you both need the same version.", "У друга Lycoreia {0}: версии у вас должны совпадать.", "Tu amigo tiene Lycoreia {0}; ambos necesitan la misma versión."), v);
        public static string LanLine(string v) => P("Same home network: ", "В той же домашней сети: ", "Misma red doméstica: ") + v;
        public static string HostFirewallWarning => P(
            "The Windows Firewall rule could not be added (no administrator rights). If Windows asked about Slime Rancher 2, allow it; otherwise allow SlimeRancher2.exe for private and public networks in the Windows Firewall settings.", "Не удалось добавить правило брандмауэра Windows (нет прав администратора). Если Windows спрашивала про Slime Rancher 2, разреши доступ; иначе разреши SlimeRancher2.exe для частных и общественных сетей в настройках брандмауэра Windows.", "No se pudo añadir la regla del Firewall de Windows (sin permisos de administrador). Si Windows te preguntó por Slime Rancher 2, permítelo; si no, permite SlimeRancher2.exe para redes privadas y públicas en la configuración del Firewall de Windows.");
        public static string HostUnreachable => P(
            "Could not reach the host. Check the code or address. If it is correct, the host's port is not reachable from the internet (provider NAT/CGNAT, a second router, a firewall or a VPN): the host should look at the warning on their hosting page. Steam works around this (Steam friends tab), and so do Radmin VPN, ZeroTier or PlayIt. On the same home network, join by the local address from the host's page.", "Не удалось связаться с хостом. Проверь код или адрес. Если всё верно, порт хоста не виден из интернета (NAT провайдера/CGNAT, второй роутер, брандмауэр или VPN): пусть хост посмотрит предупреждение на своей странице. Выручит Steam (вкладка «Друзья в Steam»), а также Radmin VPN, ZeroTier или PlayIt. Если вы в одной домашней сети, подключайся по локальному адресу со страницы хоста.", "No se pudo conectar con el anfitrión. Revisa el código o la dirección. Si es correcto, el puerto del anfitrión no es accesible desde internet (NAT del proveedor/CGNAT, un segundo router, un firewall o una VPN): el anfitrión debería mirar el aviso en su página. Steam lo soluciona (pestaña Amigos de Steam), y también Radmin VPN, ZeroTier o PlayIt. En la misma red doméstica, únete con la dirección local de la página del anfitrión.");
        public static string CopyCode => P("Copy join code", "Скопировать код", "Copiar código");
        public static string CodeCopied => P("Join code copied.", "Код скопирован.", "Código copiado.");
        public static string BoardButton => P("Co-op board: jobs, notes, expeditions", "Общая доска: задания, заметки, экспедиции", "Tablón cooperativo: tareas, notas, expediciones");
        public static string ResyncAll => P("Resync everyone", "Синхронизировать всех заново", "Resincronizar a todos");
        public static string ResyncSent => P("Resync sent to all players.", "Повторная синхронизация отправлена всем игрокам.", "Resincronización enviada a todos los jugadores.");
        public static string StopServer => P("Stop server", "Остановить сервер", "Detener servidor");
        public static string Connected => P("Connected", "Подключено", "Conectado");
        public static string RequestResync => P("Request resync", "Запросить синхронизацию", "Pedir resincronización");
        public static string ResyncRequested => P("Resync requested from the host.", "Синхронизация запрошена у хоста.", "Resincronización pedida al anfitrión.");
        public static string Disconnect => P("Disconnect", "Отключиться", "Desconectar");
        public static string SettingsHeading(bool firstTime) => firstTime ? P("Welcome! Choose your name", "Добро пожаловать! Выбери имя", "¡Bienvenido! Elige tu nombre") : Settings;
        public static string Username => P("Username", "Ник", "Nombre");
        public static string DefaultName => P("Player", "Игрок", "Jugador");
        public static string TabPalette => P("Palette", "Палитра", "Paleta");
        public static string TabHex => P("Hex code", "HEX-код", "Código HEX");
        public static string TabPicker => P("Picker", "Выбор", "Selector");
        public static string TabRandom => P("Random", "Случайный", "Aleatorio");
        public static string Preview => P("Preview:", "Так будет выглядеть:", "Vista previa:");
        public static string NameColor => P("Name color (hex)", "Цвет ника (HEX)", "Color del nombre (HEX)");
        public static string AllowCheats => P("Allow cheats (host)", "Разрешить читы (хост)", "Permitir trucos (anfitrión)");
        public static string DisableAchievements => P("Disable achievements", "Отключить достижения", "Desactivar logros");
        public static string FriendsVac => P("Friends can vac me", "Друзья могут вакать меня", "Mis amigos pueden aspirarme");
        public static string FriendsKick => P("Friends can kick me (V)", "Друзья могут пинать меня (V)", "Mis amigos pueden patearme (V)");
        public static string Save => P("Save", "Сохранить", "Guardar");
        public static string SettingsSaved => P("Settings saved.", "Настройки сохранены.", "Ajustes guardados.");
        public static string Players(int n) => P("Players (", "Игроки (", "Jugadores (") + n + ")";
        public static string Unnamed => P("(unnamed player)", "(без имени)", "(jugador sin nombre)");
        public static string WidescreenHeading => P("Widescreen FOV", "Угол обзора для широких экранов", "Campo de visión panorámico");
        public static string WidescreenInfo => P("Field of view is measured across a 16:9 screen; wider screens see more at the sides.", "Угол обзора считается для экрана 16:9, а на более широких экранах по бокам видно больше.", "El campo de visión se mide en una pantalla 16:9; las pantallas más anchas ven más por los lados.");
        public static string WidescreenUse => P("Use these settings (off = game's own FOV)", "Использовать эти настройки (если выключено, угол обзора как в игре)", "Usar estos ajustes (desactivado = campo de visión del juego)");
        public static string FovSlider(string min, string max) => P("Field of view (", "Угол обзора (", "Campo de visión (") + min + "-" + max + ")";
        public static string WidescreenHint => P("Lower values show less, but also stretch the screen edges less.", "Чем меньше значение, тем уже обзор, но края экрана растягиваются слабее.", "Con valores más bajos ves menos, pero los bordes de la pantalla se estiran menos.");
    }
}
