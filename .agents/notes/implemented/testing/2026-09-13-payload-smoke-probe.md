# Agent Note: 打包期载荷冒烟探针——publish 产物 native 面 FFI/图像/PTY 探针

Status: implemented

Review: FULL/2026-09-13/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

`dotnet test` 与安装冒烟都绿仍不等于「打包产物能跑」：自包含 publish 的 native 载荷（Ryn.Interop 携带的 `saucer`/`saucer-bindings`/`saucer-bindings-desktop`/`WebView2Loader`/`ryn-pty`）只有到真机首启才被 OS loader 实际加载——缺库、rpath/执行位破坏、导出符号缺失、图像解码原生路径损坏这几类故障在 Linux 源码 CI 与安装链冒烟（`smoke-install-{windows,macos}.sh` 只验「装得上、起得来」）全部不可见。ZK 无 win/mac 真机（见 [testing/community-targeted-testing](2026-08-20-community-targeted-testing.md)），该缺口恰落在两平台 runner 腿上。上游桌面客户端对同形态风险以 `tests/fixtures/runtime-payload-smoke.mjs` 在打包产物上冒烟 PTY/FFI/图像——上游把打包产物（非源码）作为独立验证层，本 ADR 取同一方法。

## Decision

新增独立控制台工程 `tests/DeepSeek.Harness.Desktop.PayloadSmoke`（框架依赖，net10.0，随 slnx `/tests/`），**以 publish 产物目录为参数**对产物 native 面冒烟；`package-windows.yml` 与 `package-macos.yml` 在 `dotnet publish`（壳）之后、打包脚本之前接线：publish 探针工程 → `dotnet <探针>.dll <publish 目录>`。探针用例四件（全部对 publish 目录内文件操作）：

1. **native 清单存在性**：按 OS 断言预期原生库在产物目录（根或 `runtimes/<rid>/native`，同 Ryn `NativeLibraryResolver` 探测序）——win x64：`WebView2Loader.dll`+`saucer*.dll`；osx：`libsaucer*.dylib`+`libryn-pty.dylib`。
2. **FFI 加载 + 导出解析**：逐库 `NativeLibrary.TryLoad`（绝对路径）+ 哨兵导出 `GetProcAddress`（`saucer-bindings`→`saucer_icon_new_from_file`、`ryn-pty`→`ryn_pty_spawn`、`WebView2Loader`→`GetAvailableCoreWebView2BrowserVersionString`）；加载失败即缺依赖/rpath/执行位故障现形。`saucer`/`saucer-bindings-desktop` 无稳定哨兵名，只断言可加载。
3. **图像解码**：经导出的 `saucer_icon_new_from_file` 对产物内 `icon.png` 真解码（断言非空句柄、error==0）再 `saucer_icon_free`——跨库调用同时穿透 `libsaucer` 原生解码路径。
4. **PTY 功能性**：Unix 腿经导出的 `ryn_pty_spawn` fork 真伪终端跑 `/bin/sh -c echo` 并读回 token（fork/exec/read/waitpid 全链；工作流仅 macos 腿执行，linux 支持本地对产物自测）；win 腿对应面 = kernel32 `CreatePseudoConsole` 功能探针（ConPTY 由 OS 提供、非载荷，故只探 OS 面）。

失败 fail loud：逐条列出缺失文件/加载失败/断言失败原因，exit 1；workflow 步骤 `timeout-minutes: 5` 防只读阻塞挂死。

Linux 不接腿：ZK 本机即 Linux 真机与部署机（日常实机验收覆盖），且 deb/rpm 已有安装冒烟；探针保持 win/macos 两腿——缺口所在即探针所在。

## Alternatives considered

- **把用例放进既有 xunit 测试工程**（e2e 闸门式）：测试宿主从源码树输出目录运行，`AppContext.BaseDirectory` 不是 publish 产物，须把产物路径全程参数化，且 e2e 开关（`DSH_TEST_E2E`）/ci 矩阵与打包流水线耦合。落败：探针必须作为独立 fail-fast 小 exe 直跑产物层，包工作流内两行接线即可，不需要测试宿主间接层。
- **扩展现有 `smoke-install-*.sh` 安装冒烟脚本**：native 加载/导出/图像解码语义在 bash/powershell 无法表达（需托管 runtime 做 `NativeLibrary` 调用），脚本只能做文件存在性。落败：存在性检查只是探针 1/4，功能性用例必须托管代码。
- **对安装器产物（装完后）探针而非 publish 层**：安装链故障已由 `smoke-install-*` 覆盖，装完后探针把两类故障混在一起且复现层变深。落败：publish 层是 native 面故障的最小复现层，且免安装等待。
- **同批接 package-linux 腿**：Linux 是 ZK 本机真机、日常实机验收即覆盖；探针接入成本不小（runner 上 dlopen GTK 依赖链风险）。落败（defer）：收益面最小，后续若 Linux CI 化部署脱离 ZK 本机再接。

## Consequences

代价：Ryn 原生 ABI 的哨兵导出名（`saucer_icon_new_from_file`/`ryn_pty_spawn`）钉进探针源码，Ryn bump 破坏 ABI 时打包流水线在探针步 fail loud——这是探针的职责而非缺陷；`PayloadSmoke` 随 slnx 参与常规 build/门禁（体积/约定），维护面 +1 个小工程。收益：win/macos 无真机条件下，「源码绿 ≠ 产物能跑」缺口收口——native 载荷缺库/rpath/导出/解码/PTY 五类故障在打包期即拦，不再漏到用户实机首启。

## Testing

- 探针本机（linux-x64 对壳 Release publish 产物）实跑验证存在性/加载/图像解码用例（PTY 用例 linux 同源可跑）；win/macos 腿由 package 工作流 dispatch 实跑验证。
- `dotnet test` 全绿 0 警告（探针工程随 slnx build）。

## Related

上游探针证据 `deepseek-ai/deepseek-harness` `apps/desktop/tests/fixtures/runtime-payload-smoke.mjs`（对打包产物冒烟 PTY/FFI/图像，本 ADR 的方法来源）；[testing/2026-08-20-community-targeted-testing](2026-08-20-community-targeted-testing.md)（本探针把其「CI 自动化兜底」对产物 native 面加厚一层，互补非取代）；`implemented/architecture/2026-08-29-online-first-unbundled-runtime.md`（online-first 后产物 = 壳 publish，无捆绑闭包）。
