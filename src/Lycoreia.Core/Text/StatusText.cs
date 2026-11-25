// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Text
{
    public static class StatusText
    {
        static string P(string en, string ru, string es) => CoopText.Pick(en, ru, es);

        // ---- menu input checks
        public static string PortRange => P("The port must be a number from 1 to 65535.", "Порт должен быть числом от 1 до 65535.", "El puerto debe ser un número entre 1 y 65535.");
        public static string BadJoinCode => P("That join code is not valid.", "Этот код не подходит.", "Ese código no es válido.");
        public static string EnterHostIp => P("Enter the host's IP address.", "Введи IP-адрес хоста.", "Escribe la dirección IP del anfitrión.");
        public static string NoLoopback => P("127.0.0.1 is not allowed. Use the host's LAN or public IP (PlayIt: the left-side address).", "127.0.0.1 не подойдёт. Укажи локальный или внешний IP хоста (для PlayIt: адрес слева).", "127.0.0.1 no sirve. Usa la IP local o pública del anfitrión (con PlayIt: la dirección de la izquierda).");
        public static string EnterName => P("Enter a name.", "Введи имя.", "Escribe un nombre.");
        public static string NameTooLong => P("The name can be at most 24 characters.", "Имя может быть не длиннее 24 символов.", "El nombre puede tener como máximo 24 caracteres.");
        public static string BadColour => P("The colour must be a hex value like #4D95CB.", "Цвет нужно указать в HEX, например #4D95CB.", "El color debe ser un valor HEX, como #4D95CB.");
        public static string InvalidLetIn => P("That let-in code is not valid.", "Этот код входа не подходит.", "Ese código de acceso no es válido.");

        // ---- automatic hosting
        public static string AutoCancelled => P("Automatic setup cancelled. You can retry or host on a port.", "Автонастройка отменена. Можно попробовать ещё раз или открыть мир на своём порту.", "Configuración automática cancelada. Puedes reintentarlo o abrir el mundo en un puerto.");
        public static string AutoTimedOut => P("Automatic setup timed out. Retry, or host on a forwarded port.", "Автонастройка не уложилась по времени. Попробуй ещё раз или открой мир на проброшенном порту.", "La configuración automática tardó demasiado. Reinténtalo o abre el mundo en un puerto redirigido.");
        public static string AutoFailed(string detail) => string.Format(P("Automatic hosting failed: {0}", "Не удалось открыть мир автоматически: {0}", "No se pudo abrir el mundo automáticamente: {0}"), detail);
        public static string ServerStartFailed(string detail) => string.Format(P("Starting the server failed: {0}", "Не удалось запустить сервер: {0}", "No se pudo iniciar el servidor: {0}"), detail);
        public static string StartingServer => P("Starting server...", "Запуск сервера...", "Iniciando el servidor...");
        /// <summary>The automatic-hosting stage names (AutoHost reports them in English for the logs).</summary>
        public static string Stage(string english) => english switch
        {
            "Releasing previous mapping" => P(english, "Освобождаем старый порт", "Liberando el puerto anterior"),
            "Finding public address" => P(english, "Определяем внешний адрес", "Buscando la dirección pública"),
            "Finding router" => P(english, "Ищем роутер", "Buscando el router"),
            "Opening router port" => P(english, "Открываем порт на роутере", "Abriendo el puerto del router"),
            "Checking router address" => P(english, "Проверяем адрес роутера", "Comprobando la dirección del router"),
            "Configuring firewall" => P(english, "Настраиваем брандмауэр", "Configurando el firewall"),
            "Starting server socket" => P(english, "Запускаем сервер", "Iniciando el servidor"),
            _ => english,
        };
        public static string AutoHost(AutoHostFailure kind, string detail) => string.Format(kind switch
        {
            AutoHostFailure.PublicIp => P("Could not find your public IP address. Check your internet connection.{0}", "Не удалось узнать твой внешний IP-адрес. Проверь подключение к интернету.{0}", "No se pudo averiguar tu dirección IP pública. Revisa tu conexión a internet.{0}"),
            AutoHostFailure.DiscoveryTimeout => P("Router discovery timed out.{0} Forward the port by hand or use a tunnel such as PlayIt.", "Роутер не ответил вовремя.{0} Пробрось порт вручную или используй туннель вроде PlayIt.", "El router no respondió a tiempo.{0} Redirige el puerto a mano o usa un túnel como PlayIt."),
            AutoHostFailure.DiscoveryFailed => P("Router discovery failed.{0} Forward the port by hand or use a tunnel such as PlayIt.", "Не удалось найти роутер.{0} Пробрось порт вручную или используй туннель вроде PlayIt.", "No se pudo encontrar el router.{0} Redirige el puerto a mano o usa un túnel como PlayIt."),
            AutoHostFailure.NoRouter => P("No router answered UPnP or NAT-PMP.{0} If UPnP is enabled on the router, a firewall (UDP 1900/5351) or a VPN/virtual adapter may be blocking discovery; you can forward the port by hand or use PlayIt.",
                "Роутер не ответил по UPnP или NAT-PMP.{0} Если UPnP на роутере включён, поиск может блокировать брандмауэр (UDP 1900/5351) или VPN/виртуальный адаптер; порт можно пробросить вручную или использовать PlayIt.",
                "Ningún router respondió por UPnP o NAT-PMP.{0} Si UPnP está activado en el router, puede que un firewall (UDP 1900/5351) o una VPN/adaptador virtual bloquee la búsqueda; puedes redirigir el puerto a mano o usar PlayIt."),
            _ => P("Could not open a port on your router. Forward a UDP port by hand or use a tunnel such as PlayIt.{0}", "Не удалось открыть порт на роутере. Пробрось UDP-порт вручную или используй туннель вроде PlayIt.{0}", "No se pudo abrir un puerto en tu router. Redirige un puerto UDP a mano o usa un túnel como PlayIt.{0}"),
        }, string.IsNullOrWhiteSpace(detail) ? "" : " (" + detail.Trim() + ")");

        // ---- session
        public static string ServerClosed => P("Server closed!", "Сервер закрыт.", "Servidor cerrado.");
        public static string WorldOpen => P("The world is now open to others!", "Мир открыт для друзей!", "¡Tu mundo ya está abierto para otros!");
        public static string YouDisconnected => P("You disconnected from the world!", "Отключение от мира.", "Te desconectaste del mundo.");
        public static string LostConnection => P("Lost connection to the host.", "Связь с хостом потеряна.", "Se perdió la conexión con el anfitrión.");
        public static string NotLetIn => P("The host did not let you in. Send the host your let-in code and ask them to paste it on their hosting page.", "Хост тебя не впустил. Отправь ему свой код входа и попроси вставить его на странице хостинга.", "El anfitrión no te dejó entrar. Envíale tu código de acceso y pídele que lo pegue en su página de anfitrión.");
        public static string NoAnswer => P("The host did not answer in time. Its port is probably not reachable from the internet (provider NAT/CGNAT, firewall or VPN).", "Хост не ответил вовремя. Скорее всего, его порт не виден из интернета (NAT провайдера/CGNAT, брандмауэр или VPN).", "El anfitrión no respondió a tiempo. Probablemente su puerto no es accesible desde internet (NAT del proveedor/CGNAT, firewall o VPN).");
        public static string CouldNotJoin => P("Could not join the world, check the MelonLoader console for details.", "Не удалось подключиться к миру. Подробности в консоли MelonLoader.", "No se pudo unir al mundo. Mira la consola de MelonLoader para más detalles.");
        public static string NoServerAnswered => P("No server answered. If you connect over the internet, check that the host's tunnel or port forward is up and that the firewall allows UDP.", "Сервер не ответил. Если подключаешься через интернет, проверь, что у хоста работает туннель или проброс порта, а брандмауэр пропускает UDP.", "Ningún servidor respondió. Si te conectas por internet, comprueba que el túnel o la redirección de puertos del anfitrión funcionan y que el firewall permite UDP.");
        public static string Denied(string reason) => P("Connection was denied! ", "В подключении отказано. ", "Conexión rechazada. ") + reason;
        public static string PortInUse => P("That port is already in use. Pick another port.", "Этот порт уже занят. Выбери другой.", "Ese puerto ya está en uso. Elige otro.");
        public static string CannotHostAsGuest => P("Cannot host while connected to another world.", "Нельзя открыть свой мир, пока идёт подключение к чужому.", "No puedes abrir tu mundo mientras estás conectado a otro.");
        public static string AlreadyRunning => P("A server is already running.", "Сервер уже запущен.", "Ya hay un servidor en marcha.");
        public static string StopHostingFirst => P("You are hosting a world; stop it before connecting.", "Твой мир сейчас открыт: закрой его перед подключением.", "Tienes un mundo abierto; ciérralo antes de conectarte.");
        public static string StartHostingFirst => P("Start hosting first.", "Сначала открой мир.", "Primero abre tu mundo.");

        // ---- chat lines
        public static string Welcome(string name, string version) => string.Format(P("Welcome to {0} {1}! Press Enter to chat, F5 to hide the chat.", "Добро пожаловать в {0} {1}! Enter открывает чат, F5 скрывает его.", "¡Bienvenido a {0} {1}! Pulsa Enter para chatear y F5 para ocultar el chat."), name, version);
        public static string PlayerJoined(string name) => string.Format(P("{0} joined the world!", "{0} заходит в мир!", "¡{0} se unió al mundo!"), name);
        public static string PlayerLeft(string name) => string.Format(P("{0} left the world!", "{0} покидает мир.", "{0} salió del mundo."), name);

        // ---- main-menu launch (DirectLaunch)
        public static string CreatingSave => P("Creating a fresh co-op save...", "Создаём новое сохранение для совместной игры...", "Creando una partida cooperativa nueva...");
        public static string Loading(bool hosting) => hosting ? P("Loading the world, then starting the server...", "Загружаем мир, потом запускаем сервер...", "Cargando el mundo para abrirlo...") : P("Loading the world, then joining...", "Загружаем мир, потом подключаемся...", "Cargando el mundo para unirte...");
        public static string Loaded(bool hosting) => hosting ? P("World loaded, starting the server...", "Мир загружен, запускаем сервер...", "Mundo cargado, iniciando el servidor...") : P("World loaded, joining...", "Мир загружен, подключаемся...", "Mundo cargado, uniéndote...");
        public static string StartingTheServer => P("Starting the server...", "Запускаем сервер...", "Iniciando el servidor...");
        public static string LoadFailed => P("Could not load that save. Load it from the game's menu, then use Multiplayer in the pause menu.", "Не удалось загрузить это сохранение. Загрузи его через меню игры, а потом открой Multiplayer в меню паузы.", "No se pudo cargar esa partida. Cárgala desde el menú del juego y luego usa Multiplayer en el menú de pausa.");
        public static string SlotsFullPickBelow => P("All save slots are full. Pick a save below to use for joining (it will be replaced by the host's world), or delete one in the game's Load menu.", "Все ячейки сохранений заняты. Выбери ниже сохранение для подключения (его заменит мир хоста) или удали одно в меню загрузки игры.", "Todas las ranuras de guardado están llenas. Elige abajo una partida para unirte (se sustituirá por el mundo del anfitrión) o borra una en el menú Cargar juego.");
        public static string SlotsFull => P("All save slots are full. Pick a save to use for joining, or delete one in the game's Load menu.", "Все ячейки сохранений заняты. Выбери сохранение для подключения или удали одно в меню загрузки игры.", "Todas las ranuras de guardado están llenas. Elige una partida para unirte o borra una en el menú Cargar juego.");
        public static string TookTooLong => P("Loading the world took too long. Use Multiplayer in the pause menu once you are in the game.", "Мир загружался слишком долго. Когда окажешься в игре, открой Multiplayer в меню паузы.", "El mundo tardó demasiado en cargar. Cuando estés en el juego, usa Multiplayer en el menú de pausa.");
        public static string WentWrong => P("Something went wrong while loading. Use Multiplayer in the pause menu instead.", "Что-то пошло не так при загрузке. Открой Multiplayer в меню паузы.", "Algo salió mal al cargar. Usa Multiplayer en el menú de pausa.");
        public static string NoNewGameScreen => P("Could not open the game's New Game screen. Start a new game yourself, then join from the pause menu.", "Не удалось открыть экран новой игры. Начни новую игру вручную, а потом подключись из меню паузы.", "No se pudo abrir la pantalla de Nuevo juego. Empieza una partida nueva y únete desde el menú de pausa.");
    }
}
