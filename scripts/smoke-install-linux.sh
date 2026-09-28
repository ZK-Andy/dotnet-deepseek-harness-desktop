#!/usr/bin/env bash
# smoke-install-linux.sh — Linux 安装冒烟入口（ADR artifact-verification-chain）。
# 对构建产物目录中的 deb/rpm 做「干净环境装包 → 启动 → 等 dsh web URL」验证：
#   deb → runner 原生 apt 安装（真实解析 Depends）——本脚本
#   rpm → fedora 容器内 dnf 安装（AutoReqProv:no 的显式 Requires 是否够，装了才知道）
#                                       ——scripts/smoke-linux-rpm.sh（本脚本按产物存在转调）
# 判定信号（双信号）：
#   ①`[host] dsh web =`（注意是等号——`dsh web:` 冒号格式是 dsh 子进程自检输出，壳打印的是等号格式；首版判定串错位致冒烟恒败，CI 实证）= dsh 就绪（传输层）；
#   ②`[bootstrap] 引导开始：` = 安装链保底（装包→依赖齐→运行时检测→首启引导已启动）。
# 等待/落定语义、心跳、看门狗、回退门、缺信号摘要在 scripts/lib/smoke-wait-lib.sh（唯一家；
# 三平台与容器腿同一实现）。裁决门（auth 硬拦）与截图内容见证在 scripts/lib/smoke-verdict-lib.sh。
#     deb 腿（有显示）在此之上只加截图时机与见证：落定后再等一个有界重绘窗
#     （SMOKE_REPAINT_SECONDS，默认 3s）才拍，避免拍到上一跳的旧像素；见证判红（近空白/
#     深色页）后仅客户端存活可兜底，两者俱缺才 FAIL（见 smoke_deb）。
#     CI 经 xvfb-run 启动（ADR smoke-linux-xvfb-fullchain）：虚拟 DISPLAY 下窗口可创建，
#     引导后台任务存活——deb 腿全链信号可达，落定 verdict + 截图真实开火；Xvfb 起不来
#     或无显示直跑仍回退②安装链（回退门语义不变）。rpm 容器腿无 X，恒②。
#     引导下载/安装全链的验证在实机验收转交（批次一沙箱 E2E 已通）。
# 直击事故类：v0.2.x「rpm 实机装不上」、libadwaita 缺依赖崩溃（2026-08-29 冒烟暴露，
# deb/rpm 已补显式声明）+ online-first「引导断链、dsh 起不来」。
#
# 用法: smoke-install-linux.sh <产物目录（含 *.deb 与/或 *.rpm）>
# 自测: smoke-install-linux.sh --self-test（纯函数 + 等待循环回归，不碰装包）
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/smoke-verdict-lib.sh
source "$SCRIPT_DIR/lib/smoke-verdict-lib.sh"
# shellcheck source=lib/smoke-selftest-verdict.sh
source "$SCRIPT_DIR/lib/smoke-selftest-verdict.sh"

SELFTEST=0
if [[ "${1:-}" == "--self-test" ]]; then SELFTEST=1; fi
if [[ "$SELFTEST" -eq 0 ]]; then
  PKG_DIR="${1:?usage: smoke-install-linux.sh <dir-with-deb/rpm>}"
  [[ -d "$PKG_DIR" ]] || die "目录不存在: $PKG_DIR"
  PKG_DIR="$(realpath "$PKG_DIR")"
fi
APP_BIN="/usr/bin/deepseek-harness-desktop"

# 等待窗/落定窗（含 SMOKE_WAIT_SECONDS / SMOKE_SETTLE_SECONDS 覆写）与判定串 ①②
# 的唯一家在共享库，此处只做一次解析（漏调即 set -u 炸——见 smoke-wait-lib 注释）。
smoke_resolve_windows
# 裁决后重绘窗（秒）：WebKit 提交回调早于新页出像素，裁决一过立刻拍易拍到上一跳旧帧
# （ADR page-verdict-gate）；无显示时 smoke_shot 本就早退，不睡。非数字按默认。
# SMOKE_REPAINT_SECONDS 可覆写。
SMOKE_REPAINT_SECONDS="${SMOKE_REPAINT_SECONDS:-3}"
[[ "$SMOKE_REPAINT_SECONDS" =~ ^[0-9]+$ ]] || SMOKE_REPAINT_SECONDS=3
APP_TIMEOUT=$((SMOKE_WAIT + 20))

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
    log "截图已存（${fired}）：${shot}（$(file_size "$shot") 字节）"
  else
    log "截图失败（import/scrot/gnome-screenshot 均无或全败）"
  fi
}

# 截图单工具封顶（R2 轻审）：DISPLAY 存在但 X 失联时坏工具若阻塞会拖住 kill/收尾；
# timeout 存在即 20s 封顶（runner 有；缺则直跑，不引入新依赖）。
shot_capped() {
  if command -v timeout >/dev/null 2>&1; then timeout 20 "$@"; else "$@"; fi
}

# Linux 专属夹具（共用面在 scripts/lib/smoke-selftest.sh）：截图工具级联的三条回归锁。
smoke_selftest_platform() { # $1=夹具根
  local tdir="$1"
  DISPLAY='' SMOKE_SHOT_DIR="$tdir/shots" smoke_shot "no.png" >/dev/null 2>&1 \
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
}

if [[ "$SELFTEST" -eq 1 ]]; then
  smoke_self_test_run smoke_selftest_platform
  exit $?
fi

smoke_deb() {
  local deb="$1" log home pid rc apt_log
  log="$(common_tmp_file)"; home="$(common_tmp_dir)"; apt_log="$(common_tmp_file)"
  echo "== [deb] 安装 $deb"
  sudo apt-get update -qq
  # apt 直接吃绝对路径的 deb 并自动解 Depends（libwebkitgtk-6.0-4 / libadwaita-1-0 等）。
  # DEBIAN_FRONTEND=noninteractive 防 debconf 交互挂死；stdout 留档（装包环节取证，
  # 失败打尾部——与 rpm dnf 同款，曾有 >/dev/null 丢证据的盲区）
  # shellcheck disable=SC2024  # 重定向由调用 shell 执行，$apt_log 是当前用户可写的 mktemp 文件，无需 tee
  sudo env DEBIAN_FRONTEND=noninteractive apt-get install -y "$deb" >"$apt_log" 2>&1 || {
    error "[deb] apt 安装失败（Depends 解析或包损坏）。apt 输出尾部："
    tail -30 "$apt_log" >&2
    return 1
  }
  tail -3 "$apt_log" >&2 || true
  # WebKit 版本留痕（arm64 原生 hang 三选一诊断：saucer arm64 库 / WebKitGTK 构建 / runner 环境）。
  dpkg -l 2>/dev/null | grep -i -m 5 webkit >&2 || true
  echo "== [deb] 启动冒烟（等①就绪后等导航落定，②保底；窗=${SMOKE_WAIT}s/落定${SETTLE_WAIT}s；DISPLAY=${DISPLAY:-<无>}）"
  set +e
  smoke_unattended_env "$home"
  timeout "$APP_TIMEOUT" "$APP_BIN" >"$log" 2>&1 &
  pid=$!
  smoke_wait_ready "$log" "$log" "$pid" "$home"; rc=$?
  # 裁决已过再等有界重绘窗：提交回调早于新页出像素，立刻拍会拍到上一跳（401）旧帧；无显示不睡。
  if [[ $rc -eq 0 && -n "${DISPLAY:-}" ]]; then
    sleep "$SMOKE_REPAINT_SECONDS"
    kill -0 "$pid" 2>/dev/null || log "重绘窗内应用已退出，截图可能为空窗（rc 仍按落定结论）"
  fi
  smoke_shot "smoke-linux-deb.png"
  if [[ $rc -eq 0 && -n "${DISPLAY:-}" && -n "${SMOKE_SHOT_DIR:-}" ]]; then
    if smoke_capture_witness "$SMOKE_SHOT_DIR/smoke-linux-deb.png"; then
      :
    elif smoke_client_alive "$home/logs/host.log"; then
      log "像素偏白但客户端存活（代理 200 RPC/SSE ≥3，浅色主题像素不可分），按活判过"
    else
      rc=1
    fi
  fi
  kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true
  set -e
  sudo apt-get remove -y deepseek-harness-desktop >/dev/null 2>&1 || sudo dpkg -r deepseek-harness-desktop >/dev/null 2>&1 || true
  if [[ $rc -ne 0 ]]; then
    # 现场必须落进 CI 日志：应用秒退时 stderr 是唯一定位线索（arm64 首跑实证）
    error "[deb] 冒烟失败"
    smoke_evidence_fail "$log" "$log" full
  else
    # 成功也留尾（ADR verdict-honesty-repair）：绿跑的导航/探针/自愈行此前随日志删除，
    # "绿即无证"致 401 绿 verdict 无从复核；本腿取 30 行覆盖导航段（仓内尾部惯例）。
    smoke_evidence_pass "$log" "$log" "" 30
  fi
  # 日志落盘（W2）：SMOKE_LOG_DIR 由调用方注入稳定目录，CI 传 artifact。
  smoke_dump_logs "smoke-linux-deb" "$log" "$home/logs/host.log"
  # 终态不断言哑巴：函数返回值即腿结论（调用方 `|| rc_total=1` 靠它判红），
  # 裸 `[[ ]]` 失败无任何输出——红了只能靠 verdict 猜（dispatch 36363806570
  # linux 双腿实证：full-chain + 15ms 后静默 exit 1）。
  if [[ $rc -ne 0 ]]; then
    error "[deb] 腿结论红（rc=$rc，判定证据见上文 PASS/FAIL 段）"
    return 1
  fi
  return 0
}

common_tmp_trap
found=0
rc_total=0
DEB="$(find "$PKG_DIR" -maxdepth 1 -name '*.deb' | head -1 || true)"
RPM="$(find "$PKG_DIR" -maxdepth 1 -name '*.rpm' | head -1 || true)"

if [[ -n "$RPM" ]]; then
  found=1
  if ! command -v docker >/dev/null 2>&1; then
    error "需要 docker 运行 rpm 冒烟（GitHub ubuntu runner 预装；本地请自行安装）"
    rc_total=1
  else
    # 退出码留痕：只 `|| rc_total=1` 不记数，deb/rpm 双腿时红了分不清谁（dispatch
    # 36363806570 linux 双腿排查实证）；行为不变，非零仍只记 rc_total。
    rpm_rc=0
    SMOKE_PKG_DIR="$PKG_DIR" bash "$SCRIPT_DIR/smoke-linux-rpm.sh" "$RPM" || rpm_rc=$?
    if [[ $rpm_rc -ne 0 ]]; then
      error "[linux] rpm 容器腿退出码 $rpm_rc（结论行见上文；②收工亦须 0）"
      rc_total=1
    fi
  fi
fi

if [[ -n "$DEB" ]]; then
  found=1
  smoke_deb "$DEB" || rc_total=1
fi

[[ $found -eq 1 ]] || die "$PKG_DIR 下未找到 deb/rpm 产物"
# 退出前汇总（fail-loud）：静默翻红时唯一能区分「腿结论红」与「wrapper/trap 翻转」的行。
echo "== [linux] 腿汇总：rc_total=$rc_total（0 即全绿；非零见上文 error 行）" >&2
exit $rc_total
