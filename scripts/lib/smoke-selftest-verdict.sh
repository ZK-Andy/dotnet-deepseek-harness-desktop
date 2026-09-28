#!/usr/bin/env bash
# smoke-selftest-verdict.sh — 冒烟判定面夹具（结论行/存活门/像素见证/裁决静默/证据脱敏）。
# 与 smoke-selftest.sh（等待与落定面）同属共用夹具，分文件只为尺寸闸（单文件 ≤250 行）。
# source 本文件即把基座（smoke-selftest.sh，含夹具 helper 与 smoke_self_test_run）一并载入——
# 消费方只需 source 本文件。
# shellcheck disable=SC2034  # _ST_FAIL 由汇总行消费

_SS_VERDICT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./smoke-selftest.sh
source "$_SS_VERDICT_DIR/smoke-selftest.sh"

smoke_selftest_verdict() { # $1=夹具根目录
  local tdir="$1" bg
  # 存活门：代理 200（缓冲 + 流式）或 WS 隧道合计 ≥3 即活；不足/空文件即死
  _st_ready_log "$tdir/alive" "$tdir/alive"
  smoke_client_alive "$tdir/alive" && tpass "alive-enough-passes" || tfail "alive-enough-passes"
  printf '%s\n' '[shell] 代理升级隧道已建（GET /api/remote.mux Upgrade=websocket；任一端关闭即收）' \
      '[shell] 代理升级隧道已建（GET /api/remote.mux Upgrade=websocket；任一端关闭即收）' \
      '[shell] 代理升级隧道已建（GET /api/remote.mux Upgrade=websocket；任一端关闭即收）' >"$tdir/alive"
  smoke_client_alive "$tdir/alive" && tpass "alive-tunnel-passes" || tfail "alive-tunnel-passes"
  printf '[shell] 代理流转：200 application/json（POST /api/a）\n' >"$tdir/alive"
  smoke_client_alive "$tdir/alive" && tfail "alive-short-should-fail" || tpass "alive-short-fails"
  : >"$tdir/alive"
  smoke_client_alive "$tdir/alive" && tfail "alive-empty-should-fail" || tpass "alive-empty-fails"

  # 截图内容见证：白（401 墙）/深色（引导页）判失败，浅底深块（UI）判通过
  if command -v convert >/dev/null 2>&1; then
      convert -size 1200x800 xc:white "$tdir/w_white.png" 2>/dev/null
      convert -size 1200x800 xc:"#111111" "$tdir/w_dark.png" 2>/dev/null
      convert -size 1200x800 xc:"#cccccc" -fill "#222222" -draw "rectangle 0,0 300,800" "$tdir/w_ui.png" 2>/dev/null
      smoke_capture_witness "$tdir/w_white.png" >/dev/null 2>&1 && tfail "witness-blank-should-fail" || tpass "witness-blank-fails"
      smoke_capture_witness "$tdir/w_dark.png" >/dev/null 2>&1 && tfail "witness-dark-should-fail" || tpass "witness-dark-fails"
      smoke_capture_witness "$tdir/w_ui.png" >/dev/null 2>&1 && tpass "witness-ui-passes" || tfail "witness-ui-passes"
      smoke_capture_witness "$tdir/absent.png" >/dev/null 2>&1 && tfail "witness-missing-should-fail" || tpass "witness-missing-fails"
  else
      log "skip: 无 convert，跳过截图内容见证夹具"
  fi

  # 裁决静默等待（mac 截图时机）：单行静默即返；空文件耗尽预算仍 0（只定时机，不判门）
  OUT="$tdir/out"; LOG="$tdir/host.log"
  printf '[nav] 页面裁决=healthy（origin=x 可见文本 10 字）\n' >"$OUT"; : >"$LOG"
  wait_verdict 10 2 >/dev/null 2>&1 && tpass "verdict-quiescent-seen" || tfail "verdict-quiescent-seen"
  : >"$OUT"; : >"$LOG"
  wait_verdict 3 2 >/dev/null 2>&1 && tpass "verdict-quiescent-missing-proceeds" || tfail "verdict-quiescent-missing-proceeds"

  # 证据回显脱敏：token 值不回显，标记保留
  printf '[nav] 页面裁决=auth（origin=http://127.0.0.1:1/?token=SECRET 可见文本 5 字）\n' >"$OUT"
  echo_verdict_lines 2>&1 | grep -q 'SECRET' && tfail "evidence-echo-masks-token" || tpass "evidence-echo-masks-token"
  echo_verdict_lines 2>&1 | grep -q 'token=\*\*\*' && tpass "evidence-echo-keeps-marker" || tfail "evidence-echo-keeps-marker"

  # 共享库面的回归锁放在共用面（原先只有 mac 的平台夹具钉住 wait_verdict 两侧行为——
  # 锁在平台文件里会随该平台夹具改动一起失锁）。
  OUT="$tdir/out"; LOG="$tdir/host.log"
  printf '[nav] 页面裁决=healthy（origin=x 可见文本 10 字）\n' >"$OUT"
  ( sleep 1; printf '[nav] 页面裁决=auth（origin=x 可见文本 5 字）\n' >>"$OUT" ) &
  local bg=$!
  wait_verdict 10 2 >/dev/null 2>&1
  [[ "$(grep -cE '页面裁决=' "$OUT")" -eq 2 ]] && tpass "verdict-quiescent-waits-growth" || tfail "verdict-quiescent-waits-growth"
  wait "$bg" 2>/dev/null || true
  printf '[nav] 页面裁决=unknown（x）\n' >"$OUT"; : >"$LOG"
  ( sleep 1; printf '[nav] 终页非健康（unknown），grace 8s 后无 token 重载一次\n' >>"$OUT"; sleep 1; printf '[nav] 页面裁决=unknown（x，重载后）\n' >>"$OUT" ) &
  bg=$!
  wait_verdict 12 2 >/dev/null 2>&1
  [[ "$(grep -cE '页面裁决=' "$OUT")" -eq 2 ]] && tpass "verdict-quiescent-grace-reset" || tfail "verdict-quiescent-grace-reset"
  wait "$bg" 2>/dev/null || true
  return 0
}
