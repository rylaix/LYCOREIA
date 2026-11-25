// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections.Generic;

namespace Lycoreia
{
    public static class WhatsNew
    {
        public sealed class Item
        {
            public readonly string Title, Text, TryIt;
            public Item(string title, string text, string tryIt) { Title = title; Text = text; TryIt = tryIt ?? ""; }
        }
        public sealed class Section { public string Title, Tab; public List<Item> Items = new List<Item>(); }

        public static readonly List<Section> Sections = new List<Section>
        {
            new Section { Title = "New: play together through Steam", Tab = "Steam", Items =
            {
                new Item("Join through Steam", "Friends join your world through Steam: no port forwarding, and it works even when your provider blocks incoming connections (shared \"grey\" IP).", "Host your world. Your friend opens Multiplayer > Join a friend > Steam friends and clicks your name."),
                new Item("Steam friends list", "Steam friends with an open world show up on the Join page, and you get a notice when one of them opens their world.", "Ask a Steam friend to host, then open Multiplayer > Join a friend > Steam friends."),
                new Item("Let-in codes", "Playing without Steam and the host cannot be reached directly? You get a short let-in code, and the host pastes it on their hosting page.", "Join with a code. If the menu asks, send the host your let-in code."),
                new Item("Same house, same code", "Players behind the same router can join each other with a join code again.", "Join a friend on the same Wi-Fi with their code."),
                new Item("Spanish", "Lycoreia now speaks Spanish, and the Russian text was rewritten to read naturally.", "Choose Spanish in the game's language settings."),
            } },
            new Section { Title = "New: co-op features", Tab = "Co-op", Items =
            {
                new Item("Boost jump", "Players are now solid: stand on a friend and jump to reach much higher.", "Have a friend stand still, jump onto their head, then jump again."),
                new Item("Co-op wheel", "Hold the ping button (middle mouse) on a corral, garden, silo, Gordo or any spot to pin jobs for each other, claim them and tick them off. Jobs show on the compass and above the spot.", "Hold the middle mouse button while looking at a corral and pick \"Job: Feed this corral\"."),
                new Item("Expeditions", "Timed goals you play together (Plort Rush, Gordo Feast, Treasure Trail...). Finishing one pays the shared ranch.", "Pause > Multiplayer > Co-op board > Expeditions > Harvest Run, then vacuum up food together."),
                new Item("Slime notes", "Leave a note on a corral or a Gordo: everyone sees it floating above it.", "Hold the middle mouse button on a Gordo and pick a note, or \"write your own...\"."),
                new Item("Session recap", "What everyone did this session: plorts sold, slimes caught, money earned, jobs done.", "Pause > Multiplayer > Co-op board > Recap, or the main menu after you leave."),
                new Item("Update notice", "The main menu tells you when GitHub or Nexus Mods has a newer version.", "Shown at the top of the main menu as soon as the game starts."),
            } },
            new Section { Title = "Fixed", Tab = "Fixed", Items =
            {
                new Item("Radiant slimes", "Radiant slimes look radiant for everyone and stay radiant after a friend vacuums them up and shoots them out.", "Vacuum up a radiant slime and shoot it out next to your friend."),
                new Item("Shop for everyone", "A joining player buys in the shop (toys and more) just like in single player. Sold-out items and purchase limits are shared.", "Join a friend and buy something in the shop."),
                new Item("Grey Labyrinth", "Unstable objects in the Grey Labyrinth no longer multiply for the other player.", ""),
                new Item("Hosting explains problems", "The hosting page says why friends cannot reach you (shared provider address, a second router, a VPN, Windows Firewall) and what to do.", ""),
                new Item("Silos and collectors", "Silos, plort collectors and auto-feeders now sync, and collectors no longer double plorts.", "Collect plorts with a corral collector; both players see the same silo count."),
                new Item("Gates and doors", "Gates, ranch expansions, plort doors, puzzle slots and prisma barriers opened by one player open for everyone.", "Open a gate or buy an expansion; the other player sees it open."),
                new Item("Old saves", "Corrals and plot upgrades from saves made before playing together now appear for the guest too.", "Join a save that already has corrals."),
                new Item("Ringtails", "Ringtail slimes no longer multiply at dawn and dusk.", "Watch ringtails turn to stone and wake up with both players nearby."),
                new Item("Eaten food", "Food eaten by a slime disappears for everyone (no more endless chickens).", "Shoot a chicken into the other player's corral."),
                new Item("Avatars", "Other players' heads no longer spin, and avatars no longer spin during short lag.", ""),
                new Item("Leaving", "The host returning to the main menu now ends the session cleanly.", ""),
                new Item("Stability", "Fewer errors near unloaded doors; plorts no longer show up slime-sized.", ""),
            } },
        };

        public static readonly List<Section> SectionsRu = new List<Section>
        {
            new Section { Title = "Новое: игра вместе через Steam", Tab = "Steam", Items =
            {
                new Item("Подключение через Steam", "Друзья заходят в твой мир через Steam: порты пробрасывать не нужно, и всё работает, даже если провайдер не пропускает входящие подключения («серый» IP).", "Открой мир для друзей. Друг нажимает Multiplayer > Присоединиться к другу > Друзья в Steam и выбирает тебя."),
                new Item("Друзья в Steam", "Друзья в Steam с открытым миром видны на странице подключения, а когда кто-то из них открывает мир, приходит уведомление.", "Попроси друга в Steam открыть мир и загляни в Multiplayer > Присоединиться к другу > Друзья в Steam."),
                new Item("Коды входа", "Играешь без Steam, а к хосту не подключиться напрямую? Ты получишь короткий код входа, и хост вставит его на странице хостинга.", "Подключись по коду. Если меню попросит, отправь хосту свой код входа."),
                new Item("Из одного дома", "Игроки за одним роутером снова могут подключаться друг к другу по коду.", "Подключись по коду к другу в той же сети Wi-Fi."),
                new Item("Испанский язык", "Lycoreia теперь говорит по-испански, а русские тексты переписаны и звучат естественнее.", "Выбери испанский в настройках языка игры."),
            } },
            new Section { Title = "Новое в совместной игре", Tab = "Совместная игра", Items =
            {
                new Item("Прыжок с головы друга", "Игроки теперь твёрдые: встань на друга и прыгни, чтобы взлететь гораздо выше.", "Пусть друг постоит на месте, запрыгни ему на голову и прыгни ещё раз."),
                new Item("Колесо заданий", "Зажми кнопку метки (средняя кнопка мыши), глядя на загон, сад, силос, гордо или любое место: оставляй друг другу задания, бери их на себя и отмечай выполненными. Задания видны на компасе и над самим местом.", "Зажми среднюю кнопку мыши, глядя на загон, и выбери «Задание: накормить этот загон»."),
                new Item("Экспедиции", "Общие цели на время («Плортовая лихорадка», «Пир для гордо», «Тропа сокровищ»...). За выполненную экспедицию награду получает общее ранчо.", "Меню паузы > Multiplayer > Общая доска > Экспедиции > «Сбор урожая», а потом собирайте еду вместе."),
                new Item("Заметки", "Оставь заметку на загоне или у гордо: все увидят её над ним.", "Зажми среднюю кнопку мыши, глядя на гордо, и выбери заметку или «написать своё...»."),
                new Item("Итоги сессии", "Что сделал каждый за сессию: проданные плорты, пойманные слаймы, заработанные нью-баксы, выполненные задания.", "Меню паузы > Multiplayer > Общая доска > Итоги, а после выхода из игры они есть в главном меню."),
                new Item("Уведомление об обновлении", "Главное меню подскажет, если на GitHub или Nexus Mods вышла новая версия.", "Оно появляется вверху главного меню сразу после запуска игры."),
            } },
            new Section { Title = "Исправлено", Tab = "Исправлено", Items =
            {
                new Item("Лучистые слаймы", "Лучистые слаймы выглядят лучистыми у всех и остаются такими, даже если друг втянет их вакпаком и выстрелит обратно.", "Втяни лучистого слайма вакпаком и выстрели им рядом с другом."),
                new Item("Покупки в магазине", "Подключившийся игрок покупает в магазине (игрушки и не только) так же, как в одиночной игре. Распроданные товары и лимиты покупок общие.", "Подключись к другу и купи что-нибудь в магазине."),
                new Item("Серый лабиринт", "Нестабильные предметы в Сером лабиринте больше не размножаются у другого игрока.", ""),
                new Item("Подсказки при хостинге", "Страница хостинга объясняет, почему друзья не могут к тебе подключиться («серый» IP, второй роутер, VPN, брандмауэр Windows), и что с этим делать.", ""),
                new Item("Силосы и сборщики", "Силосы, плортосборники и автокормушки теперь синхронизируются, а плорты в сборниках больше не удваиваются.", "Собери плорты плортосборником в загоне: у обоих игроков в силосе будет одинаковое число."),
                new Item("Врата и двери", "Врата, расширения ранчо, врата плортов, ячейки головоломок и призматические барьеры, открытые одним игроком, открываются у всех.", "Открой врата или купи расширение: второй игрок увидит, что оно открылось."),
                new Item("Старые сохранения", "Загоны и улучшения участков из сохранений, созданных до совместной игры, теперь видны и гостю.", "Подключись к сохранению, где уже есть загоны."),
                new Item("Кольцехвосты", "Кольцехвостые слаймы больше не размножаются на рассвете и закате.", "Понаблюдай, как кольцехвосты каменеют и просыпаются, пока оба игрока рядом."),
                new Item("Съеденная еда", "Еда, которую съел слайм, исчезает у всех: бесконечных куриц больше нет.", "Выстрели курицей в загон другого игрока."),
                new Item("Аватары", "Головы других игроков больше не вертятся, а аватары не крутятся при коротких лагах.", ""),
                new Item("Выход из игры", "Когда хост выходит в главное меню, сессия завершается корректно.", ""),
                new Item("Стабильность", "Меньше ошибок рядом с незагруженными дверями, а плорты больше не появляются размером со слайма.", ""),
            } },
        };

        public static readonly List<Section> SectionsEs = new List<Section>
        {
            new Section { Title = "Novedad: juega con amigos por Steam", Tab = "Steam", Items =
            {
                new Item("Únete por Steam", "Tus amigos entran en tu mundo por Steam: sin abrir puertos, y funciona aunque tu proveedor bloquee las conexiones entrantes (IP compartida).", "Abre tu mundo. Tu amigo pulsa Multiplayer > Unirse a un amigo > Amigos de Steam y elige tu nombre."),
                new Item("Amigos de Steam", "Tus amigos de Steam con un mundo abierto aparecen en la página para unirse, y recibes un aviso cuando alguno abre su mundo.", "Pide a un amigo de Steam que abra su mundo y mira Multiplayer > Unirse a un amigo > Amigos de Steam."),
                new Item("Códigos de acceso", "¿Juegas sin Steam y no puedes conectar directamente con el anfitrión? Recibes un código de acceso corto, y el anfitrión lo pega en su página.", "Únete con un código. Si el menú lo pide, envía tu código de acceso al anfitrión."),
                new Item("Desde la misma casa", "Los jugadores conectados al mismo router vuelven a poder unirse con un código.", "Únete con su código a un amigo que esté en tu misma red Wi-Fi."),
                new Item("Español", "Lycoreia ahora habla español, y los textos en ruso se reescribieron para que suenen naturales.", "Elige español en los ajustes de idioma del juego."),
            } },
            new Section { Title = "Novedades del modo cooperativo", Tab = "Cooperativo", Items =
            {
                new Item("Salto impulsado", "Los jugadores ahora son sólidos: súbete encima de un amigo y salta para llegar mucho más alto.", "Pide a un amigo que se quede quieto, salta sobre su cabeza y vuelve a saltar."),
                new Item("Rueda cooperativa", "Mantén el botón de marcar (botón central del ratón) sobre un corral, un jardín, un silo, un Gordo o cualquier lugar para fijar tareas para el grupo, tomarlas y marcarlas como hechas. Las tareas aparecen en la brújula y sobre el lugar.", "Mantén el botón central del ratón mientras miras un corral y elige «Tarea: Alimentar este corral»."),
                new Item("Expediciones", "Metas con tiempo para jugar en equipo (Fiebre del Plort, Festín de Gordos, Ruta del Tesoro...). Completar una da newbucks al rancho compartido.", "Pausa > Multiplayer > Tablón cooperativo > Expediciones > Gran Cosecha, y después recoge comida con tus amigos."),
                new Item("Notas", "Deja una nota en un corral o un Gordo: todos la verán flotando encima.", "Mantén el botón central del ratón sobre un Gordo y elige una nota, o «escribir la tuya...»."),
                new Item("Resumen de la sesión", "Lo que hizo cada uno en la sesión: plorts vendidos, slimes atrapados, newbucks ganados, tareas completadas.", "Pausa > Multiplayer > Tablón cooperativo > Resumen, o el menú principal después de salir."),
                new Item("Aviso de actualización", "El menú principal te avisa cuando hay una versión nueva en GitHub o Nexus Mods.", "Aparece arriba en el menú principal nada más iniciar el juego."),
            } },
            new Section { Title = "Correcciones", Tab = "Correcciones", Items =
            {
                new Item("Slimes radiantes", "Los slimes radiantes se ven radiantes para todos y lo siguen siendo aunque un amigo los aspire y los dispare.", "Aspira un slime radiante y dispáralo junto a tu amigo."),
                new Item("Compras en la tienda", "El jugador invitado compra en la tienda (juguetes y más) igual que en una partida individual. Los artículos agotados y los límites de compra se comparten.", "Únete a un amigo y compra algo en la tienda."),
                new Item("El Laberinto Gris", "Los objetos inestables del Laberinto Gris ya no se multiplican para el otro jugador.", ""),
                new Item("Ayuda para anfitriones", "La página de anfitrión explica por qué tus amigos no pueden conectar (IP compartida, un segundo router, una VPN, el Firewall de Windows) y qué hacer.", ""),
                new Item("Silos y recolectores", "Los silos, los recolectores de plorts y los autoalimentadores ahora se sincronizan, y los recolectores ya no duplican plorts.", "Recoge plorts con el recolector de un corral; ambos jugadores ven la misma cantidad en el silo."),
                new Item("Portones y puertas", "Los portones, las expansiones de rancho, las puertas de plorts, las ranuras de puzle y las barreras prismáticas que abre un jugador se abren para todos.", "Abre un portón o compra una expansión; el otro jugador la verá abierta."),
                new Item("Partidas antiguas", "Los corrales y las mejoras de parcelas de partidas creadas antes de jugar juntos ahora también aparecen para el invitado.", "Únete a una partida que ya tenga corrales."),
                new Item("Colianillados", "Los slimes colianillados ya no se multiplican al amanecer y al anochecer.", "Mira cómo los colianillados se convierten en piedra y despiertan con ambos jugadores cerca."),
                new Item("Comida que desaparece", "La comida que se come un slime desaparece para todos (se acabaron las gallinas infinitas).", "Dispara una gallina al corral del otro jugador."),
                new Item("Avatares", "Las cabezas de los demás jugadores ya no giran, y los avatares ya no dan vueltas con el lag breve.", ""),
                new Item("Salir", "Cuando el anfitrión vuelve al menú principal, la sesión termina correctamente.", ""),
                new Item("Estabilidad", "Menos errores cerca de puertas no cargadas; los plorts ya no aparecen del tamaño de un slime.", ""),
            } },
        };

        /// <summary>The release notes in the given game language ("ru", "es", anything else = English).</summary>
        public static List<Section> For(string lang) => lang == "ru" ? SectionsRu : lang == "es" ? SectionsEs : Sections;
    }
}
