# Agent Note: 交付面自证（三平台布局断言 + macOS 应用图标 + 仅在打包腿执行的脚本自测）

Status: implemented

Review: FULL/2026-10-21/R1=ok R2=ok R3=ok

## Problem

打包产物与打包链路上有四处「只在打包腿才验证、PR 里看不见」的缺口：

1. **macOS 应用图标缺失**：`package-macos.sh` 生成的 `Info.plist` 没有 `CFBundleIconFile`，publish 直出的 `icon.png` 落在 `Contents/MacOS/`——bundle 不读这个位置，Finder/Dock 回退通用图标。包能启动、能安装，缺陷是静默的。
2. **布局断言只查 Windows 的原生库**：`verify-package-layout.sh` 的原生库/runtimes 断言写在 Windows 分支里，Linux/macOS 只查主程序、可执行位与插件 tgz。Ryn 的 `NativeLibraryResolver` 只认内容根与 `runtimes/<rid>/native` 两个探测面，缺 `libsaucer*.so/.dylib`、`libryn-pty` 要等首帧才炸；Linux/macOS 包缺原生库可以一路绿到用户机器。
3. **两个脚本的自测面缺席**：`scripts/dev-sign.sh` 的自签调用面（`codesign`/`signtool` 的实参、身份透传、失败分支）从不在 CI 执行，只被 `package-{macos,windows}.sh` 的 `SELF_SIGN=1` 腿引用；`scripts/smoke-linux-rpm.sh` 有内层脚本自测却无外层自测，`docker run` 的挂载、env、镜像参数无任何离线回归——两者都只在 dispatch/tag 打包腿上被间接执行。
4. **README 的 dev 自更新口径过期**：README 写「开发运行默认不装载自更新栈，此路径无动作」，暗示点「检查更新」会有静默失效的点击。实际形态是：dev 下托盘菜单**不出现**该项（`TrayController` 按 `_updateMachine is not null` 决定），桌面设置页的更新块显示「当前运行时不可用」并区分「无自更新栈」与「命令通道断」（插件侧 `client.js` 的 `nostack`/`channel` 分流，有 `client.test.js` 钉住）。文档落后于产品，读者会去点一个不存在的入口。

## Decision

- `scripts/package-macos.sh`：staging 用 `sips -s format icns` 从 `assets/icon.png` 生成 `Contents/Resources/icon.icns`，`Info.plist` 增 `CFBundleIconFile` = `icon.icns`；缺 `sips` 即 `die`（`--stage-only` 同样需要）。
- `scripts/verify-package-layout.sh`：三平台同一条判据（原生库在内容根或 `runtimes/<rid>/native/` 至少一处命中、该目录存在；rid 通配 `linux-*`/`osx-*`/`win-*`），按平台表列出库名与独立面（Windows 壳托管程序集/`wwwroot`，macOS `Info.plist` 的 `CFBundleIconFile` 与 `Resources/icon.icns`）。断言清单与探测面契约的单一事实源是 [docs/testing.md](../../../../docs/testing.md)；夹具按同一张表造双份原生库与 macOS `Contents/`。
- `scripts/dev-sign.sh` 增 `--self-test`：临时目录注入 `codesign`/`signtool`/`pwsh` 桩（记录 argv 到调用日志），断言 macOS 的 ad-hoc 与显式身份两条路径（含 `codesign --verify` 的第二次调用）、Windows 的 `signtool sign /fd SHA256 /s My /n "DeepSeek Harness Desktop Dev"`、证书创建走 pwsh、桩失败时脚本非零、缺参数/缺目标/未知平台的用法错误。
- `scripts/smoke-linux-rpm.sh` 增 `--self-test`：注入 `docker` 桩记录 argv，断言 `docker run --rm` 的四条挂载、`-e` 传递面、`fedora:44` 镜像与 `/smoke-inner.sh` 入口、容器非零退出码原样透传并留痕；自测入口放在位置参数校验之前（否则 `--self-test` 会被当成 rpm 路径）。
- `ci.yml` 的「脚本自测」步补 `dev-sign.sh --self-test` 与 `smoke-linux-rpm.sh --self-test`——自测写了不接线等于没写（同 [delivery-chain-gaps](2026-10-20-delivery-chain-gaps.md) 的接线教训）。
- README 中英双语改写 dev 口径：设置页明确提示「当前运行时不可用」，托盘菜单不出现「检查更新」项，`DSH_DESKTOP_UPDATE_FORCE=1` 可显式装载。代码路径不动——提示通道已存在，缺的只是文档对齐。

## Alternatives considered

- **把 icon.png 直接复制进 `Contents/Resources/` 并在 plist 里写 png**：落败——`CFBundleIconFile` 的常规消费面是 `.icns`，写 png 依赖未文档化的回退行为；生成 icns 只多一条 `sips` 调用。
- **iconutil 从 iconset 生成多分辨率 icns**：落败——需要先按尺寸切出 `icon_16x16.png` … `icon_512x512@2x.png` 共十件并组织 `.iconset`，增一条固定数量的中间产物链；当前 `assets/icon.png` 是 512×512 单档，`sips` 直接产出单分辨率 icns 已满足「Dock/Finder 有本应用图标」，多分辨率是后续打磨项（`assets/icons/` 的 hicolor 档不适用于 macOS）。
- **三平台布局断言统一成「根上必须有原生库」**：落败——publish 直出同时存在根与 `runtimes/<rid>/native`，但打包脚本若只保留一份，判据须认两个探测面（[delivery-chain-gaps](2026-10-20-delivery-chain-gaps.md) 及 Windows 分支既有形态）；收紧成「根必须有」会把合法的 runtimes-only 布局判红。
- **自测里跑真 `codesign`/`signtool`/`docker`**：落败——三者在 Linux CI runner 上都不可用（macOS 的 `codesign` 需真钥匙串，`docker` 需拉 fedora 镜像），自测目标恰是「命令面形状」，桩足够；真调用由打包含冒烟腿覆盖。
- **dev 下补注册自更新路由（L11 的另一半）**：落败——dev 版本号等于 csproj 版本，注册路由会让「检查更新」对着同版本 compare 出无意义结论，且托盘菜单与设置页都已经把「不可用」表达清楚；补注册是把开发态的降级换成噪音。

## Consequences

- 收益：macOS 包带上应用图标；Linux/macOS 包缺原生库或 runtimes 在打包腿即红；`codesign`/`signtool`/`docker run` 的参数面进入离线回归（每次 push 执行）；README 的 dev 口径与产品一致。
- 代价：`package-macos.sh` 增加了对 `sips` 的硬依赖——非 macOS 主机无法 staging macOS 包（本来就只能在 macOS 上产出 dmg，属既有约束的显式化）；布局脚本到 248 行，接近 S2 的 250 行上限，后续再加断言须先拆文件。
- 未证项：本机无 macOS，`sips -s format icns` 产出的 icns 结构与 `CFBundleIconFile` 读取均未实跑【推断 · 未证】，首个执行点是 dispatch 后的 macOS 打包腿（离线夹具只钉判据逻辑）；真实 `.app` 的 Dock 观感属发版冒烟的人工确认项。

## Testing

- `bash scripts/verify-package-layout.sh --self-test`：全 ok（含 linux 缺原生库/缺 runtimes、仅 runtimes 面放行、macos 缺图标/缺 plist/值不符/缺原生库/缺 runtimes、`--platform` 缺失 rc=2）；正负例成对覆盖两个探测面与 plist 值。
- `bash scripts/dev-sign.sh --self-test` 13 条、`bash scripts/smoke-linux-rpm.sh --self-test` 12 条全 ok；`bash scripts/verify-shell-standards.sh`（S1–S6，含 shellcheck 0.11.0 `-S warning`）与 `actionlint -shellcheck="shellcheck -S warning"` 全绿。
- 未实跑项：`package-macos.sh` 的 icns 生成与 dmg 产出（需 macOS）；真 `codesign`/`signtool` 由打包腿 `SELF_SIGN=1` 覆盖。

## Related

- [delivery-chain-gaps](2026-10-20-delivery-chain-gaps.md)——上一篇补洞，本篇补其布局与自测面。
- [artifact-verification-chain](../process/2026-08-24-artifact-verification-chain.md) 与 [windows-packaging-parity](../bug-fix/2026-09-27-windows-packaging-parity.md)——③主程序/④原生库判据的出处与事故动因。
- [free-self-sign-dev](../process/2026-08-20-free-self-sign-dev.md)（自签设计）与 [companion-client-js-tests](2026-10-21-companion-client-js-tests.md)（插件 `nostack`/`channel` 分流用例）。
