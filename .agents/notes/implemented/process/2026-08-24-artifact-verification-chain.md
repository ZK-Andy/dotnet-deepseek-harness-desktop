# Agent Note: artifact-verification-chain

Status: implemented

## Problem

批次一产物验证链缺失：v0.2.0/v0.2.1 两起发布事故（闭包缓存陈旧带出旧 tgz、供应链政策静默拦截补装）均属「构建时可知、运行时才暴露」的类别，当前防线只有 Linux 构建期的 `dsh web:` 单点自检，且三平台产物从打包到 Release 发布之间没有任何内容级复核——假包/缺件/错版一旦溜过平台 job，就直达用户。台账四项：①一方闭包图静态校验 ②Linux 包安装冒烟 ③Win/mac 包内容布局断言 ④release preflight 总检位。

## Decision

1. **闭包图静态校验**（`scripts/verify-closure-graph.mjs`）：从 `node_modules/@deepseek-ai/` 直装入口出发，沿生产依赖面（dependencies + 非 optional peerDependencies）BFS，凡一方传递依赖在闭包中不可解析即 fail loud；接入 `bundle-runtime-ci.sh` 裁剪后、自检前。解析模型对齐 Node 运行时——每个包先 realpath 再从**引用方真实路径**逐级向上找 node_modules（pnpm 布局下依赖住 `.pnpm` 虚拟存储，顶层提升目录只有直装入口，按「只查顶层」建模会把健康闭包误报成全图缺件——本地实测踩到并修正）。只遍历一方边：第三方闭包完整性由 pnpm lockfile 与既有 `dsh web:` 自检把关。
2. **Linux 安装冒烟**（`scripts/smoke-install-linux.sh`）：deb 在 runner 原生安装（apt 解依赖）、rpm 在 fedora 容器内安装（dnf 解依赖），随后启动已安装的二进制，90s 内捕获 `[host] dsh web:` 行判 PASS——该行打印于窗口创建之前，无需 display 即可验证「包装得上、依赖齐、运行时定位成功、dsh 起得来」。两架构 matrix 各自原生执行；进程探活用 `kill -0 <pid>` 而非 pgrep（安装后入口是小写符号链接，按大写二进制名 pgrep 会立即误判进程已死）。
3. **包内容布局断言**（`scripts/verify-package-layout.sh`）：对包内容根断言 node 可执行、dsh 入口存在、两个随包 tgz 存在且过名称/体积关、**tgz 与源闭包逐字节一致**（cmp）。macOS 在 workflow 挂载最终 dmg 后对 `.app/Contents/Resources` 断言（`--rt runtime`：.app 布局下 runtime 直接位于 Resources 下，与 win/linux 的 `resources/runtime` 嵌套不同，参数化而非靠文件系统大小写折叠碰巧命中）；Windows 在打包脚本内对 staging 断言（staging 即 Inno [Files] 唯一内容源）；Linux 由安装冒烟覆盖更深层。
4. **release preflight 总检位**（`scripts/release-preflight.sh`）：release.yml 在合并 SHA256SUMS 之后、生成正文之前执行——资产矩阵完备（deb×2/rpm×2/dmg×2/exe×1/SUMS×1 命名模式精确匹配 + 矩阵外产物报错）、单资产体积下限 50MB、SHA256SUMS 全量复核，任一失败终止发布。

## Alternatives considered

- **布局断言复用 `.bundle-meta.json` 的 companionSha256 四元组**（台账原案）：落败——该哈希是插件**源码树**哈希（供缓存签名），与 tgz 字节无对应关系；改为「包内 tgz vs 源闭包 tgz 逐字节 cmp」，更强（覆盖 dshmarket 与 companion 双包）且无需扩展 meta 格式、无旧缓存兼容负担。
- **Windows 断言解包最终 exe**：落败——Inno Setup 安装器无 CI 可用的解包工具（innounp 需额外下载不受信二进制）；staging 是安装器内容的唯一来源（[Files] Source: staging\*），对 staging 断言等价且零新增依赖。macOS 则坚持挂载真实 dmg 断言（hdiutil 原生可用，覆盖打包环节本身）。
- **preflight 在各平台 job 内分散做矩阵检查**：落败——跨 run 产物聚合只有 release.yml 能看到全集，分散检查无法发现「某平台产物整体缺席」；preflight 是唯一拥有完整清单的位置，与平台内深度断言互补而非重复。
- **闭包图校验覆盖第三方依赖**：落败——第三方树数千节点的完整性由 pnpm 安装原子性与 `dsh web:` 强自检保证；全图 BFS 引入误报面（optional/peer 语义各家不一）却拦不住新事故类，收益不成比例。

## Consequences

- 验证链四层各守一段：构建期闭包图（缺件）→ 平台 job 布局断言/安装冒烟（包内容与可安装性）→ release preflight（全集完备性与校验和）；任一红先修再发，不做人工绕过。
- 冒烟判定信号 `[host] dsh web:` 位于窗口创建之前：能拦「装不上/起不来/闭包坏」，拦不住纯渲染问题（后者本就需真人验收，不在自动化边界内）。
- rpm 容器冒烟依赖 GitHub runner 预装 docker 与 fedora 镜像拉取；镜像拉取失败会误报为冒烟失败——脚本区分「容器起不来」与「冒烟不过」两类日志便于归因。
- preflight 50MB 体积下限是经验值：若未来瘦身后正常包逼近下限会造成误杀，届时随瘦身同 PR 调整（坑位已记入 release-flow 卡）。
- 闭包缓存命中路径不重跑图校验：签名未变即内容未变，上次构建时已校验；签名变更触发全量重建时校验随之重跑。
- Linux 平台 job 预计增加约 3 分钟（apt/dnf 安装 WebKitGTK 依赖 + 90s 冒烟窗口）；mac/win 断言为秒级。
