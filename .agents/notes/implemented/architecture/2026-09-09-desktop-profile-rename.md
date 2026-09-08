# Agent Note: desktop-profile-rename

Status: implemented

Review: FULL/2026-09-09/R1=ok R2=ok R3=ok

## Problem

桌面壳跟版全局 dsh（`DshSpec = @deepseek-ai/dsh@alpha`，不钉版）在 0.1.5-alpha.1 上线后实机拒启：`error: profile "desktop" is managed exclusively by the Electron application`。上游实证（registry 源码包 `apps/cli/src/args.ts`，commit `19444907f`「feat: electron 打包」）：CLI 新增 `rejectElectronProfile`，对字面名 `desktop`（大小写不敏感）的 boot 与 `dsh plugin` 调用**无条件硬拒**——这个名字被圈占给官方 Electron 桌面端（`apps/desktop` = `@deepseek-ai/dsh-desktop`，私有闭包形态，profile 目录 `<home>/profiles/desktop` 归其独占管理）。我方 spawn `dsh --profile desktop --port 0` 与 `dsh plugin --profile desktop add` 两个调用点同时被拦。

跟版契约（simple-shell-single-global-dsh）本身不变；冲突只在 profile 字面名。回钉 0.1.3-alpha.2 只是止血——官方 Electron 端正式发布后，携带同款检查的每一版 alpha 都会经桌面的自动跟版路径再次复现。

## Decision

桌面专属 profile 改名 `desktop` → `dotnet-desktop`，存量目录一次性迁移。

- **单点常量**：改名只动 `HarnessRuntimeHost.DesktopProfileName` 的值；spawn 参数、`dsh plugin` 参数、PID/端口文件路径、诊断白名单、自举清单均消费此常量，零散点改齐。
- **名字取向**：官方圈的全是品类词（web/headless/sdk/acp/desktop），技术栈专名 `dotnet-desktop` 落在其圈名域之外——反向隔离，被再次圈占的风险趋近于零。
- **迁移**（`DesktopProfileBootstrap.MigrateLegacyProfileName`，在 `EnsureProfile` 之前执行）：
  - `profiles/dotnet-desktop` 已存在 → no-op（绝不合并两目录，新目录所有权归 dsh/用户）；
  - 否则 `profiles/desktop` 存在 → 同卷 `Directory.Move`（原子 rename，插件装配/端口记忆/PID 文件整体保留）；
  - 移动失败（如目标位被占）→ 日志留痕、不阻断启动——降级为全新 profile 自举（`EnsureProfile` 兜底）；旧目录保留，目标位让出前后续启动会继续尝试迁移并留痕，新目录一经自举成功即命中「新目录已存在」跳过分支，绝不合并。
- **迁移时机**：`EnsureDesktopProfile` 内、spawn 前（与自举/reconcile 同一前置块）。

## Alternatives considered

- **`harness-desktop`**：落败——贴官方产品家族词（DeepSeek Harness），仍在其潜在圈名域内；`dotnet-desktop` 用技术栈域反向隔离更稳。
- **`dsh-desktop`**：落败——与上游 Electron 包名 `@deepseek-ai/dsh-desktop` 字面同名，语义上易被误认为官方桌面端的名字。
- **钉版 `DshSpec` 退出 `@alpha` 跟版**：落败——回退 online-first「内核升级与壳发版解耦」契约（重蹈 2026-08-31 钉版 ADR 已否决的形态），且治标：用户全局 dsh 与桌面共享，下一版上游任何 breaking 都会再踩；改名后跟版照常。
- **不迁移存量 `profiles/desktop`（干净起步）**：落败——旧插件装配/端口记忆凭空作废需全部重装，用户已拍板迁移。
- **向上游申请绕过令牌**（如 env 白名单）：落败——现版检查无任何绕过面；即便上游加，也等于把我方 profile 永久绑在其 Electron 端的所有权模型上。

## Consequences

- 与官方 Electron 桌面端（发布后）可在同一 home 共存：`profiles/dotnet-desktop` 与 `profiles/desktop` 各自独立，互不触碰。
- 存量用户首启一次目录改名，插件装配与端口记忆无感保留；迁移失败可见于 host.log 且不阻断启动。
- 用户在终端手动 `dsh --profile dotnet-desktop ...` 仍可用（CLI 不拒新名）；继续用旧名 `--profile desktop` 会被 0.1.5+ CLI 拒——那是官方 CLI 对其 Electron 端 profile 的所有权语义，文档已同步。
- `DiagnosticsExporter` 白名单随常量取新路径；旧名目录不再收录（迁移后不存在；迁移失败时其端口文件缺失仅影响诊断包完整度，非运行态）。

## Related

- [shared-home-desktop-profile](2026-08-23-shared-home-desktop-profile.md)（implemented）：**profile 名条款由本篇取代**（`desktop` → `dotnet-desktop` 并加迁移）；共享 home / 壳自举 / reconcile 决定不变。
- [simple-shell-single-global-dsh](2026-08-31-simple-shell-single-global-dsh.md)（implemented）：`@alpha` 跟版契约不变，本篇是对该契约在新版上游下的兼容修补。
