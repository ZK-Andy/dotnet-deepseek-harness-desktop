# Agent Note: Windows 打包与三家对齐补齐

Status: implemented

Review: FULL/2026-09-27/R1=ok R2=ok R3=ok
Review: FULL/2026-09-27#2/R1=ok R2=ok R3=ok

三审结论：R1/R2 无触达；R3 注册表读写收敛安装器脚本内（HKCU 与 lowest 提权一致、`uninsdeletekey` 清理）、PS 空 catch 收敛至 `::warning`；0 Blocker。

## Problem

Windows 安装器（Inno Setup 唯一链）相对三家源码存在五处实质缺口：无稳定 AppId 导致升级并存/残留；未注册 `dsh://` 协议；WebView2 依赖不声明不检测；图标靠 runner 现转、可静默缺失；载荷探针跑在 publish 直出目录上，Inno `[Files]` 漏配抓不住。Linux/macOS 腿已修，Windows 腿未跟进。

## Decision

安装器 `.iss` 补稳定 `AppId`（由 bundle identifier 派生一次后写死，永不再变）+ `LicenseFile`（仓库根 MIT）+ `dsh://` 协议 per-user 注册（`HKCU\Software\Classes\dsh`，URL Protocol + 默认图标 + open 命令，与 `PrivilegesRequired=lowest` 一致）+ `[Code]` WebView2 缺失提示（探针注册表 `EdgeUpdate\Clients\{F3017226-…}\pv`，HKCU 先行、HKLM 双路径，缺时完成页 MsgBox 指引安装 Evergreen，不捆 212MB 离线运行时）。图标改提交物优先（`assets/icon.ico` 入仓），缺失即 fail loud。`ryn.json` 补 `bundle{manufacturer, icon}` 段（版本仍以 CI `-p:Version` 为准）。CI 加注册表 `pv` 探针回显 + staging 布局断言。`verify-package-layout.sh` 加 Windows 内容断言（exe/dll/原生库/runtimes/wwwroot），仅含主 exe 的 target 生效，Linux/mac 腿天然跳过。

## Alternatives considered

- **改走 WiX/MSI（跟 Ryn `BundleCommand`）**：Ryn 官方确为 MSI（确定性 UpgradeCode + MajorUpgrade + 逐目录 Component）。落败原因：hairyf 实证离线包数万文件下 WiX 慢且受 2GB cab 上限约束，只出 NSIS；我方包结构同属多文件 publish 全量，Inno 在此形态更快且已有 fail-loud 链（2026-08-29 实锤），换链收益不抵迁移成本。
- **捆 WebView2 offlineInstaller（212MB）**：hairyf 注释实证体积从 63MB 涨到 275MB。落败原因：online-first 拍板下安装器保持小体积，缺时提示用户自装与 CI 预装 Evergreen 已覆盖验证面。
- **把 dsh 打进包内（跟 DSH 官方 asar 内 dsh）**：与 online-first「依赖系统全局 node + 全局 dsh」拍板方向相反。落败原因：已拍板事项不重议。
- **真签名解决 SmartScreen**：三家均为真签，我方公开包保持未签名是既定决策（自签仅内部验证）。落败原因：开源既定决策，本批不动。

## Consequences

升级覆盖可预期（AppId 稳定）+ `dsh://` 可用 + 裸机缺 WebView2 时用户得明确指引而非白窗 + 图标缺失与文件漏配在 CI 即红。代价：AppId GUID 永不可改（改则断升级链）；`[Code]` 注册表探针 HKLM 路径在受限账户下跳过，属已知弱探针，CI 回显位已注明。

## Testing

`bash -n` 全改脚本；`verify-package-layout.sh` 对本地 publish/staging 目录手跑；`verify-adr-format.py` 新建即校验。`dotnet test` 不受影响（未碰源码）。
CI 注册表探针只取证不判门：脚本尾 `$error.Clear(); exit 0`——`try` 吞掉的 HKCU miss 仍残留 `$error`，会令 `powershell.exe -Command` 以 exit 1 退出（run `36307146505` 实证：证据已打印仍红），清错后退出码归零；真语法错误无输出仍非零，fail loud 不变。
