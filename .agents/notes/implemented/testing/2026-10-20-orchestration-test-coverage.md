# Agent Note: 编排层与零引用源文件补测（可测者补用例、不可测者写明缺什么缝）

Status: implemented

中文（双语暂不启用；启用时恢复 .md 与 .zh.md 镜像 + .i18n.yaml 配对机制）

## Problem

审计点名 6 处「有实现但无自动化测试」的编排层文件（`StartupNoticeService`、`RecoverySurface`、`ProfileLifecycle`、`CaptionBarInstaller`、`AppRelaunch`、`HarnessRuntimeHost.Handoff`），另有一批 tests/ 下零引用的源文件。编排层缺覆盖的直接后果是：引导期横幅门控、profile 自举四步、重启派生参数这些「壳的行为骨架」改坏了没有用例能红。

## Decision

逐个判定可测性，能测的补用例，不能测的写明缺什么缝——不为凑数写无断言的用例。

**已补测（含最小可测性改造）**：

- `StartupNoticeService`：版本探测会 spawn `dsh --version` 进程，原为静态调用不可测；抽出构造注入的探测口（缺省仍是真探测），据此覆盖「引导未落定不推横幅 / 版本低于底线推横幅 / 探测失败跳过 / 脏退横幅」四条判定。
- `ProfileLifecycle`：经 `DSH_DESKTOP_DSH_HOME` 指向临时 home，覆盖「冷启动自举桌面 profile 三件套 + 二次调用幂等」「旧名 profile 目录迁移到新名且内容保留」。
- `AppRelaunch`：`SpawnSelf` 会真 spawn 进程，不入测试；把「派生自身启动参数」抽成纯函数 `BuildStartInfo(exePath, args)`，据此覆盖启动形态与参数透传。
- `HarnessRuntimeHost.Handoff`：交接面（`TryRideMarketRelayAsync` 及其收养/收割/漂移处置）已有既存用例覆盖，本批不新增。

**判定为不可测（缺缝，改造代价高于收益）**：

- `RecoverySurface` / `CaptionBarInstaller`：两者的构造参数都含 Ryn 的 `CurrentWindowAccessor`——该类型只有非公开构造（依赖 Ryn 的 `NativeApplicationAccessor`），且 `IRynWindow` 有 40 余成员、仓内无假件。要测就得把窗口访问面改成端口并动注册处装配；其纯逻辑部分（失败面文案、恢复页脚本、顶栏脚本构建）已各有回归用例。
- 其余零引用源文件：本批只覆盖纯逻辑且复用面大的 `ConfigSectionJson`（三类选项记录共用的 appsettings 节装卸缝）；其余为零引用的注册/装配/IO 面（`DesktopAdapter`、各 `*Registration`、托盘与页面桥的进程面），它们的行为由各自既有用例与门禁间接覆盖，补「跑一遍不红」的用例无价值。

## Alternatives considered

- **维持现状不补测**：落败。编排层是壳的行为骨架（横幅门控、profile 自举、重启派生），改坏了没有用例能红；审计已逐条核实这些文件无自动化覆盖。
- **给每个零引用源文件都补一个「跑一遍不红」的用例**：落败。无断言的用例只增运行时间不增信号，还会在断言漂移时给出假绿。
- **为 `RecoverySurface` / `CaptionBarInstaller` 造 Ryn 窗口假件**：落败。`IRynWindow` 有 40 余成员，假件本身即一大块无业务含义的测试代码，维护成本高于其覆盖的编排逻辑；改为端口抽取则要动注册处装配，超出本批范围。
- **把版本探测与重启派生都留作静态调用、只测外围**：落败。那样能覆盖的仍只是「横幅没被调用」这类弱断言，真正的判定（底线比较、参数透传）仍无覆盖。

## Consequences

- 编排层三处关键判定进入自动化回归；两处新增测试缝（版本探测口、`BuildStartInfo`）都是可选注入，生产调用点未变。
- 测试缝留在生产类型上：`StartupNoticeService` 多一个可选构造参数，`AppRelaunch` 多一个程序集内可见的纯函数——两者都只暴露派生信息，不引入新状态；`StartupNoticeService` 是 public 类型，本批只在公开构造末尾**追加一个可选注入参数**
（7 参变 8 参，对外二进制不兼容但本仓无外部消费者），公开类型集合不变——`AppRelaunch.BuildStartInfo` 仍是程序集内可见。
- 不可测的两处编排类保持原样，缺口写在本篇；后续若要覆盖，前置是 Ryn 窗口面端口化（另一决策，不在本批）。

## Testing

新增用例分属 Core.Tests / Infrastructure.Tests（编排层按层镜像：Core 判定 → Core.Tests，Infrastructure 适配器 → Infrastructure.Tests）。用例总数与覆盖率基线的唯一家在 `scripts/test-baseline.json`（本篇不复制数字），本批新增数由主会话汇总后同步基线与 README 双语徽章。

## Related

- 审计报告 §5.2 与 §7 的零引用清单（`.plan/审计报告-功能闭环与架构审计-2026-10-20.md`）。
- 重复收敛：[2026-10-20-duplicate-consolidation](../simplification/2026-10-20-duplicate-consolidation.md)。
- 失败面与恢复页契约：[2026-10-14-native-failure-surface](../architecture/2026-10-14-native-failure-surface.md)。
