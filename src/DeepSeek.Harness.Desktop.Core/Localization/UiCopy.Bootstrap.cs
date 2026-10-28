namespace DeepSeek.Harness.Desktop.Core.Localization;

/// <summary>
/// 宿主 UI 文案词典的引导失败面（引导进度页错误框）：按域立分部以守文件尺寸（F1）。
/// 词典唯一事实源语义与门禁口径见 <see cref="UiCopy"/>。
/// </summary>
public static partial class UiCopy
{
    // ======== 引导失败原因（RuntimeBootstrap → wwwroot 引导页错误框） ========

    /// <summary>引导：npm 装全局 dsh 需提升权限（附手动命令）。</summary>
    /// <param name="dshSpec">dsh 安装坐标。</param>
    /// <param name="detail">底层错误摘要。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败原因文案。</returns>
    public static string BootstrapNpmNeedsElevation(string dshSpec, string detail, UiLanguage lang) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        Pick(lang,
            zh: "npm install 需要提升权限。请在终端手动执行：sudo npm install -g {0}（{1}）",
            en: "npm install needs elevated permissions. Run it manually in a terminal: sudo npm install -g {0} ({1})",
            ja: "npm install には管理者権限が必要です。ターミナルで手動実行してください：sudo npm install -g {0}（{1}）",
            es: "npm install requiere permisos elevados. Ejecútelo manualmente en una terminal: sudo npm install -g {0} ({1})",
            fr: "npm install nécessite des droits élevés. Exécutez-le manuellement dans un terminal : sudo npm install -g {0} ({1})",
            de: "npm install benötigt erhöhte Rechte. Führen Sie es manuell im Terminal aus: sudo npm install -g {0} ({1})",
            ru: "npm install требует повышенных прав. Выполните вручную в терминале: sudo npm install -g {0} ({1})",
            pt: "npm install exige permissões elevadas. Execute manualmente em um terminal: sudo npm install -g {0} ({1})"),
        dshSpec, detail);

    /// <summary>引导：npm install 非零退出。</summary>
    /// <param name="exit">退出码。</param>
    /// <param name="detail">底层错误摘要。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败原因文案。</returns>
    public static string BootstrapNpmFailed(int exit, string detail, UiLanguage lang) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        Pick(lang,
            zh: "npm install 失败 exit={0}：{1}",
            en: "npm install failed with exit={0}: {1}",
            ja: "npm install が失敗しました exit={0}：{1}",
            es: "npm install falló con exit={0}: {1}",
            fr: "Échec de npm install avec exit={0} : {1}",
            de: "npm install fehlgeschlagen mit exit={0}: {1}",
            ru: "npm install завершился с ошибкой exit={0}: {1}",
            pt: "npm install falhou com exit={0}: {1}"),
        exit, detail);

    /// <summary>引导：装完仍解析不到全局 dsh 版本（PATH/前缀错位指引）。</summary>
    /// <param name="dshSpec">dsh 安装坐标。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败原因文案。</returns>
    public static string BootstrapDshUnresolved(string dshSpec, UiLanguage lang) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        Pick(lang,
            zh: "安装后未能解析全局 dsh 版本（{0}）。请确认该 node 的 npm 全局 bin（`npm config get prefix` 下的 bin）已加入 PATH，或手动执行 `npm install -g {0}`。",
            en: "Could not resolve the global dsh version after install ({0}). Make sure the npm global bin of that node (the bin under `npm config get prefix`) is on PATH, or run `npm install -g {0}` manually.",
            ja: "インストール後にグローバル dsh のバージョンを解決できませんでした（{0}）。その node の npm グローバル bin（`npm config get prefix` 配下の bin）が PATH にあるか確認するか、手動で `npm install -g {0}` を実行してください。",
            es: "No se pudo resolver la versión global de dsh tras la instalación ({0}). Compruebe que el bin global de npm de ese node (el bin bajo `npm config get prefix`) esté en PATH, o ejecute `npm install -g {0}` manualmente.",
            fr: "Impossible de résoudre la version globale de dsh après l'installation ({0}). Vérifiez que le bin global npm de ce node (le bin sous `npm config get prefix`) est dans le PATH, ou exécutez `npm install -g {0}` manuellement.",
            de: "Die globale dsh-Version konnte nach der Installation nicht aufgelöst werden ({0}). Stellen Sie sicher, dass das globale npm-bin dieses node (das bin unter `npm config get prefix`) im PATH liegt, oder führen Sie `npm install -g {0}` manuell aus.",
            ru: "Не удалось определить глобальную версию dsh после установки ({0}). Убедитесь, что глобальный bin npm этого node (bin в `npm config get prefix`) есть в PATH, либо выполните `npm install -g {0}` вручную.",
            pt: "Não foi possível resolver a versão global do dsh após a instalação ({0}). Verifique se o bin global do npm desse node (o bin em `npm config get prefix`) está no PATH, ou execute `npm install -g {0}` manualmente."),
        dshSpec);

    /// <summary>引导：DshSpec 非 registry 形态（caret range 等 pnpm 不支持形，ADR pnpm-caret-spec-rejection）。</summary>
    /// <param name="dshSpec">dsh 安装坐标。</param>
    /// <param name="reason">形态非法的原因。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败原因文案。</returns>
    public static string BootstrapInvalidDshSpec(string dshSpec, string reason, UiLanguage lang) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        Pick(lang,
            zh: "DshSpec 非法（{0}）：{1}。请用 registry 形态，如 @deepseek-ai/dsh@next。",
            en: "Invalid DshSpec ({0}): {1}. Use a registry spec like @deepseek-ai/dsh@next.",
            ja: "DshSpec が不正です（{0}）：{1}。@deepseek-ai/dsh@next のような registry 形式を使用してください。",
            es: "DshSpec no válido ({0}): {1}. Use una especificación de registro como @deepseek-ai/dsh@next.",
            fr: "DshSpec invalide ({0}) : {1}. Utilisez une spécification de registre comme @deepseek-ai/dsh@next.",
            de: "Ungültige DshSpec ({0}): {1}. Verwenden Sie eine Registry-Angabe wie @deepseek-ai/dsh@next.",
            ru: "Недопустимый DshSpec ({0}): {1}. Используйте запись реестра, например @deepseek-ai/dsh@next.",
            pt: "DshSpec inválido ({0}): {1}. Use uma especificação de registro como @deepseek-ai/dsh@next."),
        dshSpec, reason);

    /// <summary>引导：当前平台无 Node 发行包坐标。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败原因文案。</returns>
    public static string BootstrapUnsupportedPlatform(UiLanguage lang) => Pick(lang,
        zh: "当前平台无对应的 Node 发行包坐标（fail loud）",
        en: "No Node distribution archive coordinates for this platform",
        ja: "このプラットフォームに対応する Node 配布アーカイブの座標がありません（fail loud）",
        es: "No hay coordenadas de archivo de distribución de Node para esta plataforma (fail loud)",
        fr: "Aucune coordonnée d'archive de distribution Node pour cette plateforme (fail loud)",
        de: "Keine Koordinaten für das Node-Distributionsarchiv für diese Plattform (fail loud)",
        ru: "Нет координат архива дистрибутива Node для этой платформы (fail loud)",
        pt: "Não há coordenadas de arquivo de distribuição do Node para esta plataforma (fail loud)");

    /// <summary>引导：Node 发行包 SHA256 与官方摘要不符（安全中止）。</summary>
    /// <param name="expected">期望摘要。</param>
    /// <param name="actual">实际摘要。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败原因文案。</returns>
    public static string BootstrapNodeHashMismatch(string expected, string actual, UiLanguage lang) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        Pick(lang,
            zh: "Node 发行包 SHA256 不匹配（期望 {0}，实际 {1}），安全中止",
            en: "Node distribution SHA256 mismatch (expected {0}, actual {1}); aborting for safety",
            ja: "Node 配布アーカイブの SHA256 が一致しません（期待 {0}、実際 {1}）。安全のため中止します",
            es: "El SHA256 del archivo de Node no coincide (esperado {0}, real {1}); se cancela por seguridad",
            fr: "Le SHA256 de l'archive Node ne correspond pas (attendu {0}, réel {1}) ; abandon par sécurité",
            de: "SHA256 des Node-Archivs stimmt nicht überein (erwartet {0}, tatsächlich {1}); Abbruch aus Sicherheitsgründen",
            ru: "SHA256 архива Node не совпадает (ожидалось {0}, получено {1}); прерывание из соображений безопасности",
            pt: "O SHA256 do arquivo do Node não corresponde (esperado {0}, real {1}); abortando por segurança"),
        expected, actual);

    /// <summary>引导：Node 发行包解压后无内容目录。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败原因文案。</returns>
    public static string BootstrapNodeDistEmpty(UiLanguage lang) => Pick(lang,
        zh: "Node 发行包解压结果无内容目录", en: "Node distribution archive extracted no content directory",
        ja: "Node 配布アーカイブを展開しても内容ディレクトリがありません",
        es: "El archivo de Node no extrajo ningún directorio de contenido",
        fr: "L'archive Node n'a extrait aucun répertoire de contenu",
        de: "Das Node-Archiv enthielt kein Inhaltsverzeichnis",
        ru: "В архиве Node нет каталога с содержимым",
        pt: "O arquivo do Node não extraiu nenhum diretório de conteúdo");

    /// <summary>引导：Node 落位后入口缺失（布局异常）。</summary>
    /// <param name="prefix">安装前缀。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败原因文案。</returns>
    public static string BootstrapNodeEntryMissing(string prefix, UiLanguage lang) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        Pick(lang,
            zh: "node 装到系统全局 {0} 后入口缺失（布局异常）",
            en: "node entry missing after installing into the system-global {0} (layout anomaly)",
            ja: "システムグローバルの {0} にインストール後、node のエントリが見つかりません（レイアウト異常）",
            es: "Falta la entrada de node tras instalar en el {0} global del sistema (anomalía de diseño)",
            fr: "Entrée node manquante après installation dans le {0} global du système (anomalie de disposition)",
            de: "node-Eintrag fehlt nach der Installation im systemweiten {0} (Layout-Anomalie)",
            ru: "Отсутствует запись node после установки в системный {0} (аномалия структуры)",
            pt: "Entrada do node ausente após instalar no {0} global do sistema (anomalia de layout)"),
        prefix);

    /// <summary>引导：系统全局 node 安装位不可写（权限不足，附管理员指引）。</summary>
    /// <param name="prefix">安装前缀。</param>
    /// <param name="detail">底层错误摘要。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败原因文案。</returns>
    public static string BootstrapNodeInstallDenied(string prefix, string detail, UiLanguage lang) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        Pick(lang,
            zh: "无法写入系统全局 node 安装位 {0}（权限不足：{1}）。请以管理员权限安装 Node.js（https://nodejs.org 官方安装包/系统包管理器），或放开 {0} 写入权限后重试。",
            en: "Cannot write to the system-global node location {0} (permission denied: {1}). Install Node.js with administrator rights (official installer at https://nodejs.org or your package manager), or grant write access to {0} and retry.",
            ja: "システムグローバルの node インストール先 {0} に書き込めません（権限不足：{1}）。管理者権限で Node.js をインストールする（https://nodejs.org の公式インストーラーまたはパッケージマネージャー）か、{0} への書き込み権限を付与して再試行してください。",
            es: "No se puede escribir en la ubicación global del sistema de node {0} (permiso denegado: {1}). Instale Node.js con derechos de administrador (instalador oficial en https://nodejs.org o su gestor de paquetes), o conceda acceso de escritura a {0} y reintente.",
            fr: "Impossible d'écrire dans l'emplacement global système de node {0} (permission refusée : {1}). Installez Node.js avec des droits d'administrateur (installateur officiel sur https://nodejs.org ou votre gestionnaire de paquets), ou accordez l'accès en écriture à {0} puis réessayez.",
            de: "Das systemweite node-Verzeichnis {0} ist nicht beschreibbar (Zugriff verweigert: {1}). Installieren Sie Node.js mit Administratorrechten (offizieller Installer unter https://nodejs.org oder Ihr Paketmanager) oder gewähren Sie Schreibzugriff auf {0} und versuchen Sie es erneut.",
            ru: "Невозможно записать в системный каталог node {0} (доступ запрещён: {1}). Установите Node.js с правами администратора (официальный установщик с https://nodejs.org или ваш пакетный менеджер) либо предоставьте доступ на запись в {0} и повторите попытку.",
            pt: "Não é possível gravar no local global do sistema do node {0} (permissão negada: {1}). Instale o Node.js com direitos de administrador (instalador oficial em https://nodejs.org ou seu gerenciador de pacotes), ou conceda acesso de gravação a {0} e tente novamente."),
        prefix, detail);

    /// <summary>引导：nodejs.org index.json 解析不出最新 Node 版本。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败原因文案。</returns>
    public static string BootstrapNodeVersionUnresolved(UiLanguage lang) => Pick(lang,
        zh: "无法从 nodejs.org index.json 解析最新 Node 版本",
        en: "Could not parse the latest Node version from the nodejs.org index.json",
        ja: "nodejs.org の index.json から最新の Node バージョンを解析できませんでした",
        es: "No se pudo analizar la última versión de Node desde index.json de nodejs.org",
        fr: "Impossible d'analyser la dernière version de Node depuis index.json de nodejs.org",
        de: "Die neueste Node-Version konnte nicht aus index.json von nodejs.org gelesen werden",
        ru: "Не удалось определить последнюю версию Node из index.json на nodejs.org",
        pt: "Não foi possível analisar a versão mais recente do Node a partir do index.json do nodejs.org");

    /// <summary>引导：取官方 SHA256 摘要失败。</summary>
    /// <param name="detail">底层错误摘要。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败原因文案。</returns>
    public static string BootstrapShasumsFetchFailed(string detail, UiLanguage lang) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        Pick(lang,
            zh: "获取 Node 发行包官方 SHA256 摘要失败：{0}",
            en: "Failed to fetch the official Node distribution SHA256 digest: {0}",
            ja: "Node 配布アーカイブの公式 SHA256 ダイジェスト取得に失敗しました：{0}",
            es: "No se pudo obtener el resumen SHA256 oficial del archivo de Node: {0}",
            fr: "Échec de récupération du condensé SHA256 officiel de l'archive Node : {0}",
            de: "Offizieller SHA256-Abdruck des Node-Archivs konnte nicht geladen werden: {0}",
            ru: "Не удалось получить официальный SHA256 архива Node: {0}",
            pt: "Falha ao obter o resumo SHA256 oficial do arquivo do Node: {0}"),
        detail);

    /// <summary>引导：SHASUMS256.txt 缺目标文件摘要（安全中止）。</summary>
    /// <param name="fileName">发行包文件名。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败原因文案。</returns>
    public static string BootstrapShasumsMissing(string fileName, UiLanguage lang) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        Pick(lang,
            zh: "SHASUMS256.txt 缺少 {0} 的摘要，安全中止",
            en: "SHASUMS256.txt has no digest for {0}; aborting for safety",
            ja: "SHASUMS256.txt に {0} のダイジェストがないため、安全のため中止します",
            es: "SHASUMS256.txt no tiene resumen para {0}; se cancela por seguridad",
            fr: "SHASUMS256.txt n'a pas de condensé pour {0} ; abandon par sécurité",
            de: "SHASUMS256.txt enthält keinen Abdruck für {0}; Abbruch aus Sicherheitsgründen",
            ru: "В SHASUMS256.txt нет дайджеста для {0}; прерывание из соображений безопасности",
            pt: "SHASUMS256.txt não tem resumo para {0}; abortando por segurança"),
        fileName);

    /// <summary>引导：Node 发行包所有候选源下载失败。</summary>
    /// <param name="failures">各源的失败明细。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败原因文案。</returns>
    public static string BootstrapNodeDownloadFailed(string failures, UiLanguage lang) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        Pick(lang,
            zh: "Node 发行包下载：所有候选源均失败（{0}）",
            en: "Node distribution download: every candidate source failed ({0})",
            ja: "Node 配布アーカイブのダウンロード：すべての候補ソースが失敗しました（{0}）",
            es: "Descarga del archivo de Node: fallaron todas las fuentes candidatas ({0})",
            fr: "Téléchargement de l'archive Node : toutes les sources candidates ont échoué ({0})",
            de: "Download des Node-Archivs: alle Kandidatenquellen fehlgeschlagen ({0})",
            ru: "Загрузка архива Node: все источники завершились ошибкой ({0})",
            pt: "Download do arquivo do Node: todas as fontes candidatas falharam ({0})"),
        failures);

    /// <summary>引导：tar 解压失败。</summary>
    /// <param name="exit">退出码。</param>
    /// <param name="detail">底层错误摘要。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败原因文案。</returns>
    public static string BootstrapExtractFailed(int exit, string detail, UiLanguage lang) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        Pick(lang,
            zh: "tar 解压失败 exit={0}：{1}",
            en: "tar extraction failed with exit={0}: {1}",
            ja: "tar の展開に失敗しました exit={0}：{1}",
            es: "La extracción de tar falló con exit={0}: {1}",
            fr: "Échec de l'extraction tar avec exit={0} : {1}",
            de: "tar-Entpacken fehlgeschlagen mit exit={0}: {1}",
            ru: "Ошибка распаковки tar exit={0}: {1}",
            pt: "Falha na extração do tar com exit={0}: {1}"),
        exit, detail);

    /// <summary>引导：子进程启动失败。</summary>
    /// <param name="exe">可执行文件。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败原因文案。</returns>
    public static string BootstrapProcessStartFailed(string exe, UiLanguage lang) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        Pick(lang,
            zh: "无法启动进程 {0}", en: "Cannot start process {0}",
            ja: "プロセス {0} を起動できません", es: "No se puede iniciar el proceso {0}",
            fr: "Impossible de démarrer le processus {0}", de: "Prozess {0} kann nicht gestartet werden",
            ru: "Не удаётся запустить процесс {0}", pt: "Não é possível iniciar o processo {0}"),
        exe);

    /// <summary>引导：步骤级超时（网络停滞/资源受限，可重试）。</summary>
    /// <param name="minutes">超时分钟数。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>失败原因文案。</returns>
    public static string BootstrapStepTimeout(int minutes, UiLanguage lang) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        Pick(lang,
            zh: "步骤超时（>{0} 分钟），网络停滞或资源受限，可重试",
            en: "Step timed out (>{0} min): the network stalled or resources are constrained — you can retry",
            ja: "ステップがタイムアウトしました（>{0} 分）。ネットワークの停滞かリソース制約です。再試行できます",
            es: "El paso agotó el tiempo (>{0} min): la red se detuvo o los recursos están limitados; puede reintentar",
            fr: "Étape expirée (>{0} min) : le réseau a calé ou les ressources sont limitées — vous pouvez réessayer",
            de: "Schritt abgelaufen (>{0} Min.): Netzwerk stockte oder Ressourcen sind knapp — Sie können es erneut versuchen",
            ru: "Шаг превысил время (>{0} мин): сеть зависла или не хватает ресурсов — можно повторить",
            pt: "A etapa expirou (>{0} min): a rede travou ou os recursos estão limitados — você pode tentar novamente"),
        minutes);

    /// <summary>引导：失败但无原因文本时的兜底。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>兜底文案。</returns>
    public static string BootstrapUnknownError(UiLanguage lang) => Pick(lang,
        zh: "未知错误", en: "Unknown error", ja: "不明なエラー",
        es: "Error desconocido", fr: "Erreur inconnue",
        de: "Unbekannter Fehler", ru: "Неизвестная ошибка", pt: "Erro desconhecido");

    /// <summary>引导：单一下载源失败明细（多源失败时逐条拼接的条目）。</summary>
    /// <param name="url">下载源地址。</param>
    /// <param name="detail">失败明细。</param>
    /// <param name="lang">目标语言。</param>
    /// <returns>明细条目。</returns>
    public static string BootstrapDownloadSourceFailed(string url, string detail, UiLanguage lang) => Pick(lang,
        zh: url + "：" + detail,
        en: url + ": " + detail,
        ja: url + "：" + detail,
        es: url + ": " + detail,
        fr: url + " : " + detail,
        de: url + ": " + detail,
        ru: url + ": " + detail,
        pt: url + ": " + detail);

    /// <summary>列表分隔符（把多条明细拼成一句时用；各语言标点写法不同）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <returns>分隔符：全角分号（zh/ja）或半角分号加空格（fr 为法式前置空格）。</returns>
    public static string ListSeparator(UiLanguage lang) => Pick(lang,
        zh: "；", en: "; ", ja: "；", es: "; ", fr: " ; ", de: "; ", ru: "; ", pt: "; ");
}
