# Agent Note: 交付链路补洞（Linux 载荷冒烟 + 发布总检自测 + CI 接线）

Status: implemented

Review: FULL/2026-10-20/R1=ok R2=ok R3=ok

## Problem

三处「CI 现在漏、发布才炸」的洞：

1. **Linux 腿无载荷冒烟探针**：`PayloadSmoke` 只在 mac/win 腿跑（`.github/workflows/package.yml`），Linux 包缺 native 库（`libsaucer*.so`/`libryn-pty.so`）不会被 CI 拦——只有装包冒烟与布局断言兜着，而它们断言的是文件与包元数据，不是 native 面能否加载。
2. **`scripts/release-preflight.sh` 无自测**：它是发布总检（资产矩阵完备 + 15MB 体积下限 + SHA256SUMS 复核），只在 tag 触发的 `release.yml` 里跑，判据从不在 PR 里执行；回归只能等发布当天炸。
3. **CI 接线漏面**：`ci.yml` 的 `paths-filter` 的 code 面不含 `tools/**`——只改 `PayloadSmoke` 的 PR 整条 build-test 跳过；`release-notes.sh --self-test` 与 `coverage-summary.py --self-test` 有自测却没被 CI 列出。

## Decision

- `package.yml` 的 `build-linux` 腿在 publish 之后加一步**载荷冒烟探针**：探针工程以产物同款 RID 自包含 publish 后对 publish 产物目录实跑，与 mac/win 腿同款判据；探针要真加载 `libsaucer`（它动态链接 WebKitGTK6/GTK4 运行时），故 Linux 腿在既有的「安装 rpmbuild 与冒烟显示栈」一步里同装该运行时依赖（包名与 `.deb` Depends 同源）。
- `release-preflight.sh` 增 `--self-test`：离线夹具用稀疏文件（`dd` 只落首字节，其余空洞）造出达线的七件套假资产与 `SHA256SUMS.txt`，逐条实跑本脚本（子进程）断言退出码与关键诊断——正例（矩阵齐即通过）加十个反例/边界用例（缺资产 / 体积不足 / 体积恰达下限 / 取整向上 / 校验和不一致 / 矩阵外意外资产 / 嵌套目录意外资产 / 同模式匹配到两个 / 缺校验和文件 / 资产目录不存在）。
- `ci.yml`：脚本自测步加 `release-preflight.sh --self-test` 与 `release-notes.sh --self-test`，Python 自测步加 `coverage-summary.py --self-test`；`paths-filter` 的 code 面补 `tools/**` 与两个进包图标资产（`assets/icon.png`、`assets/icon.ico`）。

## Alternatives considered

- **维持现状不修**（Linux 腿不接探针、发布总检不自测、CI 接线不动）：落败——三处都是「平时不红、出事才红」的洞：Linux 包缺 native 库要到用户首启才现形，发布总检判据的回归要等发布当天炸；成本只是几行接线与一个离线夹具。
- **探针对解包后的 deb/rpm 内容跑**：落败——探针的输入是 publish 产物（native 面），解包面与 publish 面不同源，多一层转换就多一个失真点，且解包内容不含 publish 的原生依赖布局。
- **探针放在打包装包之后**：落败——探针要验的是 publish 产物里的 native 面；装包后的内容已经被 `verify-package-layout.sh` 覆盖，两步重叠而信息量不增。
- **不预装运行时依赖，靠探针自己报缺库**：落败——runner 上缺 `libwebkitgtk-6.0.so.4` 会让「库没打进包」与「runner 缺依赖」两种真因混成一条红；显式装上与 `.deb` Depends 同款依赖后，红即指包本身。
- **自测用真实发布产物当夹具**：落败——发布产物只在 tag 腿产生，自测必须能离线跑；稀疏文件夹具近零占用（每件 4KB 实际块）且能精确造出「体积不足」「取整边界」这类形状。
- **只补 `paths-filter`、不补自测接线**：落败——那两个脚本的判据仍停在「发布才炸」，路径补齐只让更多 PR 跑 build-test，不增加这两道判据的执行。
- **把 `packaging/**` 也纳入 code 面**：落败——Inno 安装器模板的真实门是 docs job 无条件执行的 `package-windows.sh --self-test`（含模板渲染），纳入只让改模板的 PR 多跑三平台 build-test，不增加检出。同理 `assets/**` 只列参与构建/打包的两个图标，不列 README 图与 hicolor 图标档（后两者是文档面/打包腿输入，不进 build-test 判据面，列进来只增分钟不增检出）。

## Consequences

- Linux 包缺 native 库现在在打包腿就被拦（探针红），不必等装包冒烟或用户实机。
- 发布总检有离线回归锁：判据改动会在 PR 里被自测钉住，不再只由发布当天验证；自测覆盖到递归扫描面、体积下限取值与取整口径、两条此前从未执行的诊断分支。
- `paths-filter` 扩面后，只改 `tools/` 或两个进包图标的 PR 会跑完整的 build-test 三平台矩阵——CI 分钟数上升，这是「漏判即整条门禁跳过」的代价。
- 探针依赖 runner 能装上 `libwebkitgtk-6.0-4`【推断 · 未证】：依据是同 runner 的装包冒烟腿经 apt 解析同名 Depends（今天在跑），但未实跑 runner 侧 `apt-cache policy`；若 ubuntu-latest 将来换 SONAME，此步会红（红的是元数据，不是包）。

## Testing

- 载荷探针在 Linux 上实跑通过：本机 `dotnet publish -r linux-x64 --self-contained` 后跑 `PayloadSmoke artifacts/publish-linux-x64`，输出 `loaded: libsaucer.so / libsaucer-bindings.so / libsaucer-bindings-desktop.so / libryn-pty.so`、`decoded: icon.png`、`pty: payload-smoke-pty-ok`，`PASS`。
- 探针依赖链实测：本机 `ldd artifacts/publish-linux-x64/runtimes/linux-x64/native/libsaucer.so` 显示 `libwebkitgtk-6.0.so.4` 与 `libadwaita-1.so.0` 为其直接依赖（linux-x64 publish 产物；arm64 同集）。
- `release-preflight.sh --self-test` 11/11 绿；变异实测三处均判红——把递归扫描改成非递归 → 「嵌套目录意外资产判红」用例失配；体积取整改向下 → 「体积取整向上判过」用例失配；`FLOOR_MB` 由 15 改 16 → 「体积恰达下限判过」用例失配（确认夹具非空转）。
- `verify-shell-standards.sh`（S1–S6）绿；`actionlint -shellcheck="shellcheck -S warning"` 绿（两个工作流改动都过）。

## Related

- [payload-smoke-probe](../testing/2026-09-13-payload-smoke-probe.md)——探针本体与其 FFI/图像/PTY 用例设计；该篇「Linux 不接腿」的现役断言已由 2026-10-20 的 Erratum 换家。
- [artifact-verification-chain](../process/2026-08-24-artifact-verification-chain.md)——发布总检的资产矩阵与体积下限出处。
- [ci-gate-honesty](../process/2026-09-28-ci-gate-honesty.md)——`ci.yml` 的 code 面扩面同属该篇确立的「路径过滤不得漏判结构级输入」机制。
