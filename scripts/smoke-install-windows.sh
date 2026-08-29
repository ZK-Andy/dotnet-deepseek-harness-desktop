#!/usr/bin/env bash
# smoke-install-windows.sh — Windows 安装冒烟（ADR artifact-verification-chain 平台补齐）。
# 对 Inno Setup 安装器做「静默安装 → 启动 → 等判定信号」验证，补齐 win 平台此前
# 只有 staging 布局断言、无「装得上、起得来」覆盖的缺口（libadwaita 事件同类的
# 缺依赖/起不来事故在 win 的对应面：WebView2 runtime、VC++ 运行库、原生 DLL）。
#
# 判定信号与 Linux 冒烟同款双信号（命中其一即 PASS）：
#   ①`[host] dsh web =` = 全链 PASS（装包→首启引导→dsh web 就绪）；
#   ②`[bootstrap] 引导开始：` = 安装链 PASS。
# Windows runner 有交互式桌面会话 + 预装 WebView2 + 网络，①应命中（比 Linux CI
# 更完整）；②为壳提前退出时的保底判定位。
#
# 信号源 = <DSH_HOME>/logs/host.log（HostLog 双写 stdout 与该文件）+ 启动器 stdout
# 捕获（工程为 Exe 控制台子系统，重定向通常可达；host.log 为权威源）。
#
# 等待窗与引导步超时强耦合（同 smoke-install-linux.sh：步数×StepTimeoutMinutes+
# 余量），SMOKE_WAIT_SECONDS 可覆写。Windows runner 多有预装 node（复用免下载），
# 全链耗时可观仍在窗内。
#
# 用法: smoke-install-windows.sh <setup.exe>
set -euo pipefail

SETUP="${1:?usage: smoke-install-windows.sh <setup.exe>}"
[[ -f "$SETUP" ]] || { echo "error: 安装器不存在: $SETUP" >&2; exit 1; }
SETUP="$(realpath "$SETUP")"
APP_NAME="DeepSeek.Harness.Desktop"

# Git Bash 会把 /VERYSILENT 这类开关当 POSIX 路径转换（cookbook [脚本]；robocopy
# 同款先例），MSYS2_ARG_CONV_EXCL 排除。
export MSYS2_ARG_CONV_EXCL='*'

SMOKE_WAIT="${SMOKE_WAIT_SECONDS:-1320}"
PASS_RE='\[host\] dsh web =|\[bootstrap\] 引导开始：'

INSTALL_DIR="$(mktemp -d)/app"
HOME_DIR="$(mktemp -d)"
OUT="$(mktemp)"
WIN_DIR="$(cygpath -w "$INSTALL_DIR" 2>/dev/null || echo "$INSTALL_DIR")"
APP_EXE="$INSTALL_DIR/$APP_NAME.exe"

cleanup() {
  powershell -NoProfile -Command "Stop-Process -Name '$APP_NAME' -Force -ErrorAction SilentlyContinue" >/dev/null 2>&1 || true
  rm -rf "$OUT"
}
trap cleanup EXIT

echo "== 安装（静默，DIR=$WIN_DIR）"
"$SETUP" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="$WIN_DIR"
if [[ ! -f "$APP_EXE" ]]; then
  echo "error: 安装后缺 $APP_NAME.exe（DIR=$INSTALL_DIR）" >&2
  exit 1
fi
echo "== 启动冒烟（等 dsh web URL 行或引导启动行，窗=${SMOKE_WAIT}s）"
set +e
env DSH_DESKTOP_DSH_HOME="$HOME_DIR" DEEPSEEK_API_KEY=placeholder \
  "$APP_EXE" >"$OUT" 2>&1 &
pid=$!
rc=1
LOG="$HOME_DIR/logs/host.log"
for _ in $(seq 1 "$SMOKE_WAIT"); do
  if grep -qE "$PASS_RE" "$OUT" 2>/dev/null || { [[ -f "$LOG" ]] && grep -qE "$PASS_RE" "$LOG"; }; then
    grep -m1 -E "$PASS_RE" "$OUT" 2>/dev/null || grep -m1 -E "$PASS_RE" "$LOG"
    rc=0
    break
  fi
  if ! kill -0 "$pid" 2>/dev/null; then
    # 进程已退出：补扫一次（信号可能刚好落在退出前），仍无即 fail fast
    grep -qE "$PASS_RE" "$OUT" 2>/dev/null && rc=0
    break
  fi
  sleep 1
done
set -e
if [[ $rc -ne 0 ]]; then
  echo "error: [win] 冒烟失败——${SMOKE_WAIT}s 内未出现 dsh web URL 或引导启动行。stdout 尾部：" >&2
  tail -30 "$OUT" >&2 || true
  if [[ -f "$LOG" ]]; then
    echo "--- host.log 尾部 ---" >&2
    tail -30 "$LOG" >&2
  fi
fi
kill "$pid" 2>/dev/null || true
wait "$pid" 2>/dev/null || true
exit $rc
