#!/usr/bin/env bash
# smoke-install-macos.sh — macOS 安装冒烟（ADR artifact-verification-chain 平台补齐）。
# 对 dmg 做「挂载 → 安装（拷入 /Applications）→ 启动 → 等判定信号」验证，补齐 mac
# 平台此前只有 dmg 挂载布局断言、无「装得上、起得来」覆盖的缺口。
#
# 判定信号与 Linux/Windows 冒烟同款双信号：
#   ①`[host] dsh web =` = 全链 PASS（装包→首启引导→dsh web 就绪）；
#   ②`[bootstrap] 引导开始：` = 安装链保底。
# 等待语义（ADR smoke-wait-full-after-boot）：②命中后不收工，继续等①至
# 超时或进程退出；超时仍只有②按安装链 PASS，进程退出按退出时最佳信号收工。
# mac runner 有 WindowServer 会话，①应命中；若 WKWebView/WindowServer 在 runner
# 会话受限使壳提前退出，②为保底判定位（已记录边界，同 Linux CI）。
#
# 信号源 = <DSH_HOME>/logs/host.log（HostLog 双写 stdout 与该文件；unix 形态 stdout
# 重定向同样捕获，双源并查）。
#
# Gatekeeper 边界：dmg 在同 runner 上生成、hdiutil 挂载不经下载 quarantine，
# 未签名二进制本地直跑不触发 Gatekeeper（该链路只保真 CI；终端用户侧 Gatekeeper
# 告警是既有的签名挂账，与本冒烟无关）。
#
# 用法: smoke-install-macos.sh <dmg>
set -euo pipefail

DMG="${1:?usage: smoke-install-macos.sh <dmg>}"
[[ -f "$DMG" ]] || { echo "error: dmg 不存在: $DMG" >&2; exit 1; }
DMG="$(realpath "$DMG")"
APP_BUNDLE="DeepSeek.Harness.Desktop.app"
APP_NAME="DeepSeek.Harness.Desktop"

SMOKE_WAIT="${SMOKE_WAIT_SECONDS:-1320}"
FULL_RE='\[host\] dsh web ='
BOOT_RE='\[bootstrap\] 引导开始：'
PASS_RE="$FULL_RE|$BOOT_RE"

# 判定结论（ADR smoke-runner-deepening）：命中 ① 全链还是 ② 安装链必须打印成结论，
# 不能靠翻日志。arch 自带出处（x64 leg 跑在 ARM runner = Rosetta 下验证）。
smoke_verdict() { # $1=stdout $2=host.log
  if grep -qE '\[host\] dsh web =' "$1" 2>/dev/null || { [[ -f "$2" ]] && grep -qE '\[host\] dsh web =' "$2"; }; then
    echo "SMOKE_VERDICT=full-chain（dsh web 就绪） arch=$(uname -m)"
  else
    echo "SMOKE_VERDICT=install-chain（仅引导启动） arch=$(uname -m)"
  fi
}

# 启动截图 best-effort（ADR smoke-runner-deepening）：供人眼复核，永不拦冒烟。
# 落盘目录由调用方经 SMOKE_SHOT_DIR 注入；未设（本地跑）即跳过。
smoke_shot() { # $1=文件名
  [[ -n "${SMOKE_SHOT_DIR:-}" ]] || return 0
  mkdir -p "$SMOKE_SHOT_DIR" 2>/dev/null || return 0
  screencapture -x -t png "$SMOKE_SHOT_DIR/$1" 2>/dev/null \
    || echo "note: 截图跳过（无 WindowServer 会话或 screencapture 不可用）" >&2
}

MNT="$(mktemp -d)/mnt"
HOME_DIR="$(mktemp -d)"
OUT="$(mktemp)"
INSTALLED="/Applications/$APP_BUNDLE"
SMOKE_PID=""
mkdir -p "$(dirname "$MNT")"

cleanup() {
  [[ -n "$SMOKE_PID" ]] && kill "$SMOKE_PID" 2>/dev/null || true
  hdiutil detach "$MNT" >/dev/null 2>&1 || true
  rm -rf "$INSTALLED" "$HOME_DIR" "$OUT" 2>/dev/null || true
}
trap cleanup EXIT

echo "== 挂载 $DMG"
hdiutil attach "$DMG" -mountpoint "$MNT" -nobrowse -readonly
[[ -d "$MNT/$APP_BUNDLE" ]] || { echo "error: dmg 内缺 $APP_BUNDLE" >&2; exit 1; }

echo "== 安装（拷入 /Applications）"
sudo cp -R "$MNT/$APP_BUNDLE" /Applications/
hdiutil detach "$MNT"
[[ -f "$INSTALLED/Contents/MacOS/$APP_NAME" ]] || { echo "error: 安装后缺主二进制" >&2; exit 1; }

echo "== 启动冒烟（等①全链，②保底；②命中后继续等①至超时/退出，窗=${SMOKE_WAIT}s）"
set +e
env DSH_DESKTOP_DSH_HOME="$HOME_DIR" DEEPSEEK_API_KEY=placeholder \
  "$INSTALLED/Contents/MacOS/$APP_NAME" >"$OUT" 2>&1 &
SMOKE_PID=$!
rc=1
boot_seen=0
SECONDS=0
LOG="$HOME_DIR/logs/host.log"
log_has() { # $1=正则：stdout 或 host.log 任一命中
  grep -qE "$1" "$OUT" 2>/dev/null || { [[ -f "$LOG" ]] && grep -qE "$1" "$LOG"; }
}
for _ in $(seq 1 "$SMOKE_WAIT"); do
  if log_has "$FULL_RE"; then
    grep -m1 -E "$FULL_RE" "$OUT" 2>/dev/null || grep -m1 -E "$FULL_RE" "$LOG"
    rc=0
    smoke_verdict "$OUT" "$LOG"
    smoke_shot "smoke-macos.png"
    # PASS 也打印壳输出尾部：壳何时/为何退出（如窗口创建即退出）需要证据在案
    echo "--- 壳输出尾部（PASS 证据）---" >&2
    tail -5 "$OUT" >&2 || true
    break
  fi
  if [[ $boot_seen -eq 0 ]] && log_has "$BOOT_RE"; then
    boot_seen=1
    grep -m1 -E "$BOOT_RE" "$OUT" 2>/dev/null || grep -m1 -E "$BOOT_RE" "$LOG"
    echo "note: 已见②安装链（${SECONDS}s），继续等①至超时/退出…" >&2
  fi
  if ! kill -0 "$SMOKE_PID" 2>/dev/null; then
    # 进程已退出：补扫一次（信号可能刚好落在退出前），按最佳信号收工
    if log_has "$FULL_RE"; then
      grep -m1 -E "$FULL_RE" "$OUT" 2>/dev/null || grep -m1 -E "$FULL_RE" "$LOG"
      rc=0
      smoke_verdict "$OUT" "$LOG"
      smoke_shot "smoke-macos.png"
      echo "--- 壳输出尾部（PASS 证据）---" >&2
      tail -5 "$OUT" >&2 || true
    elif log_has "$BOOT_RE"; then
      echo "note: 进程已退出，未见①，按②安装链收工" >&2
      rc=0
      smoke_verdict "$OUT" "$LOG"
      smoke_shot "smoke-macos.png"
      echo "--- 壳输出尾部（PASS 证据）---" >&2
      tail -5 "$OUT" >&2 || true
    fi
    break
  fi
  sleep 1
done
# 超时仍只有②：按安装链 PASS（等满窗语义），而非失败
if [[ $rc -ne 0 ]] && log_has "$BOOT_RE"; then
  echo "note: ${SMOKE_WAIT}s 内未见①，按②安装链收工" >&2
  rc=0
  smoke_verdict "$OUT" "$LOG"
  smoke_shot "smoke-macos.png"
  echo "--- 壳输出尾部（PASS 证据）---" >&2
  tail -5 "$OUT" >&2 || true
fi
set -e
if [[ $rc -ne 0 ]]; then
  echo "error: [mac] 冒烟失败——${SMOKE_WAIT}s 内未出现 dsh web URL 或引导启动行。stdout 尾部：" >&2
  tail -30 "$OUT" >&2 || true
  if [[ -f "$LOG" ]]; then
    echo "--- host.log 尾部 ---" >&2
    tail -30 "$LOG" >&2
  fi
  smoke_shot "smoke-macos-fail.png"
fi
exit $rc
