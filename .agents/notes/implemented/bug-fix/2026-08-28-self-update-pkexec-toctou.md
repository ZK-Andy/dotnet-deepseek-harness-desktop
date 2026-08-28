# Agent Note: 自更新 pkexec 脚本内联 + root 侧哈希复验——消除 user→root TOCTOU

Status: implemented

## Problem

Linux 自更新把安装脚本写到用户可写目录（`~/.dsh/updates/install.sh`）后交 `pkexec` 以 **root** 执行。dsh 生态里第三方插件/闭包代码以用户身份常驻——它可在用户点击授权与 `pkexec` 真正 exec 之间改写该脚本，把「更新本包」换成任意命令，形成 user→root 提权链：授权弹窗批准的是 `pkexec /bin/sh install.sh`，而脚本内容无任何完整性绑定（观察窗口长达 10s）。同链还有两个次生缺口：安装包本体在下载校验后长期位于用户可写路径（ready 可跨启动持久化，窗口分钟级到数天），装包时刻无复验；`pkexec env` 透传的用户 PATH（含 `~/.local/bin` 等用户可写目录）会被 root 侧 `/bin/sh` 用于命令解析，同名假 `sha256sum`/`dpkg` 即可架空任何校验。

## Decision

`UpdateInstaller` Linux 路径四重收口：

- **脚本内联**：安装脚本不再落盘，经 `pkexec env … /bin/sh -c '<script>'` 以 argv 传递——argv 在 `Process.Start` 后不可被用户级进程替换，root 不读用户可写文件，install.sh 形态废弃。
- **PATH 硬化**：脚本首行固定 `PATH=/usr/sbin:/usr/bin:/sbin:/bin`——用户 PATH 只保留在降权拉起的 `env`（二代实例运行环境）里，root 上下文的所有命令解析（`sha256sum`/`dpkg`/`rpm`/`getent`/`runuser`）只经系统路径，用户可写目录不参与。
- **root 侧哈希复验，锚点在 release 侧**：本进程与 ready 记录均在用户空间，任何落盘哈希都可被同权限改写——期望哈希在安装时点由 `InstallerDownloader.FetchSha256Async` 经 HTTPS 直达 release `SHA256SUMS.txt` 重取（repository 出自 root-owned 安装目录的 appsettings），冻结进脚本后由 root 在装包前 `sha256sum -c` 对照，不匹配即 `exit 1`。离线时复取失败拒装（状态机回 ready）：联网复核是安装的前置条件而非额外负担。
- **日志 symlink 守卫前置**：脚本在 `exec >> install.log` 重定向**之前** `[ -L ]` 检查并拒绝——重定向此刻即以 root 打开目标文件，守卫放在其后等于无效。检查到重定向间的窄窗（目录属主即用户本人，须赢下毫秒级竞态）为已接受的残余，完整性主依赖内联 argv 与 release 侧哈希两条防线。

边界：脚本内嵌的路径/环境值仍是用户可控字符串，但它们只被 root 用作「装这个已对照 release 哈希复验的包」与「拉起该包内的程序」——这正是用户授权的语义本身；PATH 硬化后 `sha256sum` 等命令名虽仍是字面量，但解析域已不含用户可写目录。

## Alternatives considered

- **root 拷贝脚本到受限路径后执行**：`pkexec install -m 700 <src> /run/...` 仍需 root 读取用户可写源文件，拷贝窗口同样可竞态，只是把窗口从「授权全程」缩到「拷贝瞬间」，未根治。
- **复用下载校验时算出的哈希（落盘或随 ready.json 持久化）**：落盘值与包体同在用户可写空间，攻击者可一并改写使复验空过；「装包前用户侧重算」也只覆盖重算之后的窗口，ready 跨启动持久化场景（分钟到数天）仍洞开。唯有安装时点重取 release 侧值才真正钉死内容。
- **包管理器签名验证（debsign/repo GPG）**：正解但需发版链路上游签名设施，超出桌面端单侧可收敛范围；release 侧 SHA256SUMS + HTTPS 达到等价强度。
- **root 日志改写 root-only 路径（/run、/var/log）**：彻底移除 symlink 面，但引入安装目录外的落盘依赖；守卫前置 + 内联 argv 为最小修正，残余窗已声明。

## Consequences

- 用户→root 的脚本换入链消除；装包内容绑定 release 侧哈希（安装时点联网复核，离线拒装回 ready）。
- 两类中止的观测时序不同：日志 symlink 守卫在脚本开头，pkexec 立即非零退出，被授权观察窗口捕获、状态机转 Error；哈希不匹配发生在等宿主退出**之后**——宿主已按「授权通过」正常退出，exit 1 无人观测，真相只在 install.log。错误文案统一为「授权被取消或失败（pkexec exit 1）」，不区分两类，属已接受的观测取舍。
- `install.sh` 不再落盘；`workDir`（updates 目录）承载 install.log 与已下载包本体（后者由哈希复验钉死）。

### Testing

`UpdateInstallerTests` 内容级回归：PATH 硬化行存在且先于任何外部命令；symlink 守卫先于 `exec >>` 重定向；哈希复验行存在且先于 `dpkg -i`、含 release 侧期望值与中止分支；脚本不再含 `install.sh` 落盘路径；既有 relay 链/转义/空值省略断言全保留。

### Related

- [2026-08-28-self-update-exit-reaps-dsh-child](2026-08-28-self-update-exit-reaps-dsh-child.md)：同一自更新链路的兄弟收口（退出收割）。
- [2026-08-22-self-update-review-hardening.md](2026-08-22-self-update-review-hardening.md)：下载期 SHA256SUMS 强校验（缺 SUMS 拒装）的出处；本决定把校验锚点延伸到安装时点。
