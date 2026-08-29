#!/usr/bin/env bash
# smoke-install-linux.sh — Linux 安装冒烟（批次一，ADR artifact-verification-chain；
# online-first 批次二起覆盖「装包 → 首启引导 → dsh web URL」全链）。
# 对构建产物目录中的 deb/rpm 做「干净环境装包 → 启动 → 等 dsh web URL」验证：
#   deb → runner 原生 apt 安装（真实解析 Depends）
#   rpm → fedora 容器内 dnf 安装（AutoReqProv:no 的显式 Requires 是否够，装了才知道）
# 判定信号（双信号，命中其一即 PASS）：
#   ①`[host] dsh web =`（注意是等号——`dsh web:` 冒号格式是 dsh 子进程自检输出，壳打印的是等号格式；首版判定串错位致冒烟恒败，CI 实证）= 全链 PASS（装包→引导→dsh web 就绪）；
#   ②`[bootstrap] 引导开始：` = 安装链 PASS（装包→依赖齐→运行时检测→首启引导已启动）。
#     CI 的无显示环境壳必然在窗口创建（Ryn Run）即退出（GTK 需 display，已记录边界），
#     引导是后台任务会随之夭折——全链信号在 CI 不可达，②为 CI 判定位；①在真桌面/
#     有显示环境命中。引导下载/安装全链的验证在实机验收转交（批次一沙箱 E2E 已通）。
# 直击事故类：v0.2.x「rpm 实机装不上」、libadwaita 缺依赖崩溃（2026-08-29 冒烟暴露，
# deb/rpm 已补显式声明）+ online-first「引导断链、dsh 起不来」。
#
# 用法: smoke-install-linux.sh <产物目录（含 *.deb 与/或 *.rpm）>
set -euo pipefail

PKG_DIR="${1:?usage: smoke-install-linux.sh <dir-with-deb/rpm>}"
[[ -d "$PKG_DIR" ]] || { echo "error: 目录不存在: $PKG_DIR" >&2; exit 1; }
PKG_DIR="$(realpath "$PKG_DIR")"
APP_BIN="/usr/bin/deepseek-harness-desktop"

# 等待启动日志出现 [host] dsh web = 的公共循环。进程探活用 kill -0 <pid>：
# 安装后的入口是小写符号链接（/usr/bin/deepseek-harness-desktop），pgrep 按
# 大写二进制名匹配会立刻误判「进程已死」。进程退出后再补扫一次日志，兜住
# 「URL 已打出但进程随即退出」的窗口。
# 等待窗与引导步超时强耦合：RuntimeBootstrapOptions.StepTimeoutMinutes（默认 10 分钟）
# 是**每步**上限（Node 下载/解压/npm 装树各一），等待窗必须 ≥ 步数×步超时+启动余量
# ——rpm 容器无 node 走全链（下载+装树 = 2 步），故默认 2×600+120=1320s。
# 引导步数或 StepTimeoutMinutes 变化时必须同批重算。SMOKE_WAIT_SECONDS 可覆写。
SMOKE_WAIT="${SMOKE_WAIT_SECONDS:-1320}"
APP_TIMEOUT=$((SMOKE_WAIT + 20))
PASS_RE='\[host\] dsh web =|\[bootstrap\] 引导开始：'
wait_url() { # $1=日志 $2=pid
  local log="$1" pid="$2"
  for _ in $(seq 1 "$SMOKE_WAIT"); do
    if grep -qE "$PASS_RE" "$log"; then
      grep -m1 -E "$PASS_RE" "$log"
      return 0
    fi
    if ! kill -0 "$pid" 2>/dev/null; then
      grep -qE "$PASS_RE" "$log" && { grep -m1 -E "$PASS_RE" "$log"; return 0; }
      return 1
    fi
    sleep 1
  done
  return 1
}

smoke_deb() {
  local deb="$1" log home pid rc
  log="$(mktemp)"; home="$(mktemp -d)"
  echo "== [deb] 安装 $deb"
  sudo apt-get update -qq
  # apt 直接吃绝对路径的 deb 并自动解 Depends（libwebkitgtk-6.0-4 / libadwaita-1-0 等）
  sudo apt-get install -y "$deb" >/dev/null
  echo "== [deb] 启动冒烟（等 dsh web URL 行或引导启动行）"
  set +e
  env DSH_DESKTOP_DSH_HOME="$home" DEEPSEEK_API_KEY=placeholder \
    timeout "$APP_TIMEOUT" "$APP_BIN" >"$log" 2>&1 &
  pid=$!
  wait_url "$log" "$pid"; rc=$?
  kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true
  set -e
  sudo apt-get remove -y deepseek-harness-desktop >/dev/null 2>&1 || sudo dpkg -r deepseek-harness-desktop >/dev/null 2>&1 || true
  if [[ $rc -ne 0 ]]; then
    # 现场必须落进 CI 日志：应用秒退时 stderr 是唯一定位线索（arm64 首跑实证）
    echo "error: [deb] 冒烟失败——${SMOKE_WAIT}s 内未出现 dsh web URL 或引导启动行。日志尾部：" >&2
    cat "$log" >&2
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
    -e SMOKE_PKG_NAME="$base" \
    -e SMOKE_APP_BIN="$APP_BIN" \
    -e SMOKE_WAIT="$SMOKE_WAIT" \
    -e PASS_RE="$PASS_RE" \
    -e APP_TIMEOUT="$APP_TIMEOUT" \
    fedora:44 bash -s <<'INNER'
# 刻意不带 -e：dnf 失败走显式分支打印包安装诊断，而非无声退出
set -uo pipefail
log=/tmp/smoke.log
if ! dnf install -y --setopt=install_weak_deps=False "/pkg/$SMOKE_PKG_NAME" >"$log" 2>&1; then
  echo "error: [rpm] dnf 安装失败（显式 Requires 不满足或包损坏）："
  tail -30 "$log" >&2
  exit 1
fi
home=$(mktemp -d)
timeout "$APP_TIMEOUT" env DSH_DESKTOP_DSH_HOME="$home" DEEPSEEK_API_KEY=placeholder \
  "$SMOKE_APP_BIN" >"$log" 2>&1 &
pid=$!
# 与宿主侧 wait_url 同款探活：进程秒退时立即失败，不空转满等待窗；
# 双信号同款（dsh web URL 行或引导启动行）
for _ in $(seq 1 "$SMOKE_WAIT"); do
  if grep -qE "$PASS_RE" "$log"; then
    grep -m1 -E "$PASS_RE" "$log"; kill $pid 2>/dev/null; exit 0
  fi
  if ! kill -0 $pid 2>/dev/null; then
    grep -qE "$PASS_RE" "$log" && { grep -m1 -E "$PASS_RE" "$log"; exit 0; }
    break
  fi
  sleep 1
done
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
