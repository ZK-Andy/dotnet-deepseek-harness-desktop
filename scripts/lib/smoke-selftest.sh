#!/usr/bin/env bash
# smoke-selftest.sh — 三平台冒烟自测夹具（共用面，唯一家）。
# 契约：平台入口 source 本文件（经 smoke-selftest-verdict.sh 一并载入）后调
# `smoke_self_test_run <平台夹具函数名>`（骨架唯一家）；平台专属夹具函数由入口提供。
# 断言经 tpass/tfail 记入 _ST_PASS/_ST_FAIL 全局。
# 只含定义，source 无副作用；依赖 smoke-verdict-lib.sh 已载入（含 wait 与 common）。
# shellcheck disable=SC2034  # _ST_PASS 由各平台自测汇总行消费

tpass() { _ST_PASS=$((_ST_PASS + 1)); log "ok: $1"; }
tfail() { _ST_FAIL=$((_ST_FAIL + 1)); error "FAIL: $1"; }

# 行为完整日志（①+铸币303 走 stdout 面、3 条代理流量走 host.log 面）：落定夹具的公共前件。
# 双面分置与生产同形——linux/容器腿把两参数指同一文件（同流镜像），mac/win 腿分置。
# $3=追加到 stdout 面的行（可空，如裁决行）。
_st_ready_log() { # $1=stdout $2=host.log $3=追加行（可空）
  local out="$1" log="$2" extra="${3:-}"
  : >"$out"; : >"$log"
  printf '%s\n' '[host] dsh web = http://127.0.0.1:1/?token=t' \
    '[shell] 铸币：token 跳 → 303（set-cookie=[c] 共1个；http://127.0.0.1:1）' >>"$out"
  printf '%s\n' '[shell] 代理回包：200 application/json 100字节（POST /api/a）' \
    '[shell] 代理流转：200 text/event-stream（GET /plugins/events）' \
    '[shell] 代理回包：200 application/json 200字节（POST /api/b）' >>"$log"
  if [[ -n "$extra" ]]; then printf '%s\n' "$extra" >>"$out"; fi
}

# 活进程（落定等待要 pid 存活判据）。两个坑：①`sleep` 必须断掉标准输出，否则
# `live="$(_st_live)"` 会等满 30s（命令替换的管道被后台子进程持有）；②pid 经文件登记——
# 本函数跑在命令替换的子 shell 里，写全局变量不会传回调用方。
_st_live() {
  local p
  sleep 30 >/dev/null 2>&1 &
  p=$!
  printf '%s\n' "$p" >>"$_ST_TDIR/live.pids"
  printf '%s' "$p"
}
_st_kill_live() {
  local p
  if [[ -f "$_ST_TDIR/live.pids" ]]; then
    while IFS= read -r p; do
      kill "$p" 2>/dev/null || true
    done <"$_ST_TDIR/live.pids"
    : >"$_ST_TDIR/live.pids"
  fi
  wait 2>/dev/null || true
}

smoke_selftest_common() { # $1=夹具根目录
  local tdir="$1" live log
  _ST_PASS=0; _ST_FAIL=0
  _ST_TDIR="$tdir"
  : >"$tdir/out"; : >"$tdir/host.log"; : >"$tdir/live.pids"; mkdir -p "$tdir/home"
  # 判定函数读 OUT/LOG 全局（调用方契约）；夹具把双源指到夹具文件上。
  OUT="$tdir/out"; LOG="$tdir/host.log"

  # 结论行（单源/双源）
  echo '[host] dsh web = http://127.0.0.1:1/?token=t' >"$tdir/out"; : >"$tdir/host.log"
  [[ "$(smoke_verdict "$tdir/out" "$tdir/host.log" "")" == *"full-chain"* ]] && tpass "verdict-full" || tfail "verdict-full"
  : >"$tdir/out"
  [[ "$(smoke_verdict "$tdir/out" "$tdir/host.log" "")" == *"install-chain"* ]] && tpass "verdict-install" || tfail "verdict-install"
  echo '[host] dsh web = http://127.0.0.1:1/?token=t' >"$tdir/host.log"
  [[ "$(smoke_verdict "$tdir/out" "$tdir/host.log" " arch=x")" == *"full-chain（dsh web 就绪） arch=x"* ]] \
    && tpass "verdict-dual-source-with-suffix" || tfail "verdict-dual-source-with-suffix"

  # 落定：行为三信号齐 → 0；缺铸币或缺流量 → 1（到达不判门）
  _st_ready_log "$tdir/out" "$tdir/host.log"
  live="$(_st_live)"
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tpass "settle-ok" || tfail "settle-ok"
  _st_kill_live
  _st_ready_log "$tdir/out" "$tdir/host.log" '[nav] 页面裁决=healthy（origin=http://127.0.0.1:1 可见文本 400 字）'
  live="$(_st_live)"
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tpass "settle-verdict-healthy" || tfail "settle-verdict-healthy"
  _st_kill_live
  : >"$tdir/out"; : >"$tdir/host.log"
  printf '%s\n' '[host] dsh web = http://127.0.0.1:1/?token=t' >>"$tdir/out"
  printf '%s\n' '[shell] 代理回包：200 application/json 100字节（POST /api/a）' \
    '[shell] 代理流转：200 text/event-stream（GET /plugins/events）' \
    '[shell] 代理回包：200 application/json 200字节（POST /api/b）' >>"$tdir/host.log"
  live="$(_st_live)"
  SETTLE_WAIT=2 wait_settled "$live" >/dev/null 2>&1 && tfail "settle-no-mint-should-fail" || tpass "settle-no-mint-fails"
  _st_kill_live
  : >"$tdir/out"; : >"$tdir/host.log"
  printf '%s\n' '[host] dsh web = http://127.0.0.1:1/?token=t' \
    '[shell] 铸币：token 跳 → 303（set-cookie=[c] 共1个；http://127.0.0.1:1）' >>"$tdir/out"
  live="$(_st_live)"
  SETTLE_WAIT=2 wait_settled "$live" >/dev/null 2>&1 && tfail "settle-no-traffic-should-fail" || tpass "settle-no-traffic-fails"
  _st_kill_live
  # 到达不判门（回归锁）：产品不再发射落定导航，到达再多也不落定
  : >"$tdir/out"; : >"$tdir/host.log"
  printf '%s\n' '[host] dsh web = http://127.0.0.1:1/?token=t' \
    '[nav] 导航已到达：http://localhost:9/（origin → x）' \
    '[nav] 导航已到达：ryn://app/index.html' >>"$tdir/out"
  live="$(_st_live)"
  SETTLE_WAIT=2 wait_settled "$live" >/dev/null 2>&1 && tfail "settle-arrival-only-should-fail" || tpass "settle-arrival-only-fails"
  _st_kill_live
  # 裁决 auth 硬拦（行为齐也判 FAIL）
  _st_ready_log "$tdir/out" "$tdir/host.log" '[nav] 页面裁决=auth（origin=http://127.0.0.1:1 可见文本 60 字，请重开 dsh 打印的 URL；启动继续）'
  live="$(_st_live)"
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tfail "settle-auth-should-fail" || tpass "settle-auth-fails"
  _st_kill_live
  # 只认最后一条：healthy 塌成 auth → 1；auth 恢复 healthy → 0
  _st_ready_log "$tdir/out" "$tdir/host.log" $'[nav] 页面裁决=healthy（origin=http://127.0.0.1:1 可见文本 400 字）\n[nav] 页面裁决=auth（origin=http://127.0.0.1:1 可见文本 60 字）'
  live="$(_st_live)"
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tfail "settle-verdict-relapse-should-fail" || tpass "settle-verdict-relapse-fails"
  _st_kill_live
  _st_ready_log "$tdir/out" "$tdir/host.log" $'[nav] 页面裁决=auth（origin=http://127.0.0.1:1 可见文本 60 字）\n[nav] 页面裁决=healthy（origin=http://127.0.0.1:1 可见文本 400 字）'
  live="$(_st_live)"
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tpass "settle-verdict-recovery" || tfail "settle-verdict-recovery"
  _st_kill_live
  # 裁决只落在 host.log（OUT 兜底读得到）
  _st_ready_log "$tdir/out" "$tdir/host.log"
  printf '%s\n' '[nav] 页面裁决=auth（origin=http://127.0.0.1:1 可见文本 60 字，重进后，请重开 dsh 打印的 URL；启动继续）' >>"$tdir/host.log"
  live="$(_st_live)"
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tfail "settle-log-fallback-auth-should-fail" || tpass "settle-log-fallback-auth-fails"
  _st_kill_live
  # 行为齐 + 裁决行缺席 → 0（缺行交存活 + 见证判定）；unknown 同门
  _st_ready_log "$tdir/out" "$tdir/host.log"
  live="$(_st_live)"
  SETTLE_WAIT=2 wait_settled "$live" >/dev/null 2>&1 && tpass "settle-verdict-missing-passes" || tfail "settle-verdict-missing-passes"
  _st_kill_live
  _st_ready_log "$tdir/out" "$tdir/host.log" '[nav] 页面裁决=unknown（探针无采样，期望 origin=http://127.0.0.1:1）'
  live="$(_st_live)"
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tpass "settle-verdict-unknown-passes" || tfail "settle-verdict-unknown-passes"
  _st_kill_live
  # 空 pid = 进程已退出：一次机会，未落定即 1
  : >"$tdir/out"; : >"$tdir/host.log"
  SETTLE_WAIT=90 wait_settled "" >/dev/null 2>&1 && tfail "settle-deadpid-should-fail" || tpass "settle-deadpid-fails"
  # 双源双计回归锁：同一到达在 OUT 与带时间戳 host.log 各一行，去重后计 1
  printf '[nav] 导航已到达：http://localhost:9/（origin → x）\n' >"$tdir/out"
  printf '[2026-09-27 06:44:03] [nav] 导航已到达：http://localhost:9/（origin → x）\n' >"$tdir/host.log"
  [[ "$(nav_count)" -eq 1 ]] && tpass "settle-dedupe" || tfail "settle-dedupe"
  OUT="$tdir/out"; LOG="$tdir/host.log"
  heartbeat "60" "$tdir/home" 2>&1 | grep -q "等待中（60s）" && tpass "heartbeat" || tfail "heartbeat"

  # 回退门（R2 B1 回归锁）：①已见不得翻回；纯②才翻回；双无亦不翻
  rc=1
  printf '[bootstrap] 引导开始：x\n[host] dsh web = http://127.0.0.1:1/?token=t\n' >"$tdir/out"
  timeout_fallback >/dev/null 2>&1 && tfail "fallback-full-should-not-flip" || tpass "fallback-full-noflip"
  printf '[bootstrap] 引导开始：x\n' >"$tdir/out"
  timeout_fallback >/dev/null 2>&1 && tpass "fallback-boot-flips" || tfail "fallback-boot-flips"
  : >"$tdir/out"
  timeout_fallback >/dev/null 2>&1 && tfail "fallback-empty-should-not-flip" || tpass "fallback-empty-noflip"

  # 看门狗（无进展提前收工）：首 tick 活；窗内静止活；满窗静止判死；任一增长复位
  OUT="$tdir/wd-out"; LOG="$tdir/wd-log"; : >"$OUT"; : >"$LOG"; mkdir -p "$tdir/wd-home"
  progress_watchdog_reset
  progress_watchdog_tick 0 "$tdir/wd-home" && tpass "watchdog-first-alive" || tfail "watchdog-first-alive"
  progress_watchdog_tick $((_WD_STALL_SECONDS - 1)) "$tdir/wd-home" && tpass "watchdog-under-window" || tfail "watchdog-under-window"
  progress_watchdog_tick "$_WD_STALL_SECONDS" "$tdir/wd-home" && tfail "watchdog-stall-should-trip" || tpass "watchdog-stall-trips"
  printf '[09:45:10 info] Ryn.Core.RynApplication: noise\n[update] Checking\n[health] alive\n' >>"$OUT"
  progress_watchdog_tick $((_WD_STALL_SECONDS + 100)) "$tdir/wd-home" && tfail "watchdog-misc-should-not-reset" || tpass "watchdog-misc-no-reset"
  echo '[bootstrap] test-progress' >>"$OUT"
  progress_watchdog_tick $((_WD_STALL_SECONDS + 300)) "$tdir/wd-home" && tpass "watchdog-growth-resets" || tfail "watchdog-growth-resets"
  OUT="$tdir/out"; LOG="$tdir/host.log"

  # 一等循环（宿主三平台与容器腿共用实现）：①+落定 → 0；纯②（短窗）→ 0；双无 → 1；
  # ①无落定 → 1；①+铸币+单条代理流量（隧道）→ 0；仅占位到达不落定 → 1；退出时已落定 → 0
  log="$tdir/w1"; _st_ready_log "$log" "$log" '[bootstrap] 引导开始：x'
  live="$(_st_live)"
  SMOKE_WAIT=5 SETTLE_WAIT=90 smoke_wait_ready "$log" "$log" "$live" "$tdir/home" >/dev/null 2>&1 && tpass "wait-full" || tfail "wait-full"
  _st_kill_live
  log="$tdir/w2"; printf '[bootstrap] 引导开始：x\n' >"$log"
  live="$(_st_live)"
  SMOKE_WAIT=2 smoke_wait_ready "$log" "$log" "$live" "$tdir/home" >/dev/null 2>&1 && tpass "wait-bootonly" || tfail "wait-bootonly"
  _st_kill_live
  log="$tdir/w3"; : >"$log"
  SMOKE_WAIT=2 smoke_wait_ready "$log" "$log" "99999999" "$tdir/home" >/dev/null 2>&1 && tfail "wait-nosignal-should-fail" || tpass "wait-nosignal-fails"
  log="$tdir/w4"; printf '[bootstrap] 引导开始：x\n[host] dsh web = http://127.0.0.1:1/?token=t\n' >"$log"
  live="$(_st_live)"
  SMOKE_WAIT=5 SETTLE_WAIT=2 smoke_wait_ready "$log" "$log" "$live" "$tdir/home" >/dev/null 2>&1 && tfail "wait-nosettle-should-fail" || tpass "wait-nosettle-fails"
  _st_kill_live
  # 单条代理回包 + 单条流转 + 单条 WS 隧道 = 恰好 3 条存活信号（隧道形态曾被漏数，R3 实证）
  printf '%s\n' '[host] dsh web = http://127.0.0.1:1/?token=t' \
    '[shell] 铸币：token 跳 → 303（set-cookie=[c] 共1个；http://127.0.0.1:1）' \
    '[shell] 代理回包：200 application/json 100字节（POST /api/a）' \
    '[shell] 代理流转：200 text/event-stream（GET /plugins/events）' \
    '[shell] 代理升级隧道已建（GET /api/remote.mux Upgrade=websocket；任一端关闭即收）' \
    '[bootstrap] 引导开始：x' >"$tdir/w4b"
  live="$(_st_live)"
  SMOKE_WAIT=5 SETTLE_WAIT=90 smoke_wait_ready "$tdir/w4b" "$tdir/w4b" "$live" "$tdir/home" >/dev/null 2>&1 && tpass "wait-min-alive-settles" || tfail "wait-min-alive-settles"
  _st_kill_live
  log="$tdir/w4c"; printf '[bootstrap] 引导开始：x\n[host] dsh web = http://127.0.0.1:1/?token=t\n[nav] 导航已到达：ryn://app/index.html\n' >"$log"
  live="$(_st_live)"
  SMOKE_WAIT=5 SETTLE_WAIT=2 smoke_wait_ready "$log" "$log" "$live" "$tdir/home" >/dev/null 2>&1 && tfail "wait-placeholder-should-not-settle" || tpass "wait-placeholder-not-settled"
  _st_kill_live
  _st_ready_log "$tdir/w5" "$tdir/w5" '[bootstrap] 引导开始：x'
  SMOKE_WAIT=5 SETTLE_WAIT=90 smoke_wait_ready "$tdir/w5" "$tdir/w5" "99999999" "$tdir/home" >/dev/null 2>&1 && tpass "wait-exit-settled" || tfail "wait-exit-settled"
  _st_ready_log "$tdir/w6" "$tdir/w6" '[nav] 页面裁决=healthy（origin=http://127.0.0.1:1 可见文本 400 字）'
  live="$(_st_live)"
  SMOKE_WAIT=5 SETTLE_WAIT=90 smoke_wait_ready "$tdir/w6" "$tdir/w6" "$live" "$tdir/home" >/dev/null 2>&1 && tpass "wait-verdict-healthy" || tfail "wait-verdict-healthy"
  _st_kill_live
  # 双源腿（mac/win：stdout 与 host.log 分置）——①/铸币走 stdout、存活信号走 host.log
  _st_ready_log "$tdir/w7" "$tdir/w7-log" '[bootstrap] 引导开始：x'
  live="$(_st_live)"
  SMOKE_WAIT=5 SETTLE_WAIT=90 smoke_wait_ready "$tdir/w7" "$tdir/w7-log" "$live" "$tdir/home" >/dev/null 2>&1 && tpass "wait-dual-source" || tfail "wait-dual-source"
  _st_kill_live

  # 窗口解析契约（唯一家）：覆写旋钮 → 库全局；未设 → 缺省。rpm 容器腿曾因漏调解析函数
  # 在 set -u 下整腿炸掉，此处用夹具把该契约钉住（容器本机无 docker，脚本内另有一层端到端夹具）。
  SMOKE_WAIT_SECONDS=3 SMOKE_SETTLE_SECONDS=4 smoke_resolve_windows
  [[ "$SMOKE_WAIT" -eq 3 && "$SETTLE_WAIT" -eq 4 ]] && tpass "windows-from-knobs" || tfail "windows-from-knobs"
  unset SMOKE_WAIT_SECONDS SMOKE_SETTLE_SECONDS
  smoke_resolve_windows
  [[ "$SMOKE_WAIT" -eq 720 && "$SETTLE_WAIT" -eq 90 ]] && tpass "windows-defaults" || tfail "windows-defaults"
  SMOKE_WAIT=720 SETTLE_WAIT=90

  _st_kill_live
  log "共用夹具：$_ST_PASS ok / $_ST_FAIL FAIL"
  [[ $_ST_FAIL -eq 0 ]]
}

# 三平台入口共用的自测入口（骨架唯一家）：共用面 + 可选的平台专属面。

smoke_self_test_run() { # $1=平台夹具函数名（可空）
  local tdir fn="${1:-}"
  tdir="$(mktemp -d)"
  smoke_selftest_common "$tdir"
  smoke_selftest_verdict "$tdir"
  if [[ -n "$fn" ]]; then "$fn" "$tdir"; fi
  rm -rf "$tdir"
  if [[ $_ST_FAIL -eq 0 ]]; then
    echo "self-test: PASS（$_ST_PASS ok）"
    return 0
  fi
  echo "self-test: FAIL（$_ST_FAIL 失败 / $((_ST_PASS + _ST_FAIL)) 条）"
  return 1
}
