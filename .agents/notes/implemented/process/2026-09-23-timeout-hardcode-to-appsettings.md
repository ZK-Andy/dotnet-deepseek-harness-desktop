# Agent Note: 超时写死收归 appsettings

Status: implemented

Review: FULL/2026-09-23/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

全仓 30 处 `TimeSpan` 与 3 处重试上限以字面量散落 13 个文件（接力等待/探针/监督/健康/引导/退出/单实例/IPC）。仓库规则要求可调参数进配置模型（`Update`/`RuntimeBootstrap` 两节是先例），超时/重试正是典型可调参数；但其中一部分写死实为协议/安全不变量，须一次划界，不把不该调的放出去。

## Decision

- 新增 `RuntimeTimeouts` 记录（Infrastructure/Runtime，`appsettings.json` 的 `RuntimeTimeouts` 节，`Load` + 纯函数 `Parse`，节缺失/损坏回退默认不挡启动）：23 项全部以现行值为默认值，同值迁移零行为变更（接力 1s/2s/2s + 收养轮询 1s、端口 500ms/web 1500ms、生命周期门 3s、spawn 60s、通知 2s、监督 join 2s/重启 60s/重试 2s/1s、健康首拍 10s、引导落定 120s、导航提交 5s、IPC 5s/1s、版本探测 8s、横幅 30×1s/推送 15×400ms；重试次数与节拍是同一调参面，同节收容）。
- `RuntimeBootstrapOptions` 扩展 3 项（`FetchTextTimeoutSeconds` 30、`PreinstallPollIntervalMilliseconds` 200、`PreinstallChoiceTimeoutMinutes` 5）：`FirstBootBootstrapService` 经既有 `_options` 字段消费，零 plumbing。
- `UpdateOptions` 扩展 `PkexecObserveSeconds`（10）：`UpdateCoordinator` 读配置后经 `UpdateInstaller.LaunchAsync` 显式传参（唯一调用方）。
- `RuntimeSupervisor` 两处重试延迟经构造注入（`restartTimeout` 既有先例，调用方唯一，无测试 churn）。
- `PluginInstallProbe.ProbeTimeout` 与主 spawn 同宽的耦合不变量合并为同一配置源（`SpawnTimeoutSeconds`），不再各自写死 60。
- `LauncherActivation.NotifyPrimary` 超时本就是调用方参数，组合根改传配置值。
- 保持固定的不变量：回环地址/端口范围/sha256 长度/exit 码/`--profile` 命令形状/socket 名/uid 后缀/plist doctype/安装器参数/帧缓冲与截断长度/scope 正则/locale 校验界/profile 名身份（改名即断裂）/`HostLog` 5MB 轮转（运维默认）/`RuntimeVersionGate.MinimumVersion`（协议底线，既有声明）/`ExitPipeline` 8s 双看门狗（安全不变量，见 Alternatives）。
- `appsettings.json` 示范全量默认值（显式优于隐式，可调参数以看得见为完成）。

## Alternatives considered

- **每域一节（Relay/Supervisor/PageBridge/Exit…）**：落败——节碎片化、Load plumbing 翻倍；单一 `RuntimeTimeouts` + 两处既有节扩展已覆盖全部消费点，无剩余。
- **PagePump 自建 Desktop 侧 options 或经组合根逐层传参**：落败——5 处调用点签名 churn；Desktop→Infrastructure 引用既有，`PagePump` 只读解析后值，文件 IO 仍在 Infrastructure `Load` 内，不新增运行时边界。
- **`ExitPipeline` 看门狗一并入配置**：落败——安全不变量（fail-safe bound）：可调即允许配出“永不强退/立即强杀”，与看门狗存在理由相逆；8s 双 bound 保持固定。
- **组合根持单份超时向下传（消灭 6~7 处重复读）**：落败——静态消费点（探针/单实例/PagePump）无法从组合根接值而不改签名或转实例，churn 代价远超启动期一次性 KB 级重复读取；`Load` 幂等同值，重复读无语义差。
- **缺配置/坏配置 fail loud**：落败——与 `UpdateOptions`/`RuntimeBootstrapOptions` fail-safe 哲学一致，增强配置不挡启动。

## Consequences

- 收益：超时/重试全部可在 `appsettings.json` 调整，无需改代码重新编译；默认值测试锁定，同值迁移可证零行为变更。
- 代价：`RuntimeTimeouts` 23 项单记录偏宽（按“单一超时家”取舍，优于节碎片）；静态消费点每类一次文件读取（KB 级启动期一次性，可忽略）。
- 验证：新增 `RuntimeTimeoutsTests`（默认值锁定 + Parse 全/偏/缺/坏）+ `UpdateOptions`/`RuntimeBootstrapOptions` 扩展项用例；`dotnet test` 全绿 0 警告；`dotnet format --verify` exit 0。

## Related

- [coding-standards（强制力度）](../../../../docs/coding-standards.md)：可调参数进配置模型，协议常量与安全不变量固定。
- [ide0005-enforce-via-format-gate](2026-08-31-ide0005-enforce-via-format-gate.md)（implemented）：配置缺省回退哲学（fail-safe）先例。
