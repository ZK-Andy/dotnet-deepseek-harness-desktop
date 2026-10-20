# Agent Note: Core 纯度下沉（文件/环境访问经端口，适配器落 Infrastructure）

Status: implemented

Review: FULL/2026-10-20/R1=ok R2=ok R3=ok

## Problem

「Core 是纯逻辑」此前只在**程序集层**成立：`Core.csproj` 零外层项目引用、Core 编译产物零
DeepSeek*/Ryn* 引用（架构测试逐条断言）。但程序集边界挡不住**进程环境面**——Core 自己读了盘、读了环境变量：

- `Core/Plugins/PluginVersionCheck.ReadBundledVersion/ReadInstalledVersion`：解 tgz（gzip+tar）、读
  `node_modules/<pkg>/package.json`。
- `Core/ProfilePackageCheck.IsBundleInstalled`：`File.Exists` + `File.ReadAllText` 读 profile 清单。
- `Core/Localization/UiLocale.DetectOsLocale`：读 `LC_ALL`/`LC_MESSAGES`/`LANG` 环境变量族与 `CurrentUICulture`。

后果：Core 的单测要真碰盘（临时目录、tgz 夹具），「Core 可单测」是靠文件系统而不是靠纯函数成立的；
新增一条 Core 判定就意味着新增一处 IO 触点，边界只能靠人记。审计把这条列为「Core 不纯」偏离项，
并指出这是「本项目最值钱的一条」——因为它把「Core 纯逻辑」从口号变成可机验事实。

## Decision

- **端口在 Core，IO 适配器落 Infrastructure**：
  - `Core/Plugins/IPluginPackageReader`：读随包 `package.json` 文本（tgz/目录两种形态）、已装副本
    `package.json` 文本、profile 清单文本。
  - `Core/Localization/IHostLocaleProbe`：探测宿主 locale（环境变量族 + `CurrentUICulture`）。
- **Core 侧只留纯函数**：
  - `PluginVersionCheck` 收成 `TryExtractVersion(json)` / `ReadVersion(json)` / `TryReadVersion(json)`（文本损坏按未知）
    / `NeedsUpgrade(installed, bundled)`；读盘与 tgz 解包移到 `Infrastructure/Plugins/PluginPackageReader`。
  - `ProfilePackageCheck.IsBundleInstalled` 改为吃**文本**（null = 未安装）；`BundleEntryEquals` 提为公开，
    安装侧（`MarketInstallHelper.Json.cs`）的同名私有副本随之删去、改复用 Core 这一份。
  - `UiLocale` 经 `IHostLocaleProbe` 取初值（未接线探测端口即回中文）；读环境变量移到
    `Infrastructure/Runtime/OsLocaleProbe`。
- **装配改线**：`MarketInstallHelper` 传 `new PluginPackageReader()`；`PreBuildRegistration` 的
  `UiLocale` 传 `new OsLocaleProbe()`。
- **机器门禁**（新增 `scripts/verify-core-purity.py`）：扫 `src/DeepSeek.Harness.Desktop.Core/**/*.cs` 源文件，
  出现文件/环境/进程 IO 触点（`File.`、`Directory.`、`FileStream`、`StreamReader`/`StreamWriter`、`ZipFile`/`TarReader`/
  `GZipStream`、`Registry`、`Environment.Get*`/`Set*`、`Path.GetTemp*`、`Process.Start` 等）即判红——扫描用
  `gate_common.CSharpLineScanner`（剥注释、字符串内容留空），故注释与字符串内的提及不算触点。至此「Core 纯逻辑」
  有可执行判据，不再依赖人记。

## Alternatives considered

- **维持现状不修**（Core 继续自读盘/读环境变量）：落败——「Core 纯逻辑」只在程序集层成立，测试要真碰盘，
  每加一条 Core 判定就多一处 IO 触点；审计把它列为偏离项，且这是本项目最值钱的一条（纯度一旦可机验，
  后续所有 Core 改动都自动受检）。
- **把 IO 判定整体搬出 Core**（插件版本判定、profile 就位判定、locale 初值都挪到 Infrastructure）：
  落败——那会把纯判定（版本比较、bundles 归属、locale 合法性）一起搬走，Core 就只剩空壳；
  拆法应是「解析与判定留 Core、读盘留 Infrastructure」，判定本身才可单测。
- **用 `Func<>` 委托替代端口**（如 `Func<string, bool>` 读文件）：落败——委托缝不表达「这是外部边界」，
  依赖倒置的意图与实现分离度都弱于端口；本仓既有实践也是端口优先（`IRuntimeHost`/`IDiagnosticsExport` 等）。
- **只加机器门禁、不改代码**（先让门禁红、再逐步下沉）：落败——门禁会立刻判红且无处可退；本批把触点一次下沉到位，
  门禁随之转绿——先改后设门，顺序反了就成了「已知违规但不许修」。
- **门禁做成 C# 架构测试而非独立脚本**：落败——C# 侧没有仓内既有的「剥注释 + 剥字符串」扫描器可用
  （`gate_common.CSharpLineScanner` 是 Python 侧能力），手写一份等于把它降级重写（块注释跨行、verbatim 串、
  插值串都会误判）；故改落 `scripts/verify-core-purity.py`，复用同一份扫描器，与其它门禁同族。
- **改用 Roslyn 分析器把「Core 不得做 IO」做成编译期诊断**：落败——该诊断要判定「这个 IO 调用是否越过了
  Core 边界」，需要 Core 允许的 API 白名单随 BCL 版本漂移（分析器侧无稳定清单），且 IO 面判定本质是
  「调用了哪个 API」而非「语义上是否越界」；脚本扫描 + 枚举式禁用清单是本仓既有形态（仓规：能进 verify/analyzer
  就进，此处脚本形态更贴判据本身）。

## Consequences

- Core 源文件不再有文件/环境 IO 触点，且由门禁 `scripts/verify-core-purity.py` 机器化守门——新增 Core 判定若想读盘，门禁直接红。
- Core 的插件与 locale 单测改为纯文本输入（不再建临时目录、不再解 tgz）；读盘行为（tgz 结构异常 fail loud、
  已装副本读不出按未知）的测试移到 Infrastructure 侧，随适配器一起测。
- 「随包产物坏了必须可见」与「profile 是可再生状态、按未知处理」两种失败语义在 Core 侧显式分开
  （`ReadVersion` 抛 / `TryReadVersion` 回 null），不再靠同一段 IO 代码里的分支表达。
- Core 多两个端口、Infrastructure 多两个适配器；装配面只多两处构造参数（`MarketInstallHelper` 与
  `PreBuildRegistration`），组合根闸与注册闸方向不变。`PreBuildRegistration` 的 `new OsLocaleProbe()`
  进了组合根闸的 C7 预 Build 构造清单：该清单的纪律是「新开项须有承载它的 ADR」，本篇即承载方——
  共享态在容器之前构造（`UiLocale` 在构造期就解析初值），改由容器解析需把初值解析改惰性（动行为面），
  故此处只登记清单项（`scripts/verify-compose-root.py` 的 `PREBUILD_CONSTRUCTIBLE`）。
- 既有行为保持：已装副本 package.json 损坏仍按「版本未知 → 走升级/重装」（`TryReadVersion` 回 null），
  不是新故障语义。**一处可观察的语义修正**：已装副本文件**无权访问**时，旧实现外抛异常（被上层 catch 接住 →
  该插件跳过升级检查），新实现读盘适配器捕 `UnauthorizedAccessException` 返回 null（→ 版本未知 → 进待装清单重装）；
  方向与「已装侧读不出按未知走重装修复」一致。

## Testing

- `dotnet build` + `dotnet test` 全绿（用例数与覆盖率基线以 `scripts/test-baseline.json` 为准）；测试改造：Core 侧 `ProfilePackageCheckTests` 改吃文本，
  Infrastructure 侧 `PluginVersionCheckTests` 改测适配器（tgz/目录/已装副本/坏字段/损坏 tgz/缺文件六类形态
  与改造前一一对应），`BundledPluginCatalogTests` 传读盘端口。
- 新门禁实跑绿（Core 源文件无 IO 触点）；自测 4/4 绿（合规样例 / 直接读盘判红 / 读环境变量判红 /
  块注释内的触点不判红）；变异实测：向 Core 塞入 `File.Exists` 调用 → 判红并指出文件与行号，还原后复绿。
- 组合根闸、注册点纪律闸、尺寸闸、契约扫描全绿。

## Related

- [official-clean-architecture-adoption](2026-09-14-official-clean-architecture-adoption.md)——A2/A4/A5 依赖方向断言（本篇把它从程序集层扩到进程环境层）。
- [host-boundary-ports](2026-10-20-host-boundary-ports.md)——同批的宿主侧边界端口（本篇是 Core 侧的对称动作）。
- [bundled-plugin-version-aware-catalog](../feature/2026-08-25-bundled-plugin-version-aware-catalog.md)——随包插件版本感知判定的由来（判定留 Core、读盘下沉的原始动机）。
