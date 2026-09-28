# Script Standards

> 适用范围：`scripts/**` 与 `.githooks/**`（shell）。条款的**可执行家**是
> [`verify-shell-standards.sh`](../scripts/verify-shell-standards.sh)（S1–S6）＋ CI 步骤；
> 本文件写「为什么这么定」与偏离记录，不重复判据实现。门禁总表见 [AGENTS.md](../AGENTS.md)。

## 基线

[Google Shell Style Guide](https://google.github.io/styleguide/shellguide.html)（bash 是唯一允许的
可执行脚本语言；2 空格缩进；`"${var}"` 引号纪律；错误一律 stderr；禁 `eval`/反引号，优先 `[[ ]]`；
函数 `lower_snake`；库文件 `.sh` 且不可执行）＋ [ShellCheck](https://www.shellcheck.net/)（`-S warning`）
＋ [actionlint](https://github.com/rhysd/actionlint)（对工作流 `run:` 块跑 shellcheck——一份工具同时覆盖
工作流与内嵌 shell）。

## 两处偏离（显式记录）

1. **命名保留 kebab-case**（Google 建议下划线）：脚本名已被 workflows / docs / hooks 引用，
   重命名是零功能收益 + 断链风险，故文档化偏离。
2. **尺寸闸取 250 行**（Google 说 >100 行就不该用 shell）：那条建议的信号是「该换工具」，
   而本仓体量下重写成本远大于收益——用**共享库 + 拆文件**解决（`smoke-install-linux.sh`
   503 行拆为入口 + rpm 腿 + 三个共享库；阈值见 `SIZE_LIMIT`）。超限即拆，**不迁语言**。

## 机器强制（`scripts/verify-shell-standards.sh`）

| 判据 | 条款 |
|---|---|
| **S1** | 入口脚本须 `set -euo pipefail`；缺省即 fail loud（错误不静默） |
| **S2** | 单文件 ≤250 行 |
| **S3** | 共享库（`scripts/lib/*.sh`）不得带可执行位、不得自设 shell 选项（会改调用方行为） |
| **S4** | 旋钮声明：`${NAME:-}` / `${NAME:?}` 读到的变量须在**本文件头部注释**、CI 工作流、或 `docs/` 里出现——「没人设也没人写」的旋钮即死旋钮，删或补声明 |
| **S5** | 共享库单源：`scripts/lib/*.sh` 定义的函数不得在别的脚本重定义（影子副本会与库分叉） |
| **S6** | `shellcheck -S warning` 零告警 |

**共享库纪律**：跨脚本复用的逻辑一律落 `scripts/lib/`（`common.sh` 通用助手、`packaging-common.sh`
三包脚本头部、`smoke-wait-lib.sh` / `smoke-verdict-lib.sh` / `smoke-selftest.sh` 冒烟面）；
入口脚本只留该脚本特有的编排。库文件被 `source` 后**不得**产生副作用，调用方契约（要预先设置的全局）
写在库文件头部。

**临时文件纪律**：临时文件一律 `mktemp`（禁固定 `/tmp` 名）+ `EXIT` trap 回收——
统一用 `common.sh` 的 `common_tmp_trap` / `common_tmp_dir` / `common_tmp_file`；平台清理不只
删临时文件时（如 mac 还要卸载挂载点、win 还要停进程、容器腿要拷日志）自写 trap 是正当形态，
但须同样保证退出路径回收。禁 `2>/dev/null || true` 吞关键步骤失败。

## 留评审（机器化不了，AI 兜底）

- 消息模板 `error: <对象>: <原因>（<修复>）`，级别前缀 `note:` / `warn:` / `error:`（`common.sh` 的
  `log`/`warn`/`error`/`die`）；
- 命名与写法：函数/变量 `snake_case`、条件用 `[[ ]]`、缩进 2 空格、文件头一行职责说明；
- 退出码语义：`0` 成功 / `1` 门禁或运行失败 / `2` 用法错误；
- 修外部工具的脆弱兜底（多路回退链、`|| true` 掩盖失败）：能收敛成单路 `fail loud` 就收敛。

## 工具版本（本地与 CI 同版）

| 工具 | 版本 | 装法 |
|---|---|---|
| shellcheck | 0.11.0 | `bash scripts/install-linters.sh`（钉版本 + sha256 校验，落到 `.cache/bin/`） |
| — | — | 门禁按「本仓 `.cache/bin/` → `PATH`」找 shellcheck：本地装进 `.cache/bin` 即与 CI 同版 |
| actionlint | 1.7.12 | 同上 |

版本必须同版：诊断集合随版本漂移，「本地绿 / CI 红」会直接废掉门禁的可信度。其它平台按上表版本号
用包管理器装同版本；`verify-shell-standards.sh` 依次从 `PATH`、`<repo>/.cache/bin/` 找 shellcheck，
都没有即**判红**（门禁不能悄悄不执行）。

## 豁免纪律

豁免一律**行内注释 + 理由**，可被评审读到：

- `# verify-shell-standards: no-errexit <理由>`——配 `set -uo pipefail` 用（如容器内脚本要显式
  打印安装失败诊断，`-e` 会让失败无声退出）；
- `# verify-shell-standards: allow-long <理由>`——尺寸闸豁免；
- `# shellcheck disable=SCxxxx # 理由`——shellcheck 单项豁免。
