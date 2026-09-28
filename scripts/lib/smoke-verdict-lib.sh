#!/usr/bin/env bash
# smoke-verdict-lib.sh — 冒烟判定与证据共享库（唯一家：结论行/存活门/像素见证/证据打印/
# 无人值守三元组/裁决静默等待；后者预算旋钮 SMOKE_VERDICT_SECONDS 可覆写，缺省 150s）。依赖 smoke-wait-lib.sh（log_has、PAGE_LINE_RE、nav/mint 行）
# 与 common.sh；source 本库即把两者一并载入，消费方只需 source 本文件。
# 只含定义，source 无副作用；调用方须已 `set -euo pipefail`。

_SMOKE_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./common.sh
source "$_SMOKE_LIB_DIR/common.sh"
# shellcheck source=./smoke-wait-lib.sh
source "$_SMOKE_LIB_DIR/smoke-wait-lib.sh"

# 判定结论（ADR smoke-runner-deepening）：命中 ① 全链还是 ② 安装链必须打印成结论，
# 不能靠翻日志。$3 为可选后缀（mac 腿带 arch 出处：x64 leg 跑在 ARM runner = Rosetta 下验证）。
smoke_verdict() { # $1=stdout $2=host.log $3=后缀（可空）
  local out="${1:-}" log="${2:-}" suffix="${3:-}"
  if grep -qE '\[host\] dsh web =' "$out" 2>/dev/null \
    || { [[ -f "$log" ]] && grep -qE '\[host\] dsh web =' "$log"; }; then
    printf 'SMOKE_VERDICT=full-chain（dsh web 就绪）%s\n' "$suffix"
  else
    printf 'SMOKE_VERDICT=install-chain（仅引导启动）%s\n' "$suffix"
  fi
}

# 无人值守三元组（ADR preinstall-unattended-skip）：CI 无人点选，省 5 分钟决策等待。
# 导出而非调用点内联：调用点要 `timeout ... &` + `$!`（timeout 的 pid），写成
# `env A=1 timeout ... &` 与导出等价，但三元组会在各处各写一遍；容器腿用
# `docker -e VAR`（不带 =value）从本进程环境透传两个常量项，同样单源。
# $1=dsh-home（可空：只导出两个常量项，供容器腿透传——容器自建 home）。
smoke_unattended_env() {
  export DEEPSEEK_API_KEY=placeholder DSH_DESKTOP_PREINSTALL_AUTO=skip
  if [[ -n "${1:-}" ]]; then export DSH_DESKTOP_DSH_HOME="$1"; fi
}

# 客户端存活门（ADR loopback-forward-proxy）：浅色主题真 UI 的 mean/sd 与空白页重叠
# （mac light UI mean≈0.99/sd≈0.04 vs 401 墙 sd≈0.04），像素无法区分；改证行为——
# dsh 客户端启动后必经代理发 RPC/SSE/WS（同源），host.log 里 200 回包/流转或 WS 隧道
# 合计 ≥3 即活（实测启动数秒内 ~10 次）。三种前缀都要数：缓冲体走 `代理回包：`、
# 流式走 `代理流转：`（只数其一即漏数，R3 实证）、WS 走 `代理升级隧道已建`
# （remote.mux 无 200 行，纯 WS 形态会漏数）。holder 零次——落定门的行为支即此。
# $1=host.log 路径。0=存活（落定门行为支 + 像素见证 OR 组绿）。
smoke_client_alive() {
  local log="${1:-}" n=0
  [[ -f "$log" ]] || return 1
  n=$(grep -cE "代理(流转|回包)：200 (application/json|text/event-stream)|代理升级隧道已建" "$log" 2>/dev/null || true)
  [[ "${n:-0}" -ge 3 ]]
}

# 截图内容见证（ADR page-verdict-gate）：外部 origin 上 DOM 探针回不来，故内容真伪由**截图本身**判——
# 近空白（401 墙：实测 mean≈1.00/sd≈0.04）与深色引导页（mean≈0.14）判失败，真 UI（mean≈0.81/sd≈0.13）通过。
# 阈值取自 CI 实测四图；无 convert 即 fail loud（调用点已限定显示腿）。
# $1=截图路径；$2=裁剪几何（默认 1200x800+0+0，Linux Xvfb 全屏沿用）；
# $3=gravity（默认空；mac 全屏截图含菜单栏/Dock 时传 center 取中央避边框 chrome）。0=内容像 UI。
# bash 3.2 安全：无数组展开（mac runner 默认 bash 3.2，空数组 + set -u 即炸）。
smoke_capture_witness() {
  local shot="$1" crop="${2:-1200x800+0+0}" gravity="${3:-}" stats mean sd
  [[ -s "$shot" ]] || { error "截图缺失，内容见证不通过：$shot"; return 1; }
  command -v convert >/dev/null 2>&1 || { error "无 convert，截图内容见证无法执行（显示腿须装 imagemagick）"; return 1; }
  if [[ -n "$gravity" ]]; then
    stats="$(convert "$shot" -gravity "$gravity" -crop "$crop" +repage -colorspace Gray -format '%[fx:mean] %[fx:standard_deviation]' info: 2>/dev/null || true)"
  else
    stats="$(convert "$shot" -crop "$crop" +repage -colorspace Gray -format '%[fx:mean] %[fx:standard_deviation]' info: 2>/dev/null || true)"
  fi
  mean="${stats%% *}"; sd="${stats##* }"
  if [[ -z "$mean" || -z "$sd" || "$mean" == "$stats" ]]; then
    error "截图统计失败（ImageMagick），内容见证不通过"; return 1
  fi
  if awk "BEGIN{exit !($mean >= 0.35 && $sd >= 0.08)}"; then
    log "截图内容见证通过（mean=${mean} sd=${sd}）"; return 0
  fi
  error "截图内容见证不通过（mean=${mean} sd=${sd}）：近空白/深色页（401 墙或引导页）不算 UI"
  return 1
}

# 应用侧 verdict/重进/探针行回显到 step 日志（token 脱敏）：CI 日志直接可见终态是
# healthy 还是 auth/unknown。只读观测，不判门（形状仿 echo_nav_lines）。
echo_verdict_lines() {
  local re='\[nav\] 页面裁决=|\[nav\] 检测到鉴权页|鉴权探针|终页非健康'
  { grep -ahE "$re" "${OUT:-}" 2>/dev/null; [[ -n "${LOG:-}" && -f "${LOG:-}" ]] && grep -ahE "$re" "$LOG" 2>/dev/null; true; } \
    | sed -E 's/token=[^& ]*/token=***/g' | sort -u >&2 || true
}

# verdict 静默等待（mac 腿截图时机 aid，不判门，恒 0）：等裁决行出现后"裁决行数 + grace
# 触发行数"双双 QUIET 秒不变即返；grace 触发（应用侧重载在途）即重置静默计数——文本相同的
# 两条裁决行被 sort -u 压成一行也误不了事。预算耗尽按现状截图。
# 保留而非删（A3 的裁定见 ADR script-layer-consolidation）：截图是像素见证门的输入，而 grace
# 重载会改写终页——删掉这层时序对齐会让绿跑的截图拍到重载前旧页，证据保真度下降，
# 用 150s 有界等待换取"截到的就是终页"。
# $1=总预算秒（默认 SMOKE_VERDICT_SECONDS 或 150），$2=静默秒（默认 5）。
wait_verdict() {
  local budget="${1:-${SMOKE_VERDICT_SECONDS:-150}}" quiet="${2:-5}" i vlast=-1 glast=-1 vstable=0 n g
  for i in $(seq 1 "$budget"); do
    n=$( { grep -ahE "$PAGE_LINE_RE" "${OUT:-}" 2>/dev/null; [[ -n "${LOG:-}" && -f "${LOG:-}" ]] && grep -ahE "$PAGE_LINE_RE" "$LOG" 2>/dev/null; true; } | sort -u | grep -c . || true )
    g=$( { grep -ahE '终页非健康.*grace' "${OUT:-}" 2>/dev/null; [[ -n "${LOG:-}" && -f "${LOG:-}" ]] && grep -ahE '终页非健康.*grace' "$LOG" 2>/dev/null; true; } | sort -u | grep -c . || true )
    if [[ "$n" -gt 0 && "$n" -eq "$vlast" && "$g" -eq "$glast" ]]; then vstable=$((vstable + 1)); else vstable=0; fi
    vlast="$n"; glast="$g"
    if [[ "$vstable" -ge "$quiet" ]]; then log "页面裁决已稳定（${n} 行，静默 ${vstable}s，总用时 ${i}s）"; return 0; fi
    sleep 1
  done
  log "verdict 等待预算耗尽（末态 ${vlast} 行），按现状截图"
  return 0
}

# 日志落盘（W2）：调用方经 SMOKE_LOG_DIR 注入稳定目录（与 SMOKE_SHOT_DIR 同模式），CI 传
# artifact——host.log 只在文件里全，step 日志只有尾巴。缺目录（本地跑）即静默跳过。
# $1=文件名前缀（如 smoke-linux-deb） $2=stdout 文件 $3=host.log 文件
smoke_dump_logs() {
  local prefix="$1" out="$2" hostlog="$3"
  [[ -n "${SMOKE_LOG_DIR:-}" ]] || return 0
  mkdir -p "$SMOKE_LOG_DIR" 2>/dev/null || return 0
  cp "$out" "$SMOKE_LOG_DIR/${prefix}.log" 2>/dev/null || true
  if [[ -n "$hostlog" && -f "$hostlog" && "$hostlog" != "$out" ]]; then
    cp "$hostlog" "$SMOKE_LOG_DIR/${prefix}-host.log" 2>/dev/null || true
  fi
}

# ── 证据打印（唯一家）─────────────────────────────────────────────────────
# 绿跑也留尾（ADR verdict-honesty-repair）：绿跑的导航/探针/自愈行此前随日志删除，
# "绿即无证"致 401 绿 verdict 无从复核；失败分支只打 tail 会被代理行为日志淹没关键行。
# $1=stdout $2=host.log $3=full|tail——full 打整段 stdout（应用秒退时 stderr 是唯一定位
# 线索，arm64 首跑实证，linux 腿用）；tail 打 5/30 行尾巴（mac/win 腿用）。
smoke_evidence_pass() { # $1=stdout $2=host.log $3=结论行后缀（可空，如 mac 腿的 ` arch=$(uname -m)`）
  local out="$1" log="$2" suffix="${3:-}"
  smoke_verdict "$out" "$log" "$suffix"
  echo_verdict_lines
  log "壳输出尾部（PASS 证据）："
  tail -5 "$out" >&2 || true
}

smoke_evidence_fail() { # $1=stdout $2=host.log $3=full|tail
  local out="$1" log="$2" mode="${3:-tail}"
  echo_verdict_lines
  log "到达/铸币行（FAIL 判定信号，去重）："
  echo_nav_lines
  echo_mint_lines
  if [[ "$mode" == "full" ]]; then
    log "冒烟失败，应用日志全文："
    cat "$out" >&2 || true
  else
    log "壳输出尾部（FAIL 证据）："
    tail -30 "$out" >&2 || true
    if [[ -f "$log" ]]; then
      log "host.log 尾部："
      tail -30 "$log" >&2 || true
    fi
  fi
}
