namespace DeepSeek.Harness.Desktop.Core.Localization;

/// <summary>
/// 宿主 UI 文案词典的界面面（横幅/恢复页/标题栏）：按域立分部以守文件尺寸（F1）。
/// 词典唯一事实源语义与门禁口径见 <see cref="UiCopy"/>。
/// </summary>
public static partial class UiCopy
{
    // ======== 横幅（DesktopBanner / UpdateBanner / UiLocale） ========

    /// <summary>横幅确认按钮（横幅共用，随当前 locale）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>按钮文案。</returns>
    public static string OkLabel(UiLanguage lang) => Pick(lang,
        zh: "知道了", en: "OK", ja: "OK",
        es: "Entendido", fr: "Compris",
        de: "Verstanden", ru: "Понятно", pt: "Entendi");

    /// <summary>自更新就绪横幅正文。</summary>
    /// <param name="version">新版本号。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>横幅正文。</returns>
    public static string UpdateReadyText(string version, UiLanguage lang) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        Pick(lang,
            zh: "新版本 {0} 已就绪，可在 设置 → 桌面设置 中一键安装。",
            en: "New version {0} is ready. Install it in Settings → Desktop Settings.",
            ja: "新しいバージョン {0} の準備ができました。設定 → デスクトップ設定 からインストールできます。",
            es: "La nueva versión {0} está lista. Instálela en Ajustes → Ajustes de escritorio.",
            fr: "La nouvelle version {0} est prête. Installez-la dans Réglages → Réglages du bureau.",
            de: "Die neue Version {0} ist bereit. Installieren Sie sie unter Einstellungen → Desktop-Einstellungen.",
            ru: "Новая версия {0} готова. Установите её в разделе Настройки → Настройки рабочего стола.",
            pt: "A nova versão {0} está pronta. Instale-a em Configurações → Configurações da área de trabalho."),
        version);

    /// <summary>dsh 版本底线横幅正文（检测版本低于底线）。</summary>
    /// <param name="detectedVersion">检测到的 dsh 版本。</param>
    /// <param name="minimumVersion">兼容底线版本。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>横幅正文。</returns>
    public static string VersionFloorBannerText(string detectedVersion, string minimumVersion, UiLanguage lang) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        Pick(lang,
            zh: "当前 dsh 版本 {0} 低于桌面支持的最低版本 {1}，可能出现数据或行为不兼容；请升级 dsh 至 next 通道。",
            en: "Current dsh version {0} is below the minimum supported by this desktop ({1}); data or behavior may be incompatible. Upgrade dsh to the next channel.",
            ja: "現在の dsh バージョン {0} はこのデスクトップが対応する最低バージョン（{1}）を下回っています。データや動作に互換性の問題が生じる可能性があります。dsh を next チャンネルにアップグレードしてください。",
            es: "La versión actual de dsh {0} está por debajo del mínimo admitido por este escritorio ({1}); los datos o el comportamiento pueden ser incompatibles. Actualice dsh al canal next.",
            fr: "La version actuelle de dsh {0} est inférieure au minimum pris en charge par ce bureau ({1}) ; les données ou le comportement peuvent être incompatibles. Mettez à jour dsh vers le canal next.",
            de: "Die aktuelle dsh-Version {0} liegt unter dem von diesem Desktop unterstützten Minimum ({1}); Daten oder Verhalten können inkompatibel sein. Aktualisieren Sie dsh auf den next-Kanal.",
            ru: "Текущая версия dsh {0} ниже минимальной, поддерживаемой этим приложением ({1}); данные или поведение могут быть несовместимы. Обновите dsh до канала next.",
            pt: "A versão atual do dsh {0} está abaixo do mínimo suportado por este aplicativo ({1}); dados ou comportamento podem ser incompatíveis. Atualize o dsh para o canal next."),
        detectedVersion, minimumVersion);

    /// <summary>非受控退出横幅正文。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>横幅正文。</returns>
    public static string UncleanExitBannerText(UiLanguage lang) => Pick(lang,
        zh: "上次运行未正常退出（如手动结束进程）。若应用行为异常，请在设置页导出诊断信息。",
        en: "The app did not exit cleanly last time (e.g. the process was killed). If it behaves oddly, export diagnostics in Settings.",
        ja: "前回の終了は正常ではありませんでした（プロセスを強制終了したなど）。動作がおかしい場合は、設定で診断情報をエクスポートしてください。",
        es: "La aplicación no se cerró correctamente la última vez (p. ej., se terminó el proceso). Si su comportamiento es extraño, exporte los diagnósticos en Ajustes.",
        fr: "L'application ne s'est pas fermée correctement la dernière fois (par ex. processus tué). Si elle se comporte étrangement, exportez les diagnostics dans Réglages.",
        de: "Die App wurde beim letzten Mal nicht sauber beendet (z. B. Prozess beendet). Bei auffälligem Verhalten exportieren Sie die Diagnose in den Einstellungen.",
        ru: "В прошлый раз приложение завершилось некорректно (например, процесс был завершён принудительно). Если поведение кажется странным, экспортируйте диагностику в настройках.",
        pt: "O aplicativo não foi encerrado corretamente na última vez (por exemplo, o processo foi finalizado). Se o comportamento estiver estranho, exporte os diagnósticos em Configurações.");

    // ======== 恢复页（RecoveryPageBuilder；原生对话框不可用时的回落承载面） ========

    /// <summary>恢复页：导出诊断包按钮。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>按钮文案。</returns>
    public static string RecoveryExportButton(UiLanguage lang) => Pick(lang,
        zh: "导出诊断包", en: "Export Diagnostics", ja: "診断情報をエクスポート",
        es: "Exportar diagnósticos", fr: "Exporter les diagnostics",
        de: "Diagnose exportieren", ru: "Экспорт диагностики", pt: "Exportar diagnósticos");

    /// <summary>恢复页：退出应用按钮。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>按钮文案。</returns>
    public static string RecoveryExitButton(UiLanguage lang) => Pick(lang,
        zh: "退出应用", en: "Quit App", ja: "アプリを終了",
        es: "Salir de la aplicación", fr: "Quitter l'application",
        de: "App beenden", ru: "Выйти из приложения", pt: "Sair do aplicativo");

    /// <summary>恢复页：自动重试说明行。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>说明文案。</returns>
    public static string RecoveryAutoRetryNote(UiLanguage lang) => Pick(lang,
        zh: "系统正在自动重试；恢复后本页会自动消失。",
        en: "Auto-retry in progress; this page will disappear once recovered.",
        ja: "自動的に再試行しています。復旧するとこのページは自動的に消えます。",
        es: "Reintento automático en curso; esta página desaparecerá cuando se recupere.",
        fr: "Nouvelle tentative automatique en cours ; cette page disparaîtra une fois rétablie.",
        de: "Automatischer Neuversuch läuft; diese Seite verschwindet nach der Wiederherstellung.",
        ru: "Идёт автоматическая повторная попытка; страница исчезнет после восстановления.",
        pt: "Nova tentativa automática em andamento; esta página desaparecerá quando for recuperado.");

    /// <summary>恢复页：导出中状态。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>状态文案。</returns>
    public static string RecoveryExporting(UiLanguage lang) => Pick(lang,
        zh: "正在导出…", en: "Exporting…", ja: "エクスポート中…",
        es: "Exportando…", fr: "Exportation…",
        de: "Wird exportiert…", ru: "Экспорт…", pt: "Exportando…");

    /// <summary>恢复页：已导出状态前缀（后接路径数据）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>前缀文案。</returns>
    public static string RecoveryExportedPrefix(UiLanguage lang) => Pick(lang,
        zh: "已导出：", en: "Exported: ", ja: "エクスポートしました：",
        es: "Exportado: ", fr: "Exporté : ",
        de: "Exportiert: ", ru: "Экспортировано: ", pt: "Exportado: ");

    /// <summary>恢复页：导出失败状态前缀（后接原因数据）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>前缀文案。</returns>
    public static string RecoveryExportFailedPrefix(UiLanguage lang) => Pick(lang,
        zh: "导出失败：", en: "Export failed: ", ja: "エクスポートに失敗しました：",
        es: "Error al exportar: ", fr: "Échec de l'exportation : ",
        de: "Export fehlgeschlagen: ", ru: "Ошибка экспорта: ", pt: "Falha na exportação: ");

    /// <summary>恢复页：未知原因兜底。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>兜底文案。</returns>
    public static string RecoveryUnknownReason(UiLanguage lang) => Pick(lang,
        zh: "未知原因", en: "unknown reason", ja: "不明な原因",
        es: "motivo desconocido", fr: "raison inconnue",
        de: "unbekannter Grund", ru: "неизвестная причина", pt: "motivo desconhecido");

    /// <summary>恢复页：崩溃原因（宿主侧注入，随包文案）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>原因文案。</returns>
    public static string ReasonRuntimeCrashed(UiLanguage lang) => Pick(lang,
        zh: "运行时进程意外退出，正在自动重启",
        en: "The runtime process exited unexpectedly; restarting…",
        ja: "ランタイムプロセスが予期せず終了しました。自動的に再起動しています…",
        es: "El proceso de tiempo de ejecución terminó inesperadamente; reiniciando…",
        fr: "Le processus d'exécution s'est arrêté de manière inattendue ; redémarrage…",
        de: "Der Laufzeitprozess wurde unerwartet beendet; Neustart läuft…",
        ru: "Процесс среды выполнения завершился неожиданно; выполняется перезапуск…",
        pt: "O processo de tempo de execução terminou inesperadamente; reiniciando…");

    /// <summary>恢复页：残留锁死原因（ADR residue-lock-fail-loud——残留占住文件/端口且无法安全回收，
    /// 跳过重启防碰撞；用户手动结束残留进程后监督器下轮自动恢复）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>原因文案。</returns>
    public static string ReasonDshResidueLocked(UiLanguage lang) => Pick(lang,
        zh: "检测到无法安全回收的残留运行时进程（占住文件/端口），已暂停自动重启以防冲突；手动结束残留进程后将自动恢复",
        en: "A leftover runtime process is holding files/ports and cannot be safely reaped; restart is paused to avoid a collision. End the leftover process and recovery will resume on its own.",
        ja: "安全に回収できない残留ランタイムプロセス（ファイルやポートを占有）を検出したため、衝突を避けるべく自動再起動を一時停止しました。残留プロセスを手動で終了すると自動的に復旧します。",
        es: "Se detectó un proceso de tiempo de ejecución residual que retiene archivos o puertos y no se puede reclamar de forma segura; el reinicio está en pausa para evitar conflictos. Finalice el proceso residual y la recuperación se reanudará automáticamente.",
        fr: "Un processus d'exécution résiduel retient des fichiers ou des ports et ne peut pas être récupéré en toute sécurité ; le redémarrage est suspendu pour éviter un conflit. Terminez le processus résiduel et la récupération reprendra d'elle-même.",
        de: "Ein zurückgebliebener Laufzeitprozess hält Dateien oder Ports und kann nicht sicher entfernt werden; der Neustart ist pausiert, um Kollisionen zu vermeiden. Beenden Sie den Restprozess, danach läuft die Wiederherstellung automatisch weiter.",
        ru: "Обнаружен оставшийся процесс среды выполнения, удерживающий файлы или порты, и его нельзя безопасно завершить; перезапуск приостановлен во избежание конфликта. Завершите этот процесс, и восстановление продолжится автоматически.",
        pt: "Foi detectado um processo de tempo de execução residual retendo arquivos ou portas que não pode ser encerrado com segurança; a reinicialização está pausada para evitar conflitos. Encerre o processo residual e a recuperação continuará por conta própria.");

    /// <summary>恢复页失败原因选择（域决策，ADR diag-masking-and-recovery-page）：残留锁死取锁文案，
    /// 否则普通崩溃原因——选择口径单点，承载面只消费。</summary>
    /// <param name="isLockBlocked">是否因残留锁死而跳过重启。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>与原因分支对应的文案。</returns>
    public static string RecoveryReason(bool isLockBlocked, UiLanguage lang) =>
        isLockBlocked ? ReasonDshResidueLocked(lang) : ReasonRuntimeCrashed(lang);

    // ======== 自绘标题栏（caption bar；PageBridge/CaptionBar + wwwroot/index.html 静态条同源）========
    // 三个窗口按钮的可达名（aria-label）：按钮无可见文本（细线符号），读屏依赖此名；
    // 八语言文案进 index.html 字典（verify-ui-copy 双向核对）。

    /// <summary>标题栏按钮可达名按宿主语言取值。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>（最小化，最大化，关闭）三元组。</returns>
    public static (string Minimize, string Maximize, string Close) CaptionButtonNames(UiLanguage lang) => (
        Pick(lang,
            zh: "最小化", en: "Minimize", ja: "最小化", es: "Minimizar",
            fr: "Réduire", de: "Minimieren", ru: "Свернуть", pt: "Minimizar"),
        Pick(lang,
            zh: "最大化", en: "Maximize", ja: "最大化", es: "Maximizar",
            fr: "Agrandir", de: "Maximieren", ru: "Развернуть", pt: "Maximizar"),
        Pick(lang,
            zh: "关闭", en: "Close", ja: "閉じる", es: "Cerrar",
            fr: "Fermer", de: "Schließen", ru: "Закрыть", pt: "Fechar"));

    /// <summary>标题栏：绿灯按钮在 macOS 的可达名——macOS 绿灯是<i>原生全屏</i>语义（官方行为），
    /// 非 macOS 仍走 <see cref="CaptionButtonNames"/> 的最大化/还原。不成对常量：index.html 的静态条
    /// 三端统一为最大化语义（代理未起时的引导页），无字典对账对象。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>绿灯按钮的可达名。</returns>
    public static string CaptionFullscreenName(UiLanguage lang) => Pick(lang,
        zh: "全屏", en: "Full Screen", ja: "フルスクリーン", es: "Pantalla completa",
        fr: "Plein écran", de: "Vollbild", ru: "Полный экран", pt: "Tela cheia");
}
