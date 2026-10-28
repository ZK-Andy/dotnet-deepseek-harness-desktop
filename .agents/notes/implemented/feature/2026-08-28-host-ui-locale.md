# Agent Note: 宿主侧 UI 双语（托盘/横幅）跟随 dsh 语言

Status: implemented

Erratum: 2026-10-24 — 托盘重建落点「Program 接线 → `TrayService.SetMenu`」随 G5 旧宿主删除；现行落点 = `ShellTray` + `TrayMenuRebuild`（见 [2026-10-24-tray-menu-locale-rebuild](../architecture/2026-10-24-tray-menu-locale-rebuild.md)）。决定本身（`UiLocale.Changed` 驱动托盘菜单重建）不变，正文与 Testing 段的验收意图不动。

## Problem

companion 客户端已接入 dsh i18n（中⇄英随语言切换），但宿主自绘 UI——托盘菜单（显示主窗/检查更新/退出）与三类宿主横幅（版本底线/非受控退出/更新就绪，含「知道了」按钮）——硬编码中文。dsh 界面切英文后这些宿主面仍为中文，双语体验断在宿主侧。

## Decision

宿主建立**UiLocale 单点**（`Core/Localization/UiLocale`），语言值经 dsh→companion→宿主单向桥接：

- **locale 源**：dsh locale runtime 在插件激活与每次切换时把 `<html lang>` 指向当前 locale（`zh-CN`/`en`，上游已实证的行为）——companion `client.js` 以 MutationObserver 监听 `html[lang]`，经既有 `window.__ryn.invoke` 通道上报 `desktop.companion.setLocale {locale}`；不上报业务状态，宿主不反向依赖 dsh 内部 API。
- **宿主单点**：`UiLocale` 保存当前 locale（初始值 = 上次上报的持久值 → OS locale 兜底；companion 每次上报即更新并按 `IUiLocaleStore` 落盘，见 ADR [ui-copy-bilingual-completion](2026-09-16-ui-copy-bilingual-completion.md)），`Language` 由 `UiLanguageResolver` 从 locale 主语言子标签派生（八语言封闭集，未注册语言回落英文——与上游 `FALLBACK_LOCALE` 同向，见 ADR [ui-copy-eight-locales](2026-10-28-ui-copy-eight-locales.md)）；locale 变更事件驱动托盘菜单重建。
- **托盘**：`TrayMenuActions.BuildEntries` 出八语言标签（经 `UiCopy` 的 `UiLanguage` 入口）；`UiLocale.Changed` 由 `TrayMenuRebuild` 订阅并按当前语言重建菜单。
- **横幅**：三类横幅文案 + 「知道了」按钮进 `DesktopBanner` 注入时刻的 locale 选择（横幅是一次性注入，无运行时切换面）；`DesktopBanner.Build` 增按钮文案参数。

## Alternatives considered

- **宿主直接读 OS locale**：dsh 语言设置独立于 OS locale（用户可在 dsh 内切英文而 OS 是中文），桥接缺失则复现原投诉场景；OS locale 仅作首报前的缺省。
- **宿主轮询/直读 dsh settings.yaml**：`locale.preference` 虽落在 `$DSH_HOME/settings.yaml`，但宿主自轮询文件引入第二状态源与竞态，且拿不到切换时刻；companion 本就活跃订阅 locale revision，上报是零新增依赖。
- **横幅走 dsh slot 渲染**：横幅是宿主在 dsh 崩溃/未就绪时刻注入的兜底层（恢复页同族），不能依赖 dsh 活着——纯 JS 注入通道必须保留。

## Consequences

- 托盘与横幅随 dsh 语言切换（托盘即时重建，横幅按注入时刻 locale）；旧版 companion（无上报）下宿主用 OS locale 缺省，中文环境行为不变。
- `desktop.companion.setLocale` 为新 IPC 命令：payload 仅 `{locale: string}`，坏载荷按未知命令拒绝路径忽略；companion 上报失败静默（invoke catch）——locale 桥是增强能力，绝不影响安装/更新主链路。
- companion 版本 bump 0.0.16 → 0.0.17，随下次发版闭包重建进包，已装端随版本感知升级获取。

### Testing

`UiLocaleTests`（归一化/事件/`Language` 八语言解析与未注册回落）、`CompanionLocaleCommandRouterTests`（合法上报更新/坏 JSON 与非法 locale 忽略/触发 Changed）、托盘菜单条目随语言重建、三类横幅文案的语言分支断言。Program 的 `Changed`→`SetMenu` 重建路径不单测（Main 编排历来不单测），随下次发版实机验收：dsh 切英文 → 托盘菜单变英文。

### Related

- [2026-08-28-companion-client-i18n](../feature/2026-08-28-companion-client-i18n.md)：companion 侧 i18n 接入（本决定补齐宿主侧，语言源同宗）。
- [2026-08-27-ryn-navigation-callbacks](2026-08-28-ryn-navigation-callbacks.md)：宿主→WebView 注入通道与「页面已到达」门控先例。
