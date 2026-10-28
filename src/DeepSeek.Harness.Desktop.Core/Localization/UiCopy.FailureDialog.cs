namespace DeepSeek.Harness.Desktop.Core.Localization;

/// <summary>
/// 宿主 UI 文案词典的失败面分部（ADR native-failure-surface）：原生对话框的用户可见文案；按域立分部是为守 F1
/// （单文件行数）。词典唯一事实源语义与门禁口径见 <see cref="UiCopy"/>。
/// </summary>
public static partial class UiCopy
{
    /// <summary>失败面对话框标题（问题前置：按钮固定为是／否，插件默认按钮是「是」）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>对话框标题。</returns>
    public static string RecoveryDialogTitle(UiLanguage lang) => Pick(lang,
        zh: "DeepSeek Harness Desktop — 运行时恢复：要退出应用吗？",
        en: "DeepSeek Harness Desktop — runtime recovery: quit the app?",
        ja: "DeepSeek Harness Desktop — ランタイムの復旧：アプリを終了しますか？",
        es: "DeepSeek Harness Desktop — recuperación del tiempo de ejecución: ¿salir de la aplicación?",
        fr: "DeepSeek Harness Desktop — récupération de l'exécution : quitter l'application ?",
        de: "DeepSeek Harness Desktop — Laufzeitwiederherstellung: App beenden?",
        ru: "DeepSeek Harness Desktop — восстановление среды выполнения: выйти из приложения?",
        pt: "DeepSeek Harness Desktop — recuperação do tempo de execução: sair do aplicativo?");

    /// <summary>失败面对话框：stderr 尾部小节标题。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>小节标题。</returns>
    public static string RecoveryDialogTailHeading(UiLanguage lang) => Pick(lang,
        zh: "运行时 stderr 尾部：", en: "Runtime stderr (tail):",
        ja: "ランタイム stderr（末尾）：", es: "stderr del tiempo de ejecución (final):",
        fr: "stderr de l'exécution (fin) :", de: "Laufzeit-stderr (Ende):",
        ru: "stderr среды выполнения (хвост):", pt: "stderr do tempo de execução (final):");

    /// <summary>失败面对话框：诊断包已导出行前缀（后接路径）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>前缀文案。</returns>
    public static string RecoveryDialogExportPrefix(UiLanguage lang) => Pick(lang,
        zh: "诊断包已导出：", en: "Diagnostics exported: ",
        ja: "診断情報をエクスポートしました：", es: "Diagnósticos exportados: ",
        fr: "Diagnostics exportés : ", de: "Diagnose exportiert: ",
        ru: "Диагностика экспортирована: ", pt: "Diagnósticos exportados: ");

    /// <summary>失败面对话框：诊断包导出失败行（求助路径的退路：host.log）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败行文案。</returns>
    public static string RecoveryDialogExportFailed(UiLanguage lang) => Pick(lang,
        zh: "诊断包导出失败（详见 host.log）。",
        en: "Diagnostics export failed (see host.log).",
        ja: "診断情報のエクスポートに失敗しました（host.log を参照）。",
        es: "Error al exportar los diagnósticos (consulte host.log).",
        fr: "Échec de l'exportation des diagnostics (voir host.log).",
        de: "Diagnose-Export fehlgeschlagen (siehe host.log).",
        ru: "Не удалось экспортировать диагностику (см. host.log).",
        pt: "Falha ao exportar os diagnósticos (consulte host.log).");

    /// <summary>失败面对话框：动作说明（按钮固定为是／否；只有显式「是」才退出）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>动作说明。</returns>
    public static string RecoveryDialogActions(UiLanguage lang) => Pick(lang,
        zh: "选「是」退出应用；选「否」继续在后台自动恢复。",
        en: "Choose Yes to quit the app; choose No to keep auto-recovering.",
        ja: "「はい」でアプリを終了します。「いいえ」でバックグラウンドの自動復旧を続けます。",
        es: "Elija «Sí» para salir de la aplicación; «No» para seguir recuperándose en segundo plano.",
        fr: "Choisissez « Oui » pour quitter l'application ; « Non » pour continuer la récupération en arrière-plan.",
        de: "Wählen Sie „Ja“, um die App zu beenden; „Nein“, um die automatische Wiederherstellung fortzusetzen.",
        ru: "Выберите «Да», чтобы выйти из приложения; «Нет» — чтобы продолжить автоматическое восстановление.",
        pt: "Escolha “Sim” para sair do aplicativo; “Não” para continuar a recuperação automática.");

    /// <summary>启动失败对话框标题（dsh 运行时起不来；新宿主进程内模态）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>对话框标题。</returns>
    public static string StartupFailureTitle(UiLanguage lang) => Pick(lang,
        zh: "DeepSeek Harness Desktop — 启动失败",
        en: "DeepSeek Harness Desktop — startup failed",
        ja: "DeepSeek Harness Desktop — 起動に失敗しました",
        es: "DeepSeek Harness Desktop — error de inicio",
        fr: "DeepSeek Harness Desktop — échec du démarrage",
        de: "DeepSeek Harness Desktop — Start fehlgeschlagen",
        ru: "DeepSeek Harness Desktop — сбой запуска",
        pt: "DeepSeek Harness Desktop — falha na inicialização");

    /// <summary>启动失败原因（正文首行）：只说可确认的事实，细节指向日志。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>原因文案。</returns>
    public static string StartupFailureReason(UiLanguage lang) => Pick(lang,
        zh: "dsh 运行时未能启动（详见 host.log）。",
        en: "The dsh runtime could not be started (see host.log for details).",
        ja: "dsh ランタイムを起動できませんでした（詳細は host.log を参照）。",
        es: "No se pudo iniciar el tiempo de ejecución de dsh (consulte host.log).",
        fr: "Impossible de démarrer l'exécution dsh (voir host.log pour les détails).",
        de: "Die dsh-Laufzeit konnte nicht gestartet werden (Details siehe host.log).",
        ru: "Не удалось запустить среду выполнения dsh (подробности в host.log).",
        pt: "Não foi possível iniciar o tempo de execução do dsh (consulte host.log).");

    /// <summary>启动失败对话框动作说明：退出结束进程，留在窗口保留现场。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>动作说明。</returns>
    public static string StartupFailureActions(UiLanguage lang) => Pick(lang,
        zh: "选择「退出应用」结束进程；选择「留在窗口」保留窗口（重新启动应用即可再试）。",
        en: "Choose \"Quit App\" to end the process; choose \"Stay\" to keep the window open (restart the app to try again).",
        ja: "「アプリを終了」でプロセスを終了します。「ウィンドウに残る」でウィンドウを開いたままにします（アプリを再起動すると再試行できます）。",
        es: "Elija «Salir de la aplicación» para terminar el proceso; «Permanecer» para mantener la ventana abierta (reinicie la aplicación para reintentar).",
        fr: "Choisissez « Quitter l'application » pour terminer le processus ; « Rester » pour garder la fenêtre ouverte (redémarrez l'application pour réessayer).",
        de: "Wählen Sie „App beenden“, um den Prozess zu beenden; „Bleiben“, um das Fenster offen zu halten (App neu starten, um es erneut zu versuchen).",
        ru: "Выберите «Выйти из приложения», чтобы завершить процесс; «Остаться» — чтобы оставить окно открытым (перезапустите приложение, чтобы повторить).",
        pt: "Escolha “Sair do aplicativo” para encerrar o processo; “Permanecer” para manter a janela aberta (reinicie o aplicativo para tentar novamente).");

    /// <summary>失败面对话框：留在窗口按钮（非破坏性动作，也是关闭框的缺省）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>按钮文案。</returns>
    public static string DialogStayLabel(UiLanguage lang) => Pick(lang,
        zh: "留在窗口", en: "Stay", ja: "ウィンドウに残る",
        es: "Permanecer", fr: "Rester",
        de: "Bleiben", ru: "Остаться", pt: "Permanecer");

    /// <summary>WebView2 缺失对话框标题（Windows 启动前置；ADR avalonia-g4-webview2-preflight）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>对话框标题。</returns>
    public static string WebView2MissingTitle(UiLanguage lang) => Pick(lang,
        zh: "DeepSeek Harness Desktop — 缺少 WebView2 运行时",
        en: "DeepSeek Harness Desktop — WebView2 runtime missing",
        ja: "DeepSeek Harness Desktop — WebView2 ランタイムが見つかりません",
        es: "DeepSeek Harness Desktop — falta el tiempo de ejecución WebView2",
        fr: "DeepSeek Harness Desktop — environnement d'exécution WebView2 manquant",
        de: "DeepSeek Harness Desktop — WebView2-Laufzeit fehlt",
        ru: "DeepSeek Harness Desktop — отсутствует среда выполнения WebView2",
        pt: "DeepSeek Harness Desktop — tempo de execução do WebView2 ausente");

    /// <summary>WebView2 缺失正文：后果 + 安装指引 + 是/否动作（原生模态按钮固定为是／否）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>正文文案。</returns>
    public static string WebView2MissingBody(UiLanguage lang) => Pick(lang,
        zh: "未检测到 Microsoft Edge WebView2 运行时（或其文件缺失），应用窗口无法渲染。\n\n请安装 Evergreen 运行时后重新启动应用。\n\n选择「是」打开下载页；选择「否」退出。",
        en: "Microsoft Edge WebView2 Runtime is not installed (or its files are missing), so the app window cannot be rendered.\n\nInstall the Evergreen Runtime, then start the app again.\n\nChoose Yes to open the download page; choose No to quit.",
        ja: "Microsoft Edge WebView2 ランタイムがインストールされていない（またはファイルが欠落している）ため、アプリのウィンドウを描画できません。\n\nEvergreen ランタイムをインストールしてから、アプリを再起動してください。\n\n「はい」でダウンロードページを開きます。「いいえ」で終了します。",
        es: "El tiempo de ejecución de Microsoft Edge WebView2 no está instalado (o faltan sus archivos), por lo que la ventana de la aplicación no puede mostrarse.\n\nInstale el tiempo de ejecución Evergreen y vuelva a iniciar la aplicación.\n\nElija «Sí» para abrir la página de descarga; «No» para salir.",
        fr: "Le runtime Microsoft Edge WebView2 n'est pas installé (ou ses fichiers sont manquants), la fenêtre de l'application ne peut donc pas être rendue.\n\nInstallez le runtime Evergreen, puis redémarrez l'application.\n\nChoisissez « Oui » pour ouvrir la page de téléchargement ; « Non » pour quitter.",
        de: "Die Microsoft Edge WebView2-Runtime ist nicht installiert (oder ihre Dateien fehlen), daher kann das App-Fenster nicht dargestellt werden.\n\nInstallieren Sie die Evergreen-Runtime und starten Sie die App erneut.\n\nWählen Sie „Ja“, um die Download-Seite zu öffnen; „Nein“, um die App zu beenden.",
        ru: "Среда выполнения Microsoft Edge WebView2 не установлена (или её файлы отсутствуют), поэтому окно приложения не может быть отрисовано.\n\nУстановите среду выполнения Evergreen и снова запустите приложение.\n\nВыберите «Да», чтобы открыть страницу загрузки; «Нет» — чтобы выйти.",
        pt: "O tempo de execução do Microsoft Edge WebView2 não está instalado (ou seus arquivos estão ausentes), portanto a janela do aplicativo não pode ser renderizada.\n\nInstale o tempo de execução Evergreen e inicie o aplicativo novamente.\n\nEscolha “Sim” para abrir a página de download; “Não” para sair.");
}
