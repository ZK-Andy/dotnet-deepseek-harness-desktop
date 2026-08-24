#!/usr/bin/env bash
# smoke-install-linux.sh — Linux 安装冒烟（批次一，ADR artifact-verification-chain）。
# 对构建产物目录中的 deb/rpm 做「干净环境装包 → 启动 → 等 dsh web URL」全链路验证：
#   deb → runner 原生 apt 安装（真实解析 Depends）
#   rpm → fedora 容器内 dnf 安装（AutoReqProv:no 的显式 Requires 是否够，装了才知道）
# 判定信号 = 桌面程序 stdout 的 `[host] dsh web =` 行（注意是等号——`dsh web:` 冒号格式是 dsh 子进程自检的输出，壳进程打印的是等号格式；首版判定串错位致冒烟恒败，CI 实证）。
# 无需 display 即可证明「包装得上、依赖齐、运行时定位成功、捆绑闭包能起 dsh」。
# 直击事故类：v0.2.x「rpm 实机装不上 / 闭包残缺 dsh 起不来」。
#
# 用法: smoke-install-linux.sh <产物目录（含 *.deb 与/或 *.rpm）>
set -euo pipefail

PKG_DIR="${1:?usage: smoke-install-linux.sh <dir-with-deb/rpm>}"
[[ -d "$PKG_DIR" ]] || { echo "error: 目录不存在: $PKG_DIR" >&2; exit 1; }
PKG_DIR="$(realpath "$PKG_DIR")"
APP_BIN="/usr/bin/deepseek-harness-desktop"

# 等待启动日志出现 [host] dsh web = 的公共循环（90×1s）。进程探活用 kill -0 <pid>：
# 安装后的入口是小写符号链接（/usr/bin/deepseek-harness-desktop），pgrep 按
# 大写二进制名匹配会立刻误判「进程已死」。进程退出后再补扫一次日志，兜住
# 「URL 已打出但进程随即退出」的窗口。
wait_url() { # $1=日志 $2=pid
  local log="$1" pid="$2"
  for _ in $(seq 1 90); do
    if grep -q "\[host\] dsh web =" "$log"; then
      grep -m1 "\[host\] dsh web =" "$log"
      return 0
    fi
    if ! kill -0 "$pid" 2>/dev/null; then
      grep -q "\[host\] dsh web =" "$log" && { grep -m1 "\[host\] dsh web =" "$log"; return 0; }
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
  # apt 直接吃绝对路径的 deb 并自动解 Depends（libwebkitgtk-6.0-4 等）
  sudo apt-get install -y "$deb" >/dev/null
  echo "== [deb] 启动冒烟（等 dsh web URL 行）"
  set +e
  env DSH_DESKTOP_DSH_HOME="$home" DEEPSEEK_API_KEY=placeholder \
    timeout 100 "$APP_BIN" >"$log" 2>&1 &
  pid=$!
  wait_url "$log" "$pid"; rc=$?
  kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true
  set -e
  sudo apt-get remove -y deepseek-harness-desktop >/dev/null 2>&1 || sudo dpkg -r deepseek-harness-desktop >/dev/null 2>&1 || true
  if [[ $rc -ne 0 ]]; then
    # 现场必须落进 CI 日志：应用秒退时 stderr 是唯一定位线索（arm64 首跑实证）
    echo "error: [deb] 冒烟失败——90s 内未出现 [host] dsh web =。日志尾部：" >&2
    cat "$log" >&2 || true
  fi
  rm -rf "$home" "$log"
  [[ $rc -eq 0 ]]
}

smoke_rpm_container() {
  local rpm_path="$1" base
  base="$(basename "$rpm_path")"
  echo "== [rpm] fedora 容器安装冒烟: $base"
  # 容器内 root + 无 display：URL 行在窗口创建前输出，判定不受影响。
  # heredoc 用引号界定符：宿主变量经 docker -e 显式注入，容器侧 $ 一律保持字面——
  # 未加引号版本曾被宿主 set -u 撞上容器变量（$log 未定义）直接炸掉 rpm 路径（CI 实证）。
  docker run --rm -i \
    -v "$PKG_DIR:/pkg:ro" \
    -e SMOKE_PKG_NAME="$base" \
    -e SMOKE_APP_BIN="$APP_BIN" \
    fedora:41 bash -s <<'INNER'
# 刻意不带 -e：dnf 失败走显式分支打印包安装诊断，而非无声退出
set -uo pipefail
log=/tmp/smoke.log
if ! dnf install -y --setopt=install_weak_deps=False "/pkg/$SMOKE_PKG_NAME" >"$log" 2>&1; then
  echo "error: [rpm] dnf 安装失败（显式 Requires 不满足或包损坏）："
  tail -30 "$log" >&2
  exit 1
fi
home=$(mktemp -d)
timeout 100 env DSH_DESKTOP_DSH_HOME="$home" DEEPSEEK_API_KEY=placeholder \
  "$SMOKE_APP_BIN" >"$log" 2>&1 &
pid=$!
# 与宿主侧 wait_url 同款探活：进程秒退时立即失败，不空转满 90s
for _ in $(seq 1 90); do
  if grep -q "\[host\] dsh web =" "$log"; then
    grep -m1 "\[host\] dsh web =" "$log"; kill $pid 2>/dev/null; exit 0
  fi
  if ! kill -0 $pid 2>/dev/null; then
    grep -q "\[host\] dsh web =" "$log" && { grep -m1 "\[host\] dsh web =" "$log"; exit 0; }
    break
  fi
  sleep 1
done
echo "error: [rpm] 冒烟失败——90s 内未出现 [host] dsh web =。尾部："
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
