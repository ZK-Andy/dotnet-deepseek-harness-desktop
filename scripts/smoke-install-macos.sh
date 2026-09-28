#!/usr/bin/env bash
# smoke-install-macos.sh — macOS 安装冒烟（ADR artifact-verification-chain 平台补齐）。
# 对 dmg 做「挂载 → 安装（拷入 /Applications）→ 启动 → 等判定信号」验证，补齐 mac
# 平台此前只有 dmg 挂载布局断言、无「装得上、起得来」覆盖的缺口。
#
# 判定信号与 Linux/Windows 冒烟同款双信号：
#   ①`[host] dsh web =` = dsh 就绪（传输层）；
#   ②`[bootstrap] 引导开始：` = 安装链保底。
# 等待/落定/心跳/看门狗/回退门语义在 scripts/lib/smoke-wait-lib.sh（唯一家，三平台同一实现）；
# 裁决门（auth 硬拦）、存活门、像素见证、证据打印在 scripts/lib/smoke-verdict-lib.sh。
# 导航到达只作诊断回显，不判门（holder 自 reload 不产生到达回调；token 第二跳已随转发模型退役）。
# mac runner 有 WindowServer 会话，①应命中；若 WKWebView/WindowServer 在 runner
# 会话受限使壳提前退出（①前），②为保底判定位（已记录边界，同 Linux CI）。
# verdict 观测 + 截图门（ADR macos-cookie-grace-reload）：截图等 verdict 行静默后再拍
# （应用侧 grace 重载可能改写终页），到达后拍截图内容见证（与 Linux 同阈值，中央裁剪避菜单栏/Dock）。
#
# 信号源 = <DSH_HOME>/logs/host.log（HostLog 双写 stdout 与该文件；unix 形态 stdout
# 重定向同样捕获，双源并查，去重防双计）。
#
# Gatekeeper 边界：dmg 在同 runner 上生成、hdiutil 挂载不经下载 quarantine，
# 未签名二进制本地直跑不触发 Gatekeeper（该链路只保真 CI；终端用户侧 Gatekeeper
# 告警是既有的签名挂账，与本冒烟无关）。
#
# 用法: smoke-install-macos.sh <dmg>
# 自测: smoke-install-macos.sh --self-test（纯函数回归，不碰安装器）
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/smoke-verdict-lib.sh
source "$SCRIPT_DIR/lib/smoke-verdict-lib.sh"
# shellcheck source=lib/smoke-selftest-verdict.sh
source "$SCRIPT_DIR/lib/smoke-selftest-verdict.sh"

SELFTEST=0
if [[ "${1:-}" == "--self-test" ]]; then SELFTEST=1; fi
if [[ "$SELFTEST" -eq 0 ]]; then
  DMG="${1:?usage: smoke-install-macos.sh <dmg>}"
  [[ -f "$DMG" ]] || die "dmg 不存在: $DMG"
  DMG="$(realpath "$DMG")"
fi
APP_BUNDLE="DeepSeek.Harness.Desktop.app"
APP_NAME="DeepSeek.Harness.Desktop"

# 等待窗/落定窗（含覆写旋钮）与判定串 ①②的唯一家在共享库，此处只做一次解析。
smoke_resolve_windows
# 裁决静默等待预算（秒，只定截图时机不判门）：探针 + grace 重载 + 再探针全程可超 100s，
# 取 150s 有界。SMOKE_VERDICT_SECONDS 可覆写。
SMOKE_VERDICT_SECONDS="${SMOKE_VERDICT_SECONDS:-150}"
# 裁决后重绘窗（秒）：提交回调早于新页出像素，立刻拍易拍到上一跳旧帧（Linux 同款）；非数字按默认。
# （存活门落地后启动判定不再依赖截图时序，回退 3s——禁祈祷式加时。）SMOKE_REPAINT_SECONDS 可覆写。
SMOKE_REPAINT_SECONDS="${SMOKE_REPAINT_SECONDS:-3}"
[[ "$SMOKE_REPAINT_SECONDS" =~ ^[0-9]+$ ]] || SMOKE_REPAINT_SECONDS=3

# 启动截图 best-effort（ADR smoke-runner-deepening）：供人眼复核，永不拦冒烟。
# 落盘目录由调用方经 SMOKE_SHOT_DIR 注入；未设（本地跑）即跳过。
smoke_shot() { # $1=文件名
  [[ -n "${SMOKE_SHOT_DIR:-}" ]] || return 0
  mkdir -p "$SMOKE_SHOT_DIR" 2>/dev/null || return 0
  screencapture -x -t png "$SMOKE_SHOT_DIR/$1" 2>/dev/null \
    || log "截图跳过（无 WindowServer 会话或 screencapture 不可用）"
}

# mac 专属夹具：裁决静默等待的事件驱动侧（增长后稳定才返、grace 触发重置静默）
# 与中央裁剪几何（含菜单栏/Dock 的全屏图取中央）。
smoke_selftest_platform() { # $1=夹具根
  local tdir="$1"
  # mac 全屏截图含菜单栏/Dock，取中央裁剪（与 Linux 默认几何不同的唯一一处）
  if command -v convert >/dev/null 2>&1; then
    convert -size 1024x768 xc:white "$tdir/m_white.png" 2>/dev/null
    convert -size 1024x768 xc:"#cccccc" -fill "#222222" -draw "rectangle 100,84 400,684" "$tdir/m_ui.png" 2>/dev/null
    smoke_capture_witness "$tdir/m_white.png" "800x600+0+0" "center" >/dev/null 2>&1 && tfail "mac-witness-blank-should-fail" || tpass "mac-witness-blank-fails"
    smoke_capture_witness "$tdir/m_ui.png" "800x600+0+0" "center" >/dev/null 2>&1 && tpass "mac-witness-ui-passes" || tfail "mac-witness-ui-fails"
  else
    log "skip: 无 convert，跳过 mac 截图内容见证夹具"
  fi
}

if [[ "$SELFTEST" -eq 1 ]]; then
  smoke_self_test_run smoke_selftest_platform
  exit $?
fi

MNT="$(mktemp -d)/mnt"
HOME_DIR="$(mktemp -d)"
OUT="$(mktemp)"
INSTALLED="/Applications/$APP_BUNDLE"
SMOKE_PID=""

cleanup() {
  [[ -n "$SMOKE_PID" ]] && kill "$SMOKE_PID" 2>/dev/null || true
  hdiutil detach "$MNT" >/dev/null 2>&1 || true
  # MNT 的 mktemp -d 父目录也要收（只 rm -rf "$MNT" 会留下空父目录）
  rm -rf "$INSTALLED" "$HOME_DIR" "$OUT" "$(dirname "$MNT")" 2>/dev/null || true
}
trap cleanup EXIT

echo "== 挂载 $DMG"
hdiutil attach "$DMG" -mountpoint "$MNT" -nobrowse -readonly
[[ -d "$MNT/$APP_BUNDLE" ]] || die "dmg 内缺 $APP_BUNDLE"

echo "== 安装（拷入 /Applications）"
sudo cp -R "$MNT/$APP_BUNDLE" /Applications/
hdiutil detach "$MNT"
[[ -f "$INSTALLED/Contents/MacOS/$APP_NAME" ]] || die "安装后缺主二进制"

echo "== 启动冒烟（等①就绪后等导航落定，②保底；窗=${SMOKE_WAIT}s/落定${SETTLE_WAIT}s）"
LOG="$HOME_DIR/logs/host.log"
set +e
smoke_unattended_env "$HOME_DIR"
"$INSTALLED/Contents/MacOS/$APP_NAME" >"$OUT" 2>&1 &
SMOKE_PID=$!
rc=1
if smoke_wait_ready "$OUT" "$LOG" "$SMOKE_PID" "$HOME_DIR"; then
  rc=0
  # 静默等待只为「①已落定且进程还在写终页」这一形态服务：②安装链收工（①未现）与进程已死
  # 都跳过，免得空裁决面白等满预算（R2 S3）。
  if log_has "$FULL_RE" && kill -0 "$SMOKE_PID" 2>/dev/null; then wait_verdict; fi
  smoke_evidence_pass "$OUT" "$LOG" " arch=$(uname -m)"
  if [[ -n "${SMOKE_SHOT_DIR:-}" ]]; then
    # 裁决尘埃落定后再等有界重绘窗：提交回调早于新页出像素，立刻拍易拍到上一跳旧帧；只拍才睡。
    sleep "$SMOKE_REPAINT_SECONDS"
    smoke_shot "smoke-macos.png"
    # 内容见证（与 Linux 同阈值，中央裁剪避菜单栏/Dock）：401 墙/引导页像素即红，截图缺失亦红。
    # 落定/裁决门：显示腿 mac 开门（ADR macos-cookie-grace-reload）——到达过不算绿。
    if smoke_capture_witness "$SMOKE_SHOT_DIR/smoke-macos.png" "800x600+0+0" "center"; then
      :
    elif smoke_client_alive "$LOG"; then
      log "像素偏白但客户端存活（代理 200 RPC/SSE ≥3，浅色主题像素不可分），按活判过"
    else
      rc=1
    fi
  fi
else
  rc=1
  smoke_shot "smoke-macos-fail.png"
  smoke_evidence_fail "$OUT" "$LOG" tail
fi
set -e
if [[ $rc -ne 0 ]]; then
  error "[mac] 冒烟失败（rc=${rc}：落定/见证任一环节红，见上文 error 行；非特指①缺失）"
  tail -30 "$OUT" >&2 || true
  smoke_shot "smoke-macos-fail.png"
fi
# 日志落盘（W2）：调用方经 SMOKE_LOG_DIR 注入稳定目录（与 SMOKE_SHOT_DIR 同模式），
# CI 传 artifact——host.log 只在文件里全，step 日志只有尾巴。
# 日志落盘（W2）：SMOKE_LOG_DIR 由调用方注入稳定目录，CI 传 artifact。
smoke_dump_logs "smoke-macos" "$OUT" "${LOG:-}"
exit $rc
