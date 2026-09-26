#!/usr/bin/env bash
# smoke-install-linux.sh — Linux 安装冒烟（批次一，ADR artifact-verification-chain；
# online-first 批次二起覆盖「装包 → 首启引导 → dsh web URL」全链）。
# 对构建产物目录中的 deb/rpm 做「干净环境装包 → 启动 → 等 dsh web URL」验证：
#   deb → runner 原生 apt 安装（真实解析 Depends）
#   rpm → fedora 容器内 dnf 安装（AutoReqProv:no 的显式 Requires 是否够，装了才知道）
# 判定信号（双信号）：
#   ①`[host] dsh web =`（注意是等号——`dsh web:` 冒号格式是 dsh 子进程自检输出，壳打印的是等号格式；首版判定串错位致冒烟恒败，CI 实证）= dsh 就绪（传输层）；
#   ②`[bootstrap] 引导开始：` = 安装链保底（装包→依赖齐→运行时检测→首启引导已启动）。
# 等待语义（ADR smoke-wait-full-after-boot）：②命中后不收工，继续等①至
# 超时或进程退出；超时仍只有②按安装链 PASS，进程退出按退出时最佳信号收工。
# 落定语义（ADR smoke-settle-content-verdict + page-verdict-gate）：①只是 dsh 就绪行，verdict 与截图
# 必须等导航落定——`[nav] 导航已到达` 去重 ≥2 次且含 `?token=` 第二跳，或①之后到达 ≥2 次（Linux 只报最终 URL）。落定超时或
# 落定期进程退出即 FAIL（dsh 已就绪但 UI 未落定是真实事故，不再按 full-chain 放行）。
#     deb 腿（有显示）还要等应用自己的终页裁决行 `[nav] 页面裁决=healthy` 才算落定：
#     auth / unknown / 裁决未出现（探针未回）皆 FAIL——绿必须等于"同源且非鉴权页"，
#     到达过的 401 页（v0.5.7 实跑：verdict 绿配 401 图）不再能冒充通过；截图在裁决之后
#     再等一个有界重绘窗（SMOKE_REPAINT_SECONDS，默认 3s）才拍，避免拍到上一跳的旧像素。
#     mac/win 腿本批未置位（下批开门）；rpm 容器腿无 X，①不可达 ⇒ 恒②安装链，落定/裁决门不适用（置位腿之外的落定仍只认到达，auth 除外）。
#     CI 经 xvfb-run 启动（ADR smoke-linux-xvfb-fullchain）：虚拟 DISPLAY 下窗口可创建，
#     引导后台任务存活——deb 腿全链信号可达，落定 verdict + 截图真实开火；Xvfb 起不来
#     或无显示直跑仍回退②安装链（回退门语义不变）。rpm 容器腿无 X，恒②。
#     引导下载/安装全链的验证在实机验收转交（批次一沙箱 E2E 已通）。
# 直击事故类：v0.2.x「rpm 实机装不上」、libadwaita 缺依赖崩溃（2026-08-29 冒烟暴露，
# deb/rpm 已补显式声明）+ online-first「引导断链、dsh 起不来」。
#
# 用法: smoke-install-linux.sh <产物目录（含 *.deb 与/或 *.rpm）>
# 自测: smoke-install-linux.sh --self-test（纯函数 + wait_url 回归，不碰装包）
set -euo pipefail

SELFTEST=0
if [[ "${1:-}" == "--self-test" ]]; then SELFTEST=1; fi
if [[ "$SELFTEST" -eq 0 ]]; then
  PKG_DIR="${1:?usage: smoke-install-linux.sh <dir-with-deb/rpm>}"
  [[ -d "$PKG_DIR" ]] || { echo "error: 目录不存在: $PKG_DIR" >&2; exit 1; }
  PKG_DIR="$(realpath "$PKG_DIR")"
fi
APP_BIN="/usr/bin/deepseek-harness-desktop"

# 等待启动日志出现 [host] dsh web = 的公共循环。进程探活用 kill -0 <pid>：
# 安装后的入口是小写符号链接（/usr/bin/deepseek-harness-desktop），pgrep 按
# 大写二进制名匹配会立刻误判「进程已死」。进程退出后再补扫一次日志，兜住
# 「URL 已打出但进程随即退出」的窗口。
# 等待窗 = 单轮尝试预算：RuntimeBootstrapOptions.StepTimeoutMinutes（默认 10 分钟）
# 单步上限 + 120s 余量 = 720s。重试轮不计入——冒烟只等首轮落定，超时按②收工。
# 引导步数或 StepTimeoutMinutes 变化时必须同批重算。SMOKE_WAIT_SECONDS 可覆写。
SMOKE_WAIT="${SMOKE_WAIT_SECONDS:-720}"
# 落定窗：①出现后等导航提交，再等应用终页裁决。预算须覆盖含自愈重进的最坏链
# （建窗 120 残量 + 2×(导航调用 30 + 提交 5) + 探针 15×2 + 重进 35 + 再探针 30 ≈ 285s，
# ADR page-verdict-gate），故 ci 矩阵按架构给值（SMOKE_SETTLE_SECONDS；见 package-linux.yml）。
# 硬上限（用户令）：冒烟总时长不许超 3 分钟，落定窗不得超过 120s——CI 矩阵值超限即夹紧，避免"加时间换绿"。
SMOKE_MAX_SETTLE_SECONDS="${SMOKE_MAX_SETTLE_SECONDS:-120}"
SETTLE_WAIT="${SMOKE_SETTLE_SECONDS:-90}"
if [[ "$SETTLE_WAIT" =~ ^[0-9]+$ ]] && (( SETTLE_WAIT > SMOKE_MAX_SETTLE_SECONDS )); then
  echo "note: 落定窗 ${SETTLE_WAIT}s 超过上限 ${SMOKE_MAX_SETTLE_SECONDS}s，按上限执行（时长硬约束）" >&2
  SETTLE_WAIT="$SMOKE_MAX_SETTLE_SECONDS"
fi
# 裁决后重绘窗（秒）：WebKit 提交回调早于新页出像素，裁决一过立刻拍易拍到上一跳旧帧
# （ADR page-verdict-gate）；无显示时 smoke_shot 本就早退，不睡。非数字按默认。
SMOKE_REPAINT_SECONDS="${SMOKE_REPAINT_SECONDS:-3}"
[[ "$SMOKE_REPAINT_SECONDS" =~ ^[0-9]+$ ]] || SMOKE_REPAINT_SECONDS=3
APP_TIMEOUT=$((SMOKE_WAIT + 20))
FULL_RE='\[host\] dsh web ='
BOOT_RE='\[bootstrap\] 引导开始：'
PASS_RE="$FULL_RE|$BOOT_RE"

# 落定等待共享库（NAV 正则 + log_has/nav/wait_settled/heartbeat/timeout_fallback）。
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LIB="$SCRIPT_DIR/smoke-settle-lib.sh"
# shellcheck disable=SC1091
source "$LIB"

# 判定结论（ADR smoke-runner-deepening）：命中 ① 全链还是 ② 安装链必须打印成结论。
smoke_verdict() { # $1=日志
  if grep -qE '\[host\] dsh web =' "$1" 2>/dev/null; then
    echo "SMOKE_VERDICT=full-chain（dsh web 就绪）"
  else
    echo "SMOKE_VERDICT=install-chain（仅引导启动）"
  fi
}

# 启动截图 best-effort（ADR smoke-runner-deepening）：供人眼复核，永不拦冒烟。
# CI 经 xvfb-run 启动（ADR smoke-linux-xvfb-fullchain）时 $DISPLAY 存在即真实开火；
# 无显示（本地直跑/rpm 容器）仍跳过。调用方须在 kill 之前拍（活页终页证据；死后拍多为空）。
# 工具链 fail loud 化：按序试 import/scrot/gnome-screenshot，坏工具（存在但拍失败，
# 曾遮掉可用 scrot 致空包）即清残文件换下一个；开火/全败皆留痕（含字节数）。
smoke_shot() { # $1=文件名
  [[ -n "${SMOKE_SHOT_DIR:-}" && -n "${DISPLAY:-}" ]] || return 0
  mkdir -p "$SMOKE_SHOT_DIR" 2>/dev/null || return 0
  local shot="$SMOKE_SHOT_DIR/$1" fired=""
  if [[ -z "$fired" ]] && command -v import >/dev/null 2>&1; then
    if shot_capped import -window root "$shot" 2>/dev/null && [[ -s "$shot" ]]; then fired="import"; else rm -f "$shot"; fi
  fi
  if [[ -z "$fired" ]] && command -v scrot >/dev/null 2>&1; then
    if shot_capped scrot "$shot" 2>/dev/null && [[ -s "$shot" ]]; then fired="scrot"; else rm -f "$shot"; fi
  fi
  if [[ -z "$fired" ]] && command -v gnome-screenshot >/dev/null 2>&1; then
    if shot_capped gnome-screenshot -f "$shot" 2>/dev/null && [[ -s "$shot" ]]; then fired="gnome-screenshot"; else rm -f "$shot"; fi
  fi
  if [[ -n "$fired" ]]; then
    echo "note: 截图已存（${fired}）：$shot（$(wc -c <"$shot" 2>/dev/null || echo ?) 字节）" >&2
  else
    echo "note: 截图失败（import/scrot/gnome-screenshot 均无或全败）" >&2
  fi
}

# 截图单工具封顶（R2 轻审）：DISPLAY 存在但 X 失联时坏工具若阻塞会拖住 kill/收尾；
# timeout 存在即 20s 封顶（runner 有；缺则直跑，不引入新依赖）。
shot_capped() {
  if command -v timeout >/dev/null 2>&1; then timeout 20 "$@"; else "$@"; fi
}

# 落定等待与心跳实现在 smoke-settle-lib.sh（上已 source）。

wait_url() { # $1=日志 $2=pid $3=dsh-home：①命中即落定等待；只有②（超时或退出时）亦 0；双无才 1
  local log="$1" pid="$2" home="$3" boot_seen=0 start="$SECONDS"
  OUT="$log"; LOG=""
  for _ in $(seq 1 "$SMOKE_WAIT"); do
    if grep -qE "$FULL_RE" "$log"; then
      grep -m1 -E "$FULL_RE" "$log"
      wait_settled "$pid"
      return $?
    fi
    if [[ $boot_seen -eq 0 ]] && grep -qE "$BOOT_RE" "$log"; then
      boot_seen=1
      grep -m1 -E "$BOOT_RE" "$log"
      echo "note: 已见②安装链（$((SECONDS - start))s），继续等①至超时/退出…" >&2
    fi
    if ! kill -0 "$pid" 2>/dev/null; then
      if grep -qE "$FULL_RE" "$log"; then grep -m1 -E "$FULL_RE" "$log"; wait_settled ""; return $?; fi
      if grep -qE "$BOOT_RE" "$log"; then echo "note: 进程已退出，未见①，按②安装链收工" >&2; return 0; fi
      return 1
    fi
    heartbeat "$((SECONDS - start))" "$home"
    sleep 1
  done
  if grep -qE "$FULL_RE" "$log"; then grep -m1 -E "$FULL_RE" "$log"; wait_settled "$pid"; return $?; fi
  if grep -qE "$BOOT_RE" "$log"; then
    echo "note: ${SMOKE_WAIT}s 内未见①，按②安装链收工" >&2
    return 0
  fi
  return 1
}

smoke_self_test() { # 纯函数 + wait_url 回归：夹具断言 verdict/落定/心跳/等待循环
  local tdir fail=0 live log rc
  tdir="$(mktemp -d)"
  OUT="$tdir/out"; LOG="$tdir/host.log"; mkdir -p "$tdir/home"
  tpass() { echo "ok: $1"; }
  tfail() { echo "FAIL: $1"; fail=1; }
  echo "[host] dsh web = http://127.0.0.1:1/?token=t" >"$OUT"; : >"$LOG"
  [[ "$(smoke_verdict "$OUT")" == *"full-chain"* ]] && tpass "verdict-full" || tfail "verdict-full"
  : >"$OUT"
  [[ "$(smoke_verdict "$OUT")" == *"install-chain"* ]] && tpass "verdict-install" || tfail "verdict-install"
  printf '[nav] 导航已到达：http://127.0.0.1:1/（origin → x）\n[nav] 导航已到达：http://127.0.0.1:1/?token=t → y\n' >"$OUT"
  sleep 30 & live=$!
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tpass "settle-ok" || tfail "settle-ok"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  printf '[nav] 导航已到达：http://127.0.0.1:1/（origin → x）\n' >"$OUT"; : >"$LOG"
  sleep 30 & live=$!
  SETTLE_WAIT=2 wait_settled "$live" >/dev/null 2>&1 && tfail "settle-timeout-should-fail" || tpass "settle-timeout-fails"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  heartbeat "60" "$tdir/home" 2>&1 | grep -q "等待中（60s）" && tpass "heartbeat" || tfail "heartbeat"
  # smoke_shot：无 DISPLAY 即静默跳过（不建目录不拦冒烟）；fake scrot 开火留痕且非空
  DISPLAY= SMOKE_SHOT_DIR="$tdir/shots" smoke_shot "no.png" >/dev/null 2>&1 \
    && [[ ! -e "$tdir/shots/no.png" ]] && tpass "shot-nodisplay" || tfail "shot-nodisplay"
  mkdir -p "$tdir/fakebin"
  printf '#!/bin/sh\nprintf "PNG" > "$1"\n' >"$tdir/fakebin/scrot"; chmod +x "$tdir/fakebin/scrot"
  PATH="$tdir/fakebin:/usr/bin:/bin" DISPLAY=:99 SMOKE_SHOT_DIR="$tdir/shots" smoke_shot "s.png" >/dev/null 2>&1 \
    && [[ -s "$tdir/shots/s.png" ]] && tpass "shot-fires" || tfail "shot-fires"
  # 主回归：坏 import（存在但落空文件、零退出）不得遮掉可用 scrot（注释空包事故钉死）。
  # fake 须按真 import 语义取末参为输出（取 $1 会把 "-window" 当输出，杂散文件曾落工作区根——自测实证钉死此坑）。
  printf '#!/bin/sh\nfor a in "$@"; do last="$a"; done\n: > "$last"\n' >"$tdir/fakebin/import"; chmod +x "$tdir/fakebin/import"
  rm -f "$tdir/shots/s2.png"
  PATH="$tdir/fakebin:/usr/bin:/bin" DISPLAY=:99 SMOKE_SHOT_DIR="$tdir/shots" smoke_shot "s2.png" >/dev/null 2>&1 \
    && [[ "$(cat "$tdir/shots/s2.png")" == "PNG" ]] && tpass "shot-fallback" || tfail "shot-fallback"
  # wait_url 集成：①+落定 → 0；只有②（短窗）→ 0；双无 → 1；①无落定 → 1
  log="$tdir/w1"; printf '[bootstrap] 引导开始：x\n[host] dsh web = http://127.0.0.1:1/?token=t\n[nav] 导航已到达：http://127.0.0.1:1/\n[nav] 导航已到达：http://127.0.0.1:1/?token=t\n' >"$log"
  sleep 30 & live=$!
  SMOKE_WAIT=5 SETTLE_WAIT=90 wait_url "$log" "$live" "$tdir/home" >/dev/null 2>&1 && tpass "wait_url-full" || tfail "wait_url-full"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  log="$tdir/w2"; printf '[bootstrap] 引导开始：x\n' >"$log"
  sleep 30 & live=$!
  SMOKE_WAIT=2 wait_url "$log" "$live" "$tdir/home" >/dev/null 2>&1 && tpass "wait_url-bootonly" || tfail "wait_url-bootonly"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  log="$tdir/w3"; : >"$log"
  SMOKE_WAIT=2 wait_url "$log" "99999999" "$tdir/home" >/dev/null 2>&1 && tfail "wait_url-nosignal-should-fail" || tpass "wait_url-nosignal-fails"
  log="$tdir/w4"; printf '[bootstrap] 引导开始：x\n[host] dsh web = http://127.0.0.1:1/?token=t\n[nav] 导航已到达：http://127.0.0.1:1/\n' >"$log"
  sleep 30 & live=$!
  SMOKE_WAIT=5 SETTLE_WAIT=2 wait_url "$log" "$live" "$tdir/home" >/dev/null 2>&1 && tfail "wait_url-nosettle-should-fail" || tpass "wait_url-nosettle-fails"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  log="$tdir/w5"; printf '[bootstrap] 引导开始：x\n[host] dsh web = http://127.0.0.1:1/?token=t\n[nav] 导航已到达：http://127.0.0.1:1/\n[nav] 导航已到达：http://127.0.0.1:1/?token=t\n' >"$log"
  SMOKE_WAIT=5 SETTLE_WAIT=90 wait_url "$log" "99999999" "$tdir/home" >/dev/null 2>&1 && tpass "wait_url-exit-settled" || tfail "wait_url-exit-settled"
  # wait_url × 裁决门（deb 腿的真实组合，ADR page-verdict-gate）：①+到达+healthy → 0；到达齐但缺裁决 → 1
  log="$tdir/w6"; printf '[bootstrap] 引导开始：x\n[host] dsh web = http://127.0.0.1:1/?token=t\n[nav] 导航已到达：http://127.0.0.1:1/\n[nav] 导航已到达：http://127.0.0.1:1/?token=t\n[nav] 页面裁决=healthy（origin=http://127.0.0.1:1 可见文本 400 字）\n' >"$log"
  sleep 30 & live=$!
  PAGE_VERDICT_REQUIRED=1
  SMOKE_WAIT=5 SETTLE_WAIT=90 wait_url "$log" "$live" "$tdir/home" >/dev/null 2>&1 && tpass "wait_url-verdict-healthy" || tfail "wait_url-verdict-healthy"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  log="$tdir/w7"; printf '[bootstrap] 引导开始：x\n[host] dsh web = http://127.0.0.1:1/?token=t\n[nav] 导航已到达：http://127.0.0.1:1/\n[nav] 导航已到达：http://127.0.0.1:1/?token=t\n' >"$log"
  sleep 30 & live=$!
  PAGE_VERDICT_REQUIRED=1
  SMOKE_WAIT=5 SETTLE_WAIT=2 wait_url "$log" "$live" "$tdir/home" >/dev/null 2>&1 && tfail "wait_url-verdict-missing-should-fail" || tpass "wait_url-verdict-missing-fails"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  PAGE_VERDICT_REQUIRED=0
  # 落定①后计数（ADR settle-gate-and-probe-retry）：①前双到达不算落定；①后双到达即落定（Linux 只报最终 URL）
  # 注意：此前 wait_url 用例把 OUT/LOG 指走，此处显式复位回自测夹具（settle-ok 先例同理）。
  OUT="$tdir/out"; LOG="$tdir/host.log"
  printf '[nav] 导航已到达：ryn://app/index.html\n[nav] 导航已到达：http://127.0.0.1:1/\n[host] dsh web = http://127.0.0.1:1/?token=t\n' >"$OUT"; : >"$LOG"
  sleep 30 & live=$!
  SETTLE_WAIT=2 wait_settled "$live" >/dev/null 2>&1 && tfail "settle-preready-should-fail" || tpass "settle-preready-fails"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  printf '[host] dsh web = http://127.0.0.1:1/?token=t\n[nav] 导航已到达：ryn://app/index.html\n[nav] 导航已到达：http://127.0.0.1:1/\n[nav] 导航已到达：http://127.0.0.1:1/\n' >"$OUT"; : >"$LOG"
  sleep 30 & live=$!
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tpass "settle-postready" || tfail "settle-postready"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  # 裁决 auth 进门（ADR page-verdict-gate）：到达再多、终页裁决 auth 即失败（不置位也拦，容器腿同门）
  printf '[host] dsh web = http://127.0.0.1:1/?token=t\n[nav] 导航已到达：http://127.0.0.1:1/\n[nav] 导航已到达：http://127.0.0.1:1/?token=t\n[nav] 页面裁决=auth（origin=http://127.0.0.1:1 可见文本 60 字，请重开 dsh 打印的 URL；启动继续）\n' >"$OUT"; : >"$LOG"
  sleep 30 & live=$!
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tfail "settle-auth-should-fail" || tpass "settle-auth-fails"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  # 显示腿裁决门（PAGE_VERDICT_REQUIRED=1）：arrivals + healthy → 0
  printf '[host] dsh web = http://127.0.0.1:1/?token=t\n[nav] 导航已到达：http://127.0.0.1:1/\n[nav] 导航已到达：http://127.0.0.1:1/?token=t\n[nav] 页面裁决=healthy（origin=http://127.0.0.1:1 可见文本 400 字）\n' >"$OUT"; : >"$LOG"
  sleep 30 & live=$!
  PAGE_VERDICT_REQUIRED=1
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tpass "settle-verdict-healthy" || tfail "settle-verdict-healthy"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  # 同门：arrivals + unknown → 1（到达过不算绿）
  printf '[host] dsh web = http://127.0.0.1:1/?token=t\n[nav] 导航已到达：http://127.0.0.1:1/\n[nav] 导航已到达：http://127.0.0.1:1/?token=t\n[nav] 页面裁决=unknown（探针无采样，期望 origin=http://127.0.0.1:1）\n' >"$OUT"; : >"$LOG"
  sleep 30 & live=$!
  PAGE_VERDICT_REQUIRED=1
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tfail "settle-verdict-unknown-should-fail" || tpass "settle-verdict-unknown-fails"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  # 同门：arrivals 齐但裁决行始终不出现（探针未回）→ 1
  printf '[host] dsh web = http://127.0.0.1:1/?token=t\n[nav] 导航已到达：http://127.0.0.1:1/\n[nav] 导航已到达：http://127.0.0.1:1/?token=t\n' >"$OUT"; : >"$LOG"
  sleep 30 & live=$!
  PAGE_VERDICT_REQUIRED=1
  SETTLE_WAIT=2 wait_settled "$live" >/dev/null 2>&1 && tfail "settle-verdict-missing-should-fail" || tpass "settle-verdict-missing-fails"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  # LOG 兜底（宿主腿形态，R2 S5）：OUT 无裁决行、裁决只在 host.log → 仍读得到且 auth 判红
  printf '[host] dsh web = http://127.0.0.1:1/?token=t\n[nav] 导航已到达：http://127.0.0.1:1/\n[nav] 导航已到达：http://127.0.0.1:1/?token=t\n' >"$OUT"
  printf '[nav] 页面裁决=auth（origin=http://127.0.0.1:1 可见文本 60 字，重进后，请重开 dsh 打印的 URL；启动继续）\n' >"$LOG"
  sleep 30 & live=$!
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tfail "settle-log-fallback-auth-should-fail" || tpass "settle-log-fallback-auth-fails"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  : >"$LOG"
  # 只认最后一条：healthy 之后又坏成 auth（页面塌陷）→ 1，旧 healthy 不得冒充绿
  printf '[host] dsh web = http://127.0.0.1:1/?token=t\n[nav] 导航已到达：http://127.0.0.1:1/\n[nav] 导航已到达：http://127.0.0.1:1/?token=t\n[nav] 页面裁决=healthy（origin=http://127.0.0.1:1 可见文本 400 字）\n[nav] 页面裁决=auth（origin=http://127.0.0.1:1 可见文本 60 字，重进后，请重开 dsh 打印的 URL；启动继续）\n' >"$OUT"; : >"$LOG"
  sleep 30 & live=$!
  PAGE_VERDICT_REQUIRED=1
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tfail "settle-verdict-relapse-should-fail" || tpass "settle-verdict-relapse-fails"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  # 只认最后一条：auth 之后重试恢复 healthy → 0（终页确实是 UI）
  printf '[host] dsh web = http://127.0.0.1:1/?token=t\n[nav] 导航已到达：http://127.0.0.1:1/\n[nav] 导航已到达：http://127.0.0.1:1/?token=t\n[nav] 页面裁决=auth（origin=http://127.0.0.1:1 可见文本 60 字，重进后，请重开 dsh 打印的 URL；启动继续）\n[nav] 页面裁决=healthy（origin=http://127.0.0.1:1 可见文本 400 字）\n' >"$OUT"; : >"$LOG"
  sleep 30 & live=$!
  PAGE_VERDICT_REQUIRED=1
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tpass "settle-verdict-recovery" || tfail "settle-verdict-recovery"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  PAGE_VERDICT_REQUIRED=0
  rm -rf "$tdir"
  [[ $fail -eq 0 ]] && echo "self-test: PASS" || echo "self-test: FAIL"
  return $fail
}

if [[ "$SELFTEST" -eq 1 ]]; then
  smoke_self_test
  exit $?
fi

smoke_deb() {
  local deb="$1" log home pid rc apt_log
  log="$(mktemp)"; home="$(mktemp -d)"; apt_log="$(mktemp)"
  echo "== [deb] 安装 $deb"
  sudo apt-get update -qq
  # apt 直接吃绝对路径的 deb 并自动解 Depends（libwebkitgtk-6.0-4 / libadwaita-1-0 等）。
  # DEBIAN_FRONTEND=noninteractive 防 debconf 交互挂死；stdout 留档（装包环节取证，
  # 失败打尾部——与 rpm dnf 同款，曾有 >/dev/null 丢证据的盲区）
  sudo env DEBIAN_FRONTEND=noninteractive apt-get install -y "$deb" >"$apt_log" 2>&1 || {
    echo "error: [deb] apt 安装失败（Depends 解析或包损坏）。apt 输出尾部：" >&2
    tail -30 "$apt_log" >&2
    rm -rf "$home" "$log" "$apt_log"
    return 1
  }
  tail -3 "$apt_log" >&2 || true
  rm -f "$apt_log"
  echo "== [deb] 启动冒烟（等①就绪后等导航落定，②保底；窗=${SMOKE_WAIT}s/落定${SETTLE_WAIT}s；DISPLAY=${DISPLAY:-<无>}）"
  set +e
  # 无人值守跳过可选插件（ADR preinstall-unattended-skip）：CI 无人点选，省 5 分钟决策等待。
  env DSH_DESKTOP_DSH_HOME="$home" DEEPSEEK_API_KEY=placeholder DSH_DESKTOP_PREINSTALL_AUTO=skip \
    timeout "$APP_TIMEOUT" "$APP_BIN" >"$log" 2>&1 &
  pid=$!
  # 动态作用域：wait_url→wait_settled 读得到；钉在本次调用内，不外泄给后续腿（R2 S3）。
  local PAGE_VERDICT_REQUIRED=1
  wait_url "$log" "$pid" "$home"; rc=$?
  # 裁决已过再等有界重绘窗：提交回调早于新页出像素，立刻拍会拍到上一跳（401）旧帧；无显示不睡。
  if [[ $rc -eq 0 && -n "${DISPLAY:-}" ]]; then
    sleep "$SMOKE_REPAINT_SECONDS"
    kill -0 "$pid" 2>/dev/null || echo "note: 重绘窗内应用已退出，截图可能为空窗（rc 仍按落定结论）" >&2
  fi
  smoke_shot "smoke-linux-deb.png"
  kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true
  set -e
  sudo apt-get remove -y deepseek-harness-desktop >/dev/null 2>&1 || sudo dpkg -r deepseek-harness-desktop >/dev/null 2>&1 || true
  if [[ $rc -ne 0 ]]; then
    # 现场必须落进 CI 日志：应用秒退时 stderr 是唯一定位线索（arm64 首跑实证）
    echo "error: [deb] 冒烟失败。日志尾部：" >&2
    cat "$log" >&2
  else
    smoke_verdict "$log"
    # 成功也留尾（ADR verdict-honesty-repair）：绿跑的导航/探针/自愈行此前随日志删除，
    # "绿即无证"致 401 绿 verdict 无从复核；30 行覆盖导航段（仓内尾部惯例）。
    echo "== [deb] 冒烟通过，应用日志尾部（内容判定留痕）：" >&2
    tail -30 "$log" >&2 || true
  fi
  rm -rf "$home" "$log"
  [[ $rc -eq 0 ]]
}

smoke_rpm_container() {
  local rpm_path="$1" base
  base="$(basename "$rpm_path")"
  echo "== [rpm] fedora 容器安装冒烟: $base"
  # 容器内 root + 无 display：判定走双信号（见文件头），引导启动行先于窗口创建输出。
  # heredoc 用引号界定符：宿主变量经 docker -e 显式注入，容器侧 $ 一律保持字面——
  # 未加引号版本曾被宿主 set -u 撞上容器变量（$log 未定义）直接炸掉 rpm 路径（CI 实证）。
  docker run --rm -i \
    -v "$PKG_DIR:/pkg:ro" \
    -v "$LIB:/smoke-lib.sh:ro" \
    -e SMOKE_PKG_NAME="$base" \
    -e SMOKE_APP_BIN="$APP_BIN" \
    -e SMOKE_WAIT="$SMOKE_WAIT" \
    -e SMOKE_SETTLE_SECONDS="${SMOKE_SETTLE_SECONDS:-90}" \
    -e PASS_RE="$PASS_RE" \
    -e FULL_RE="$FULL_RE" \
    -e BOOT_RE="$BOOT_RE" \
    -e APP_TIMEOUT="$APP_TIMEOUT" \
    fedora:44 bash -s <<'INNER'
# 刻意不带 -e：dnf 失败走显式分支打印包安装诊断，而非无声退出
set -uo pipefail
log=/tmp/smoke.log
SETTLE_WAIT="${SMOKE_SETTLE_SECONDS:-90}"
OUT="$log"; LOG=""
# 落定等待与宿主侧同一实现（R1：禁止手抄复刻，挂载 + source 共享库）
# shellcheck disable=SC1091
source /smoke-lib.sh
if ! dnf install -y --setopt=install_weak_deps=False "/pkg/$SMOKE_PKG_NAME" >"$log" 2>&1; then
  echo "error: [rpm] dnf 安装失败（显式 Requires 不满足或包损坏）："
  tail -30 "$log" >&2
  exit 1
fi
home=$(mktemp -d)
# 无人值守跳过可选插件（同上，容器内同样无人点选）。
timeout "$APP_TIMEOUT" env DSH_DESKTOP_DSH_HOME="$home" DEEPSEEK_API_KEY=placeholder DSH_DESKTOP_PREINSTALL_AUTO=skip \
  "$SMOKE_APP_BIN" >"$log" 2>&1 &
pid=$!
OUT="$log"; LOG=""
boot_seen=0; start="$SECONDS"
rc=1
# 与宿主侧 wait_url 同款语义：②命中后继续等①至超时/退出，①命中后等导航落定（ADR smoke-settle-content-verdict）
for _ in $(seq 1 "$SMOKE_WAIT"); do
  if grep -qE "$FULL_RE" "$log"; then
    grep -m1 -E "$FULL_RE" "$log"
    if wait_settled "$pid"; then
      echo "SMOKE_VERDICT=full-chain（dsh web 就绪）"
      kill $pid 2>/dev/null; exit 0
    else
      tail -30 "$log" >&2
      kill $pid 2>/dev/null; exit 1
    fi
  fi
  if [[ "${boot_seen:-0}" -eq 0 ]] && grep -qE "$BOOT_RE" "$log"; then
    boot_seen=1
    grep -m1 -E "$BOOT_RE" "$log"
    echo "note: 已见②安装链，继续等①至超时/退出…" >&2
  fi
  if ! kill -0 $pid 2>/dev/null; then
    if grep -qE "$FULL_RE" "$log"; then
      grep -m1 -E "$FULL_RE" "$log"
      if wait_settled ""; then echo "SMOKE_VERDICT=full-chain（dsh web 就绪）"; exit 0; fi
      tail -30 "$log" >&2; exit 1
    fi
    if grep -qE "$BOOT_RE" "$log"; then echo "note: 进程已退出，未见①，按②安装链收工" >&2; echo "SMOKE_VERDICT=install-chain（仅引导启动）"; exit 0; fi
    break
  fi
  elapsed=$((SECONDS - start)); [[ $((elapsed % 60)) -eq 0 ]] && heartbeat "$elapsed" "$home"
  sleep 1
done
if grep -qE "$FULL_RE" "$log"; then grep -m1 -E "$FULL_RE" "$log"; if wait_settled "$pid"; then echo "SMOKE_VERDICT=full-chain（dsh web 就绪）"; kill $pid 2>/dev/null; exit 0; fi; tail -30 "$log" >&2; kill $pid 2>/dev/null; exit 1; fi
if grep -qE "$BOOT_RE" "$log"; then echo "note: ${SMOKE_WAIT}s 内未见①，按②安装链收工" >&2; echo "SMOKE_VERDICT=install-chain（仅引导启动）"; kill $pid 2>/dev/null; exit 0; fi
echo "error: [rpm] 冒烟失败——${SMOKE_WAIT}s 内未出现 dsh web URL 或引导启动行。尾部："
tail -30 "$log" >&2
kill $pid 2>/dev/null
exit 1
INNER
}

found=0
rc_total=0
DEB="$(find "$PKG_DIR" -maxdepth 1 -name '*.deb' | head -1 || true)"
RPM="$(find "$PKG_DIR" -maxdepth 1 -name '*.rpm' | head -1 || true)"

if [[ -n "$RPM" ]]; then
  found=1
  if ! command -v docker >/dev/null 2>&1; then
    echo "error: 需要 docker 运行 rpm 冒烟（GitHub ubuntu runner 预装；本地请自行安装）" >&2
    rc_total=1
  else
    smoke_rpm_container "$RPM" || rc_total=1
  fi
fi

if [[ -n "$DEB" ]]; then
  found=1
  smoke_deb "$DEB" || rc_total=1
fi

[[ $found -eq 1 ]] || { echo "error: $PKG_DIR 下未找到 deb/rpm 产物" >&2; exit 1; }
exit $rc_total
