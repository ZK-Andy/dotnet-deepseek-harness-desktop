#!/usr/bin/env bash
# smoke-linux-rpm.sh — Linux rpm 冒烟容器腿（fedora 内 dnf 装包 → 启动 → 等判定信号）。
# 由 smoke-install-linux.sh 在产物目录含 *.rpm 时转调；宿主只起容器与挂载，容器内脚本
# 是 scripts/smoke-linux-rpm-inner.sh（单独文件→可 lint / 可评审，非 heredoc 字面块）。
# 容器内 root + 无 display：判定走双信号 + 行为落定（见 smoke-install-linux.sh 文件头），
# 引导启动行先于窗口创建输出，恒为②安装链收工（无 X 出不了截图，见证门不适用）。
#
# 用法: SMOKE_PKG_DIR=<产物目录> smoke-linux-rpm.sh <rpm 路径>
# 环境：SMOKE_WAIT_SECONDS / SMOKE_SETTLE_SECONDS 可覆写窗口；SMOKE_LOG_DIR 置时落盘日志。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/smoke-verdict-lib.sh
source "$SCRIPT_DIR/lib/smoke-verdict-lib.sh"
RPM_PATH="${1:?usage: SMOKE_PKG_DIR=<dir> smoke-linux-rpm.sh <rpm>}"
PKG_DIR="${SMOKE_PKG_DIR:?usage: SMOKE_PKG_DIR=<dir> smoke-linux-rpm.sh <rpm>}"
APP_BIN="/usr/bin/deepseek-harness-desktop"

# 窗口解析（覆写旋钮 → 库读的全局）与判定串 ①②的唯一家在共享库
smoke_resolve_windows
APP_TIMEOUT=$((SMOKE_WAIT + 20))
base="$(basename "$RPM_PATH")"

# W2 落盘：SMOKE_LOG_DIR 置时挂进容器供容器的 EXIT trap 落日志；未置则挂一次性 tmp（跑后清掉）。
rpm_log_host=""
if [[ -n "${SMOKE_LOG_DIR:-}" ]]; then
  rpm_log_host="$SMOKE_LOG_DIR"
else
  rpm_log_host="$(mktemp -d)"
fi
mkdir -p "$rpm_log_host" 2>/dev/null || true

# 无人值守三元组里的两个常量项从宿主环境透传给容器（`-e VAR` 不带 =value = 取本进程同名变量），
# 值只在 smoke-verdict-lib.sh 写一处（ADR preinstall-unattended-skip）；容器自建 home，故不透传 home。
smoke_unattended_env

echo "== [rpm] fedora 容器安装冒烟: $base"
# `-e SMOKE_WAIT_SECONDS`/`-e SMOKE_SETTLE_SECONDS` 透传窗口旋钮；判据串①②不传——
# 容器内 source 同一份共享库即得（判据串曾因各处手抄错位致冒烟恒败）。
docker_rc=0
docker run --rm \
  -v "$PKG_DIR:/pkg:ro" \
  -v "$SCRIPT_DIR/lib:/smoke-lib:ro" \
  -v "$SCRIPT_DIR/smoke-linux-rpm-inner.sh:/smoke-inner.sh:ro" \
  -v "$rpm_log_host:/smokelogs:rw" \
  -e SMOKE_PKG_NAME="$base" \
  -e SMOKE_APP_BIN="$APP_BIN" \
  -e SMOKE_SETTLE_SECONDS="${SMOKE_SETTLE_SECONDS:-}" \
  -e SMOKE_WAIT_SECONDS="${SMOKE_WAIT_SECONDS:-}" \
  -e APP_TIMEOUT="$APP_TIMEOUT" \
  -e DEEPSEEK_API_KEY \
  -e DSH_DESKTOP_PREINSTALL_AUTO \
  fedora:44 bash /smoke-inner.sh || docker_rc=$?
# rc 传给调用方（调用方记数判红）；一次性 tmp 在两条路径上都回收。
# 非零留痕：docker 退出码是内外腿分界（inner 结论行在上文），哑巴 exit 让顶层
# 分不清容器内红还是 docker 本体红。
if [[ -z "${SMOKE_LOG_DIR:-}" ]]; then rm -rf "$rpm_log_host"; fi
if [[ "$docker_rc" -ne 0 ]]; then error "[rpm] 容器退出码 $docker_rc（inner 结论行见上文）"; fi
exit "$docker_rc"
