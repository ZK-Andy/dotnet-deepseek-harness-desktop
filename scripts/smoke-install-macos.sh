#!/usr/bin/env bash
# smoke-install-macos.sh — macOS 安装冒烟（ADR artifact-verification-chain 平台补齐）。
# 对 dmg 做「挂载 → 安装（拷入 /Applications）→ 启动 → 等判定信号」验证，补齐 mac
# 平台此前只有 dmg 挂载布局断言、无「装得上、起得来」覆盖的缺口。
#
# 判定信号与 Linux/Windows 冒烟同款双信号：
#   ①`[host] dsh web =` = dsh 就绪（传输层）；
#   ②`[bootstrap] 引导开始：` = 安装链保底。
# 等待语义（ADR smoke-wait-full-after-boot）：②命中后不收工，继续等①至
# 超时或进程退出；超时仍只有②按安装链 PASS，进程退出按退出时最佳信号收工。
# 落定语义（ADR smoke-settle-content-verdict）：①只是 dsh 就绪行，verdict 与截图
# 必须等导航落定——`[nav] 导航已到达` 去重 ≥1 次且含 `?token=` 第二跳，或①之后到达 ≥1 次（单跳世界：壳直达；token 路径保留）。落定超时或
# 落定期进程退出即 FAIL（dsh 已就绪但 UI 未落定是真实事故，不再按 full-chain 放行）。
# mac runner 有 WindowServer 会话，①应命中；若 WKWebView/WindowServer 在 runner
# 会话受限使壳提前退出（①前），②为保底判定位（已记录边界，同 Linux CI）。
# verdict 观测 + 截图门（ADR macos-cookie-grace-reload）：应用侧 verdict/重进/探针行回显到 step 日志
# （token 脱敏），截图等 verdict 行数静默后再拍（应用侧 grace 重载可能改写终页）；
# 到达后拍截图内容见证（与 Linux 同阈值，中央裁剪避菜单栏/Dock）。判定逻辑见各函数。
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

SELFTEST=0
if [[ "${1:-}" == "--self-test" ]]; then SELFTEST=1; fi
if [[ "$SELFTEST" -eq 0 ]]; then
  DMG="${1:?usage: smoke-install-macos.sh <dmg>}"
  [[ -f "$DMG" ]] || { echo "error: dmg 不存在: $DMG" >&2; exit 1; }
  DMG="$(realpath "$DMG")"
fi
APP_BUNDLE="DeepSeek.Harness.Desktop.app"
APP_NAME="DeepSeek.Harness.Desktop"

# 等待窗 = 单轮尝试预算：RuntimeBootstrapOptions.StepTimeoutMinutes（默认 10 分钟）
# 单步上限 + 120s 余量 = 720s。重试轮不计入——冒烟只等首轮落定，超时按②收工。
# 引导步数或 StepTimeoutMinutes 变化时必须同批重算。SMOKE_WAIT_SECONDS 可覆写。
SMOKE_WAIT="${SMOKE_WAIT_SECONDS:-720}"
# 落定窗：①出现后等导航提交（commit 延迟毫秒级，90s 只防 runner 卡顿）。
SETTLE_WAIT="${SMOKE_SETTLE_SECONDS:-90}"
# 截图前等应用侧 verdict 行静默：探针 + grace 重载 + 再探针全程可超 100s，取 150s 有界（只定截图时机，不判门）。
VERDICT_WAIT="${SMOKE_VERDICT_SECONDS:-150}"
# 裁决后重绘窗（秒）：提交回调早于新页出像素，立刻拍易拍到上一跳旧帧（Linux 同款）；非数字按默认。
SMOKE_REPAINT_SECONDS="${SMOKE_REPAINT_SECONDS:-3}"
[[ "$SMOKE_REPAINT_SECONDS" =~ ^[0-9]+$ ]] || SMOKE_REPAINT_SECONDS=3
FULL_RE='\[host\] dsh web ='
BOOT_RE='\[bootstrap\] 引导开始：'
PASS_RE="$FULL_RE|$BOOT_RE"

# 落定等待共享库（NAV 正则 + log_has/nav/wait_settled/heartbeat/timeout_fallback）。
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "$SCRIPT_DIR/smoke-settle-lib.sh"

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

# 应用侧 verdict/重进/探针行回显到 step 日志（token 脱敏）：CI 日志直接可见终态是 healthy
# 还是 auth/unknown。只读观测，不判门。
echo_verdict_lines() {
  { grep -ahE '\[nav\] 页面裁决=|\[nav\] 检测到鉴权页|鉴权探针|终页非健康' "$OUT" 2>/dev/null; [[ -f "${LOG:-}" ]] && grep -ahE '\[nav\] 页面裁决=|\[nav\] 检测到鉴权页|鉴权探针|终页非健康' "$LOG" 2>/dev/null; true; } \
    | sed -E 's/token=[^& ]*/token=***/g' | sort -u >&2 || true
}

# verdict 静默等待：截图时机 aid，不判门（恒 0）。等裁决行出现后"裁决行数 + grace 触发行数"
# 双双 QUIET 秒不变即返；grace 触发（应用侧重载在途）即重置静默计数——文本相同的两条裁决行被
# sort -u 压成一行也误不了事。预算耗尽按现状截图。
# $1=总预算秒（默认 VERDICT_WAIT），$2=静默秒（默认 5）。
# 事件驱动（双计数静默），非固定睡眠——应用侧 1 条还是 2 条 verdict（grace 重载）都对齐终页。
wait_verdict() {
  local budget="${1:-$VERDICT_WAIT}" quiet="${2:-5}" i vlast=-1 glast=-1 vstable=0 n g
  for i in $(seq 1 "$budget"); do
    n=$( { grep -ahE "$PAGE_LINE_RE" "$OUT" 2>/dev/null; [[ -f "${LOG:-}" ]] && grep -ahE "$PAGE_LINE_RE" "$LOG" 2>/dev/null; true; } | sort -u | grep -c . || true )
    g=$( { grep -ahE '终页非健康.*grace' "$OUT" 2>/dev/null; [[ -f "${LOG:-}" ]] && grep -ahE '终页非健康.*grace' "$LOG" 2>/dev/null; true; } | sort -u | grep -c . || true )
    if [[ "$n" -gt 0 && "$n" -eq "$vlast" && "$g" -eq "$glast" ]]; then vstable=$((vstable + 1)); else vstable=0; fi
    vlast="$n"; glast="$g"
    if [[ "$vstable" -ge "$quiet" ]]; then echo "note: 页面裁决已稳定（${n} 行，静默 ${vstable}s，总用时 ${i}s）" >&2; return 0; fi
    sleep 1
  done
  echo "note: verdict 等待预算耗尽（末态 ${vlast} 行），按现状截图" >&2
  return 0
}

# 落定等待与心跳实现在 smoke-settle-lib.sh（上已 source）。

smoke_self_test() { # 纯函数回归：夹具断言 verdict/落定/心跳/回退门（函数实现在 smoke-settle-lib.sh）
  local tdir fail=0 live
  tdir="$(mktemp -d)"
  OUT="$tdir/out"; LOG="$tdir/host.log"; HOME_DIR="$tdir/home"; mkdir -p "$HOME_DIR"
  tpass() { echo "ok: $1"; }
  tfail() { echo "FAIL: $1"; fail=1; }
  echo "[host] dsh web = http://127.0.0.1:1/?token=t" >"$OUT"; : >"$LOG"
  [[ "$(smoke_verdict "$OUT" "$LOG")" == *"full-chain"* ]] && tpass "verdict-full" || tfail "verdict-full"
  : >"$OUT"; : >"$LOG"
  [[ "$(smoke_verdict "$OUT" "$LOG")" == *"install-chain"* ]] && tpass "verdict-install" || tfail "verdict-install"
  printf '[nav] 导航已到达：http://127.0.0.1:1/（origin → x）\n[nav] 导航已到达：http://127.0.0.1:1/?token=t → y\n' >"$OUT"
  sleep 30 & live=$!
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tpass "settle-ok" || tfail "settle-ok"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  printf '[nav] 导航已到达：http://127.0.0.1:1/（origin → x）\n' >"$OUT"; : >"$LOG"
  sleep 30 & live=$!
  SETTLE_WAIT=2 wait_settled "$live" >/dev/null 2>&1 && tfail "settle-timeout-should-fail" || tpass "settle-timeout-fails"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  printf '[host] dsh web = http://127.0.0.1:1/?token=t\n[shell] 代理源就绪：http://localhost:9/（回环独占）\n[nav] 导航已到达：http://localhost:9/\n' >"$OUT"; : >"$LOG"
  sleep 30 & live=$!
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tpass "settle-single-proxy" || tfail "settle-single-proxy"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  : >"$OUT"; : >"$LOG"
  SETTLE_WAIT=90 wait_settled "" >/dev/null 2>&1 && tfail "settle-deadpid-should-fail" || tpass "settle-deadpid-fails"
  heartbeat "60" "$HOME_DIR" 2>&1 | grep -q "等待中（60s）" && tpass "heartbeat" || tfail "heartbeat"
  # 回退门（R2 B1 回归锁）：①已见不得翻回；纯②才翻回；双无不翻
  rc=1
  printf '[bootstrap] 引导开始：x\n[host] dsh web = http://127.0.0.1:1/?token=t\n' >"$OUT"
  timeout_fallback >/dev/null 2>&1 && tfail "fallback-full-should-not-flip" || tpass "fallback-full-noflip"
  printf '[bootstrap] 引导开始：x\n' >"$OUT"
  timeout_fallback >/dev/null 2>&1 && tpass "fallback-boot-flips" || tfail "fallback-boot-flips"
  : >"$OUT"; : >"$LOG"
  timeout_fallback >/dev/null 2>&1 && tfail "fallback-empty-should-not-flip" || tpass "fallback-empty-noflip"
  # verdict 静默等待：单行静默即返；空文件耗尽预算仍 0（只定截图时机，不判门）；增长后稳定才返
  printf '[nav] 页面裁决=healthy（origin=x 可见文本 10 字）\n' >"$OUT"; : >"$LOG"
  wait_verdict 10 2 >/dev/null 2>&1 && tpass "verdict-quiescent-seen" || tfail "verdict-quiescent-seen"
  : >"$OUT"; : >"$LOG"
  wait_verdict 3 2 >/dev/null 2>&1 && tpass "verdict-quiescent-missing-proceeds" || tfail "verdict-quiescent-missing-proceeds"
  printf '[nav] 页面裁决=healthy（origin=x 可见文本 10 字）\n' >"$OUT"
  ( sleep 1; printf '[nav] 页面裁决=auth（origin=x 可见文本 5 字）\n' >>"$OUT" ) &
  bg=$!
  wait_verdict 10 2 >/dev/null 2>&1
  [[ "$(grep -cE '页面裁决=' "$OUT")" -eq 2 ]] && tpass "verdict-quiescent-waits-growth" || tfail "verdict-quiescent-waits-growth"
  wait "$bg" 2>/dev/null || true
  # grace 感知：verdict 稳定中途出现 grace 触发行 → 静默重置，不早返
  printf '[nav] 页面裁决=unknown（x）\n' >"$OUT"; : >"$LOG"
  ( sleep 1; printf '[nav] 终页非健康（unknown），grace 8s 后无 token 重载一次\n' >>"$OUT"; sleep 1; printf '[nav] 页面裁决=unknown（x，重载后）\n' >>"$OUT" ) &
  bg=$!
  wait_verdict 12 2 >/dev/null 2>&1
  [[ "$(grep -cE '页面裁决=' "$OUT")" -eq 2 ]] && tpass "verdict-quiescent-grace-reset" || tfail "verdict-quiescent-grace-reset"
  wait "$bg" 2>/dev/null || true
  printf '[nav] 页面裁决=auth（origin=http://127.0.0.1:1/?token=SECRET 可见文本 5 字）\n' >"$OUT"
  echo_verdict_lines 2>&1 | grep -q 'SECRET' && tfail "verdict-echo-masks-token" || tpass "verdict-echo-masks-token"
  echo_verdict_lines 2>&1 | grep -q 'token=\*\*\*' && tpass "verdict-echo-keeps-marker" || tfail "verdict-echo-keeps-marker"
  # mac 截图内容见证夹具（中央裁剪）：白（401 墙形态）判失败，浅底深块（UI 形态）判通过
  if command -v convert >/dev/null 2>&1; then
    convert -size 1024x768 xc:white "$tdir/m_white.png" 2>/dev/null
    convert -size 1024x768 xc:"#cccccc" -fill "#222222" -draw "rectangle 100,84 400,684" "$tdir/m_ui.png" 2>/dev/null
    smoke_capture_witness "$tdir/m_white.png" "800x600+0+0" "center" >/dev/null 2>&1 && tfail "mac-witness-blank-should-fail" || tpass "mac-witness-blank-fails"
    smoke_capture_witness "$tdir/m_ui.png" "800x600+0+0" "center" >/dev/null 2>&1 && tpass "mac-witness-ui-passes" || tfail "mac-witness-ui-fails"
  else
    echo "skip: 无 convert，跳过 mac 截图内容见证夹具"
  fi
  rm -rf "$tdir"
  [[ $fail -eq 0 ]] && echo "self-test: PASS" || echo "self-test: FAIL"
  return $fail
}

if [[ "$SELFTEST" -eq 1 ]]; then
  smoke_self_test
  exit $?
fi

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

echo "== 启动冒烟（等①就绪后等导航落定，②保底；窗=${SMOKE_WAIT}s/落定${SETTLE_WAIT}s）"
set +e
# 无人值守跳过可选插件（ADR preinstall-unattended-skip）：CI 无人点选，省 5 分钟决策等待。
env DSH_DESKTOP_DSH_HOME="$HOME_DIR" DEEPSEEK_API_KEY=placeholder DSH_DESKTOP_PREINSTALL_AUTO=skip \
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
    if wait_settled "$SMOKE_PID"; then
      rc=0
      smoke_verdict "$OUT" "$LOG"
      wait_verdict
      echo_verdict_lines
      # 裁决尘埃落定后再等有界重绘窗：提交回调早于新页出像素，立刻拍易拍到上一跳旧帧；只拍才睡。
      if [[ -n "${SMOKE_SHOT_DIR:-}" ]]; then
        sleep "$SMOKE_REPAINT_SECONDS"
      fi
      smoke_shot "smoke-macos.png"
      # 内容见证（与 Linux 同阈值，中央裁剪避菜单栏/Dock）：401 墙/引导页像素即红，截图缺失亦红。
      # 落定/裁决门：显示腿 mac 开门（ADR macos-cookie-grace-reload）——到达过不算绿。
      if [[ -n "${SMOKE_SHOT_DIR:-}" ]]; then
        smoke_capture_witness "$SMOKE_SHOT_DIR/smoke-macos.png" "800x600+0+0" "center" || rc=1
      fi
      # PASS 也打印壳输出尾部：壳何时/为何退出（如窗口创建即退出）需要证据在案
      echo "--- 壳输出尾部（PASS 证据）---" >&2
      tail -5 "$OUT" >&2 || true
    else
      rc=1
      echo_verdict_lines
      smoke_shot "smoke-macos-fail.png"
      echo "--- 壳输出尾部（FAIL 证据）---" >&2
      tail -30 "$OUT" >&2 || true
      if [[ -f "$LOG" ]]; then
        echo "--- host.log 尾部 ---" >&2
        tail -30 "$LOG" >&2 || true
      fi
    fi
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
      if wait_settled ""; then
        rc=0
        smoke_verdict "$OUT" "$LOG"
        echo_verdict_lines
        smoke_shot "smoke-macos.png"
        echo "--- 壳输出尾部（PASS 证据）---" >&2
        tail -5 "$OUT" >&2 || true
      else
        rc=1
        echo_verdict_lines
        smoke_shot "smoke-macos-fail.png"
        echo "--- 壳输出尾部（FAIL 证据）---" >&2
        tail -30 "$OUT" >&2 || true
      fi
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
  heartbeat "$SECONDS" "$HOME_DIR"
  sleep 1
done
# 超时仍只有②：按安装链 PASS（等满窗语义），而非失败。回退门保证①已见时不翻回
# （R2 B1：①已见 + 落定失败必须保持 FAIL）。
if timeout_fallback; then
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
    tail -30 "$LOG" >&2 || true
  fi
  smoke_shot "smoke-macos-fail.png"
fi
exit $rc
