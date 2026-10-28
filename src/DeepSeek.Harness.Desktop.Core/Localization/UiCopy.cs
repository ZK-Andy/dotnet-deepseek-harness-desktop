namespace DeepSeek.Harness.Desktop.Core.Localization;

/// <summary>
/// 宿主 UI 文案单一词典：托盘菜单、横幅、恢复页、引导过渡页等
/// <b>用户可见</b>文案的唯一家。随 locale 切换的文案以 <see cref="UiLanguage"/> 入口提供，
/// 覆盖上游随包分发的八种语言（zh/en 基线 + ja/es/fr/de/ru/pt 语言包）；
/// 静态页（<c>wwwroot/index.html</c>）的文案由页内 <c>var D</c> 表收容；词典是唯一事实源，
/// <c>index.html</c> 与 <c>client.js</c> 是被门禁核对的消费方（<c>scripts/verify-ui-copy.py</c>）。
/// host.log 诊断行不属 UI 文案，不入本词典。
/// </summary>
/// <remarks>
/// 词典选型（ADR review-to-machine-gates）：强类型静态类而非 resx——强类型入口让编译器
/// （而非字符串键）保证引用完整性。八语言化（ADR ui-copy-eight-locales）后仍不引 resx：
/// 语言集是封闭枚举，resx 的卫星装配与 ResourceManager 文化链对本场景是纯开销。
/// 每条文案经 <see cref="Pick"/> 的<b>具名实参</b>列出八种语言：漏一种语言编译即失败，
/// 不依赖测试兜底。
/// </remarks>
public static partial class UiCopy
{
    /// <summary>八语言选择器：具名实参强制每个调用点给全八种语言（漏一种即编译期报错）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <param name="zh">简体中文。</param>
    /// <param name="en">English。</param>
    /// <param name="ja">日本語。</param>
    /// <param name="es">Español。</param>
    /// <param name="fr">Français。</param>
    /// <param name="de">Deutsch。</param>
    /// <param name="ru">Русский。</param>
    /// <param name="pt">Português (Brasil)。</param>
    /// <returns>对应语言的文案。</returns>
    private static string Pick(
        UiLanguage lang, string zh, string en, string ja, string es, string fr, string de, string ru, string pt) => lang switch
        {
            UiLanguage.Zh => zh,
            UiLanguage.Ja => ja,
            UiLanguage.Es => es,
            UiLanguage.Fr => fr,
            UiLanguage.De => de,
            UiLanguage.Ru => ru,
            UiLanguage.Pt => pt,
            _ => en,
        };

    // ======== 托盘菜单（TrayMenuActions） ========

    /// <summary>托盘：显示主窗。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>菜单项文案。</returns>
    public static string TrayShowMainWindow(UiLanguage lang) => Pick(lang,
        zh: "显示主窗", en: "Show Main Window", ja: "メインウィンドウを表示",
        es: "Mostrar ventana principal", fr: "Afficher la fenêtre principale",
        de: "Hauptfenster anzeigen", ru: "Показать главное окно", pt: "Mostrar janela principal");

    /// <summary>托盘：检查更新。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>菜单项文案。</returns>
    public static string TrayCheckForUpdates(UiLanguage lang) => Pick(lang,
        zh: "检查更新", en: "Check for Updates", ja: "更新を確認",
        es: "Buscar actualizaciones", fr: "Rechercher des mises à jour",
        de: "Nach Updates suchen", ru: "Проверить обновления", pt: "Verificar atualizações");

    /// <summary>托盘：退出。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>菜单项文案。</returns>
    public static string TrayQuit(UiLanguage lang) => Pick(lang,
        zh: "退出", en: "Quit", ja: "終了",
        es: "Salir", fr: "Quitter",
        de: "Beenden", ru: "Выход", pt: "Sair");

    /// <summary>托盘：重启应用。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>菜单项文案。</returns>
    public static string TrayRestart(UiLanguage lang) => Pick(lang,
        zh: "重启", en: "Restart", ja: "再起動",
        es: "Reiniciar", fr: "Redémarrer",
        de: "Neu starten", ru: "Перезапустить", pt: "Reiniciar");

    // ======== 托盘「检查更新」结果通知（TrayCheckFeedback） ========

    /// <summary>托盘通知：已是最新版本。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>通知正文。</returns>
    public static string TrayCheckUpToDate(UiLanguage lang) => Pick(lang,
        zh: "已是最新版本", en: "Already up to date", ja: "最新バージョンです",
        es: "Ya está actualizado", fr: "Déjà à jour",
        de: "Bereits aktuell", ru: "Обновление не требуется", pt: "Já está atualizado");

    /// <summary>托盘通知：新版本就绪，可在设置页安装。</summary>
    /// <param name="version">新版本号。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>通知正文。</returns>
    public static string TrayCheckReady(string version, UiLanguage lang) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        Pick(lang,
            zh: "新版本 {0} 已就绪，可在 设置 → 桌面设置 安装",
            en: "New version {0} is ready; install it in Settings → Desktop Settings.",
            ja: "新しいバージョン {0} の準備ができました。設定 → デスクトップ設定 からインストールできます。",
            es: "La nueva versión {0} está lista; instálela en Ajustes → Ajustes de escritorio.",
            fr: "La nouvelle version {0} est prête ; installez-la dans Réglages → Réglages du bureau.",
            de: "Die neue Version {0} ist bereit; installieren Sie sie unter Einstellungen → Desktop-Einstellungen.",
            ru: "Новая версия {0} готова; установите её в разделе Настройки → Настройки рабочего стола.",
            pt: "A nova versão {0} está pronta; instale-a em Configurações → Configurações da área de trabalho."),
        version);

    /// <summary>托盘通知：检查更新失败前缀（后接原因）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>前缀文案。</returns>
    public static string TrayCheckFailedPrefix(UiLanguage lang) => Pick(lang,
        zh: "检查更新失败：", en: "Update check failed: ", ja: "更新の確認に失敗しました：",
        es: "Error al buscar actualizaciones: ", fr: "Échec de la recherche de mises à jour : ",
        de: "Update-Prüfung fehlgeschlagen: ", ru: "Не удалось проверить обновления: ",
        pt: "Falha ao verificar atualizações: ");
}
