# 用户指南

> 面向下载使用 DeepSeek Harness Desktop 的用户。开发与架构文档见 [architecture.md](architecture.md) / [development.md](development.md)；常见问题见 [faq.md](faq.md)。

## 系统要求与下载

从 [GitHub Releases](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/releases) 下载对应平台安装包（随包附 `SHA256SUMS.txt` 校验文件）：

| 平台 | 安装包 |
|---|---|
| Windows 10/11 x64 | `..._windows-x64-setup.exe` |
| macOS Apple Silicon / Intel | `..._macos-arm64.dmg` / `..._macos-x64.dmg` |
| Debian/Ubuntu x64 / arm64 | `..._linux-amd64.deb` / `..._linux-arm64.deb` |
| Fedora/RHEL x86_64 / aarch64 | `..._linux-x86_64.rpm` / `..._linux-aarch64.rpm` |

**无需单独安装 Node.js 或 DeepSeek Harness**——外壳在**首次启动**时自动联网下载并准备运行时（下载钉版 Node，经 registry 安装 `@deepseek-ai/dsh`）。此过程**需要网络**，进度页可见、失败可重试；准备完成后再进入界面。

## 安装

- **Windows**：运行 setup.exe 按提示安装（开始菜单/桌面快捷方式）。
- **macOS**：打开 DMG，将应用拖入「应用程序」。
- **Linux**：`sudo apt install ./....deb` 或 `sudo dnf install ./....rpm`（WebKitGTK 等依赖由包管理器自动解决）。

未签名说明：本项目开源且不做付费签名。macOS 首次打开若被 Gatekeeper 拦截 → 右键「打开」或在 系统设置 → 隐私与安全性 中放行；Windows 若见 SmartScreen → 「更多信息」→「仍要运行」。详见 [README](../README.md)。

## 首次启动

1. 启动后壳会自动准备并拉起 DeepSeek Harness 运行时并加载界面。首次启动（无捆绑运行时且无 PATH dsh）会进入引导：检测/复用本机 Node，否则下载钉版 Node 并安装 dsh——全程进度页可见；**断网或失败可在进度页重试**（步骤级超时兜底）。
2. 首次使用请在界面内的**模型设置**中配置你自己的模型 API 凭据。
3. 运行时就位后、进入界面前，引导页会出现「插件准备」步：插件市场（dshmarket）以推荐 chip 展示，你可用「确认安装」或「跳过」决定是否安装（跳过仍可稍后在应用内市场补装），安装过程日志实时回流到进度页。桌面伴生插件（companion）为壳必需，静默自愈、不展示在勾选清单。确认后可进入界面。
4. 插件市场就位后即可浏览并安装社区插件。

## 日常使用

- **会话自动恢复**：正常重启应用后会回到上次的会话；运行时崩溃会自动重启并回到当前对话。
- **外部链接**：点击站外链接会调用系统默认浏览器打开，不会困在应用内。
- **桌面设置入口**：设置页中的「桌面设置」区块显示当前版本，可手动检查更新。
- **系统托盘**：托盘图标常驻，菜单含「显示主窗 / 检查更新 / 退出」；点击窗口关闭按钮默认**隐藏到托盘**而非退出，从托盘菜单可召回窗口或真正退出。
- **开机自启**：设置 →「桌面设置」→「开机自启」开启后，登录系统时自动启动。

## 更新

- 启动时后台检查一次新版本；也可以在 设置 →「桌面设置」手动检查，或用托盘菜单的「检查更新」。
- 发现新版本后出现一键更新按钮：确认授权后应用自动退出→完成安装→自动以新版本重启。
- 安装包经过 SHA256 强校验；macOS 会引导你手动完成替换。
- 取消更新随时可以，应用保持当前版本不受影响。

## 数据与日志位置

桌面端与 DeepSeek Harness 生态**共享同一数据目录**——会话、凭据、工作区与 CLI/TUI/Web 互通；桌面使用其中专属的 `profiles/desktop` 子目录承载插件装配：

| 平台 | 数据目录 |
|---|---|
| Linux / macOS | `~/.dsh` |
| Windows | `%USERPROFILE%\.dsh` |

设置环境变量 `DSH_HOME` 可整体改到其他目录（与上游工具同语义）；`DSH_DESKTOP_DSH_HOME` 只影响桌面端。

运行日志位于数据目录下 `logs/host.log`。

**从 v0.2.x 及更早版本升级**：旧版使用私有目录（Linux `~/.local/share/DeepSeek.Harness.Desktop/dsh`、macOS `~/Library/Application Support/DeepSeek.Harness.Desktop/dsh`、Windows `%LOCALAPPDATA%\DeepSeek.Harness.Desktop\dsh`）。新版**不做自动迁移**：升级前请自行备份；新目录从干净状态开始。旧数据原地保留——需要的会话/凭据可手动拷入新目录，或设置 `DSH_DESKTOP_DSH_HOME` 指回旧目录继续使用；确认不再需要后删除旧目录即可。

**彻底卸载**：卸载应用包后，手动删除上述数据目录即可清除全部本地数据。

## 故障排查

1. **首启卡在下载/安装**（断网或镜像慢）：进度页显示失败步骤并可重试——确认网络可达后点「重试」（步骤级超时兜底）。**启动白屏或一直「重连中」**：查看 `logs/host.log` 末尾的错误信息。
2. **窗口关闭后「不见了」**：关闭按钮默认隐藏到系统托盘（应用仍在运行），从托盘图标菜单选「显示主窗」即可召回；彻底退出请用托盘菜单的「退出」。若桌面环境无系统托盘，应用会记录降级日志并保持关窗即退出的行为。
3. **更新失败**：校验不通过时应用会拒绝安装（安全设计），直接重新下载最新安装包覆盖安装即可。
4. **插件市场打不开**：首次补装可能因网络较慢，重启一次应用通常自愈。
5. **收集诊断信息**：设置 →「桌面设置」→「导出诊断信息」生成 zip（仅日志与运行状态，不含会话与凭据）；界面打不开时用命令行 `<安装的可执行文件> --export-diagnostics`。
6. 以上无法解决时，欢迎到 [Issues](https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop/issues) 反馈，附上系统平台与 `host.log` 相关片段。
