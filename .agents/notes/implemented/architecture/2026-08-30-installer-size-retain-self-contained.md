# Agent Note: installer-size-retain-self-contained（安装器体积——维持 self-contained）

Status: implemented

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

2026-08-30 对照参照项目 `dsh-tauri-desk/deepseek-harness-desktop`（v0.9.4）的安装器体积，用户指出「我们的安装器还是有点大，参照只有 5MB（它自己宣称的）」。实测厘清双方真实构成后需拍板：是否为此把 .NET 壳换成 framework-dependent 或 AOT。

**实测基准**（本机 `dotnet publish -c Release -r linux-x64`）：

| 形态 | 体积 | 组成 |
|---|---|---|
| 我们 · self-contained（现状） | **88MB** | .NET 运行时 ~80MB（System.Private.CoreLib 15M、libcoreclr 6.8M、libclrjit 4.6M 等）+ Ryn 原生桥 `libsaucer.so` 2.6M + 应用壳 ~8MB |
| 我们 · framework-dependent | **8.2MB** | Ryn 原生桥 + 应用 dll + wwwroot，无 .NET 运行时 |
| 参照 · 安装器（v0.9.4） | exe 5.11MB / deb 9.55MB / dmg 7.4–7.9MB / msi 7.43MB | Rust/Tauri 原生二进制 + 系统 WebView；DeepSeek Harness 运行时首启在线下载 |
| 参照 · AppImage（v0.9.4） | 84.66MB | 该档**捆绑了 DeepSeek Harness 运行时** |

**「5MB」的来源**：参照是 Rust/Tauri，**没有 CLR/JVM 那类「非系统标准、必须单独安装」的托管运行时**；其 Rust std 静态烘焙进二进制，唯一外部依赖是系统标准件（WebKitGTK / GTK / libadwaita / glibc——恰与我们 Ryn 依赖的系统 WebView 库同层，两边都不打包）。所以参照安装器极小。真正的分水岭是 **.NET CLR 这一层**：自包含要随包带（+80MB），framework-dependent 则要系统装（.NET 10 不是任何主流消费 OS 的预装件，门槛比参照的「系统标准件」更重）。

## Decision

**维持 self-contained（.NET 运行时随包携带），接受与参照在「运行时携带」上的结构性体积差距。** 安装器体积 ~88MB（linux-x64）为当前形态的诚实基线，不为此转 framework-dependent 或 AOT。

- **不追参照的 5MB**：该目标需要对 .NET 而言「外包 CLR」或「用原生 AOT」，两者都引入本 ADR 拒绝的代价（见 Alternatives），与「零环境/下载即用、单一安装器"的既有产品承诺冲突。
- **修正过时预期**：[online-first-unbundled-runtime](2026-08-29-online-first-unbundled-runtime.md)「首启形态」1 条「体积降至 10MB 级」**只在 framework-dependent 下成立**；维持 self-contained 实际为 ~88MB。该条「10MB 级」表述已在此修正（在线 first 去掉的是 DeepSeek Harness 运行时闭包，不含 .NET CLR）。
- **体积约束锚点**：安装器体积的最大项是自包含 .NET 运行时，属平台成本，非打包缺陷；目标从「对齐参照的 5MB」调整为「不冗余携带」（已做到：不捆 DeepSeek Harness 运行时、per-arch 瘦身、无便携 zip）。

## Alternatives considered

- **framework-dependent（8.2MB，对照参照）**：壳变小、与参照同级，但用户系统需装 **.NET 10 运行时**——而 .NET 10 不是任何主流消费 OS 预装件（Win10/11 不带、macOS 不带、Linux 各发行版不一），前置比参照「唯有系统标准件」**更重**，且推翻「零环境/下载即用」宣称（online-first 只覆盖 DeepSeek Harness 运行时，不覆盖 .NET CLR）。若走此路需三平台安装器兜底装 .NET 10（Inno 检测装 / Linux `dotnet-runtime-10.0` 依赖 / macOS 引导装），引入新的打包复杂度与「已装端缺运行时」fail loud 面。权衡后否决——体积收益不足以换取前置变重与打包复杂度。
- **Native AOT（PublishAot=true，免 CLR）**：可免运行时随包、「下载即用」与体积兼得——但 Ryn 依赖反射/服务注册/DI 面，AOT 兼容性高风险（候选：`libsaucer` 原生桥 + 运行时反射装配），且 csproj 已注明「macOS 交叉编需关 AOT」（本项目 mac 走交叉编）。AOT 兼容需逐点验证，若不通则整条路径作废，成本高、回报不确定。当前 PublishAot=false（JIT）显式对齐发运（见 csproj 注释）。保留 AOT 为裕度，不作为降体积手段。
- **自包含 + trimming（PublishTrimmed + ReadyToRun）**：只能从 88MB 削到 ~50MB 级，仍远超参照，且对反射/Ryn 面有运行时风险，收益不足。否决为降体积手段（可作保守的独立优化另行评估）。
- **single-file / 压缩 deb/dmg**：仅减少文件数与传输压缩，不改变「携带 .NET 运行时」的本质，体积改善有限（.NET 运行时 dll 压缩率低）。不采纳为降体积手段。

## Consequences

- 安装器（deb/rpm ~78–115MB、dmg ~288MB、exe ~78MB 为 v0.3.12 捆绑态；online-first 后 self-contained 基线 ~88MB）依旧显著大于参照安装器——**这是对托管运行时平台的结构性差异，非打包缺陷**；对外宣称不再以「对标参照体积」为卖点。
- 体积上限锚定为「自包含 .NET 运行时 + Ryn 原生桥 + 壳 + 自有插件」，后续任何打包优化不再把「降到参照 5MB 级」作为目标。
- 若未来需要显著缩小安装器，唯一现实路径仍是 framework-dependent（接受 .NET 10 前置 + 安装器兜底装），届时重开本决策。
- 本决策只约束「安装器/壳是否携带 .NET 运行时」这一轴；DeepSeek Harness 运行时的 online-first 去捆绑（另一轴）不受影响。

## Related

- [online-first-unbundled-runtime](2026-08-29-online-first-unbundled-runtime.md)（implemented）：同轴去捆绑 DeepSeek Harness 运行时；其「首启形态」1 条「体积降至 10MB 级」预期已在本文档修正为「仅 framework-dependent 成立，self-contained 实际 ~88MB」。
- [ryn-shell-bundled-dsh-runtime](2026-08-20-ryn-shell-bundled-dsh-runtime.md)（archived）：历史「捆绑 dsh 运行时」形态，与 online-first 转向互为对立，被后续去捆绑取代；本决策不重新捆绑运行时。
