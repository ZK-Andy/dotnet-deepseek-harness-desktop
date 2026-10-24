# Agent Note: Avalonia G4 打包面——原生库清单换底与 Linux 依赖降到 GTK3 栈

Status: implemented

Review: FULL/2026-10-24/R1=ok R2=ok R3=ok

## Problem

宿主在 G0–G3 换成 Avalonia 后，发包面的断言仍停在旧宿主：`verify-package-layout.sh` 与
`PayloadSmoke` 断言 `libsaucer*`/`libryn-pty`（动态链接 WebKitGTK 6 / GTK4），deb `Depends:` 与
rpm `Requires:` 仍声明 `libwebkitgtk-6.0-4` + `libadwaita-1-0`，CI 也只装这套 GTK4 运行时。

新宿主实际加载的是 Skia/HarfBuzz（macOS 另 `libAvaloniaNative`，Windows 另 ANGLE 的
`av_libglesv2` 与 `WebView2Loader`），系统 WebView 在 Linux 走 WebKitGTK 4.1（GTK3 API）。
三处错位的后果各自独立：包缺渲染栈库时三平台布局断言照绿、首帧才炸；最小系统按声明装完仍缺
GTK3/WebKitGTK 4.1；探针把已无加载路径的 saucer 当成门，反过来要求 CI 装 GTK4。

## Decision

- **断言清单 = 宿主实际加载面**（`verify-package-layout.sh` 与 `PayloadSmoke` 同源）：
  Linux `libSkiaSharp` + `libHarfBuzzSharp`；macOS 再加 `libAvaloniaNative`；Windows 再加
  ANGLE `av_libglesv2`。双探测面（根或 `runtimes/<rid>/native`）保留——publish 现行把 RID
  原生资产落根，`runtimes` 目录仍由 Ryn 包供给。
- **`WebView2Loader.dll` 不算加载面**：`Avalonia.Controls.WebView` 12.1.1 包内零原生资产，
  Windows 路径用自带 `ManagedWebView2Loader` 定位已安装的 WebView2 运行时；产物里的
  `WebView2Loader.dll` 只由 Ryn.Interop 供给，宿主不加载它。G5 摘 Ryn 后该文件消失，所以既
  不作为布局断言、也不作导出断言——否则是对死文件的假红，对「运行时可用」也没有任何守护。
- **Ryn 的 `libsaucer*`/`libryn-pty`/`WebView2Loader` 退出断言**：包引用要到 G5 才摘，产物里
  物理仍在，但新宿主没有它们的加载路径；断言只跟加载面，否则是对死代码设门。
- **Linux 依赖换 GTK3 栈**：deb `Depends: libgtk-3-0t64 | libgtk-3-0, libwebkit2gtk-4.1-0`
  （24.04/trixie 起 GTK3 二进制包 t64 改名，新名在前、老发行版走第二候选）；rpm
  `Requires: libgtk-3.so.0()(64bit), libwebkit2gtk-4.1.so.0()(64bit)`（限定形沿用历史教训：
  裸名在 aarch64 无提供者、在 x86_64 会被 i686 多库包侥幸满足）。CI 的 Linux 腿同装这套包，
  与包元数据同源。
- **PayloadSmoke 换用例**：删 saucer 图像解码与 ryn-pty/ConPTY 用例，改为经 libSkiaSharp 的
  `sk_data_new_from_file` → `sk_image_new_from_encoded` → `sk_image_get_width` 真解码产物内
  `icon.png`；五个 Skia 导出由父进程**逐个**断言——子进程缺符号时静默退出，父进程不逐个断言
  就会把导出漂移放成 PASS。功能用例仍在子进程执行，native 硬崩溃不截断诊断清单；
  stdout/stderr 改异步读，`WaitForExit` 的 90s 超时才真正可达（同步 `ReadToEnd` 会先阻塞到
  子进程退出，旧实现的超时分支不可达）。
- **顺手修负例崩溃**：父进程清单断言失败时 `resolvedPaths[库名]` 直接抛
  `KeyNotFoundException`（旧代码同形，删根副本时因 `runtimes` 副本仍在而从未暴露）；改为
  `TryGetValue` 后跳过功能用例，缺库只报一条诊断、exit 1。
- **Windows 安装器检测保留**：Avalonia `NativeWebView` 在 Windows 同为 WebView2 运行时，
  既有注册表探针与提示文案继续有效，只去掉「Ryn DoctorCommand」的出处引用。
- **更新 feed 不动**：`ReleaseMetaClient` 按资产名抓链、`UpdateInstaller` 按扩展名分派，两侧都
  不含原生库清单；本轮换底对自更新路径无影响。

## Alternatives considered

- **保留 Ryn 断言与 GTK4 依赖直到 G5**：断言对象必须等于加载面；留着等于让死代码决定包的运行
  时要求，CI 每腿还多装一套 GTK4。落败。
- **只改 CI 依赖、不动包元数据**：装包时的依赖解析是用户侧失败，发布面看不见；把本该在打包期
  暴露的错推迟到用户机器。落败。
- **探针改用 SkiaSharp 托管包解码**：探针会加载自己 publish 出来的 `libSkiaSharp`，断言对象变成
  探针自带库而不是被测产物，负例永远绿。落败。
- **本片在应用内加 WebView2 缺失检测（Windows）**：要包住 `NativeWebView` 构造的失败路径并复用
  失败面模态，本机无 Windows 无法验证真机行为；做成未验证的 GUI 退出路径比明确留债更危险。
  推迟到 G4-b。落败（本片）。

## Consequences

发布前三平台都拦得住「渲染栈缺库」；Linux 包要求从 GTK4/WebKitGTK 6 降到 GTK3/WebKitGTK 4.1，
`webkit2gtk-4.1` 在 jammy(22.04)+/bookworm(12)+ 均有包，可装系统面不变；GTK3 的双名依赖覆盖
noble/trixie 的 t64 改名。

残留代价：

- 包内仍物理携带 `libsaucer*`/`libryn-pty`/`WebView2Loader`（Ryn 包引用随 G5 摘除），体积与
  扫描噪音留着。
- `runtimes/<rid>/native` 目录必在的断言今天只由 Ryn 包供给（Skia/HarfBuzz 只落根），G5 摘包后
  这条断言必然假红，须在同一批删掉（自测夹具里两处也钉着它）。
- Windows 应用内 WebView2 缺失检测由 G4-b 落地（见 [avalonia-g4-webview2-preflight](2026-10-24-avalonia-g4-webview2-preflight.md)）；安装器提示保留为安装期提示。
- mac/win 的 publish 目录未在本机产出，两平台的原生清单只经自测夹具与 CI 腿验证。
- Linux 依赖换底能否真起窗口由 CI 冒烟腿判定；沙箱起不了 GUI，本机无法复现。

## Testing

- `verify-package-layout.sh --self-test`：三平台夹具改用 Avalonia 清单后 PASS；缺
  `libSkiaSharp` / `WebView2Loader` 判红，仅 `runtimes` 面存在仍放行。
- `package-linux.sh --stage-only` 对真实 publish 目录：staging 112M，布局断言通过。
- `PayloadSmoke` 对真实 publish：`loaded: libSkiaSharp.so` / `libHarfBuzzSharp.so`、
  `decoded: icon.png (512px 宽)`、PASS；负例（删 `libSkiaSharp.so`）exit 1 并列出缺失项。
- `actionlint` 与 `verify-shell-standards.sh`（S1–S6，含 shellcheck）通过；文档预算/链接闸通过。
- `dotnet build` 0 警告；`dotnet test` 1203/1203。

## Related

- [avalonia-host-migration](../../proposed/architecture/2026-10-22-avalonia-host-migration.md)：阶段表 G4
- [avalonia-g4-webview2-preflight](2026-10-24-avalonia-g4-webview2-preflight.md)：G4-b 应用内运行时探测
- [payload-smoke-probe](../testing/2026-09-13-payload-smoke-probe.md)：探针形态与子进程隔离
- [delivery-chain-gaps](../testing/2026-10-20-delivery-chain-gaps.md)：Linux 腿载荷探针的由来
