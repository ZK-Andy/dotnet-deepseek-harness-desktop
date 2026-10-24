#!/usr/bin/env bash
# smoke-linux-rpm.sh — Linux rpm 冒烟容器腿（fedora 内 dnf 装包 → 启动 → 等判定信号）。
# 由 smoke-install-linux.sh 在产物目录含 *.rpm 时转调；宿主只起容器与挂载，容器内脚本
# 是 scripts/smoke-linux-rpm-inner.sh（单独文件→可 lint / 可评审，非 heredoc 字面块）。
# 容器内 root + 无 display：inner 自装 Xvfb 提供虚拟显示（Avalonia 启动即初始化 X11、
# 无显示 fail loud——run 40109768884 三红腿之一），判定走双信号 + 行为落定；
# 无截图面（见证门不适用）。
#
# 自测旋钮：STUB_DOCKER_RC（--self-test 里桩 docker 的退出码，缺省 0）。
# 用法: SMOKE_PKG_DIR=<产物目录> smoke-linux-rpm.sh <rpm 路径>
# 自测: smoke-linux-rpm.sh --self-test（离线夹具：docker 桩记录 argv，断容器命令面与退出码透传）
# 环境：SMOKE_WAIT_SECONDS / SMOKE_SETTLE_SECONDS 可覆写窗口；SMOKE_LOG_DIR 置时落盘日志。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/smoke-verdict-lib.sh
source "$SCRIPT_DIR/lib/smoke-verdict-lib.sh"
# 离线夹具：注入 docker 桩（记录 argv），把容器命令面与退出码透传钉住——否则这条腿
# 只有真产物 + 真 docker 才被执行过。
smoke_rpm_self_test() {
  common_tmp_trap
  local tmp bin calls fails=0 rc out
  tmp="$(common_tmp_dir)"
  bin="$tmp/bin"
  calls="$tmp/calls"
  mkdir -p "$bin" "$tmp/pkg" "$tmp/logs"
  : >"$tmp/pkg/app.rpm"
  : >"$calls"
  cat >"$bin/docker" <<'STUB'
#!/usr/bin/env bash
printf 'docker %s\n' "$*" >>"$STUB_CALLS"
exit "${STUB_DOCKER_RC:-0}"
STUB
  chmod +x "$bin/docker"
  _expect_rc() { # $1=描述 $2=期望 rc $3=实得 rc
    if [[ "$2" -ne "$3" ]]; then
      echo "FAIL: $1（期望 rc=$2，实得 rc=$3）" >&2
      fails=$((fails + 1))
    else
      echo "ok: $1"
    fi
  }
  _expect_line() { # $1=描述 $2=期望子串
    if grep -qF -- "$2" "$calls"; then
      echo "ok: $1"
    else
      echo "FAIL: $1（未见到「$2」）" >&2
      fails=$((fails + 1))
    fi
  }

  rc=0
  PATH="$bin:$PATH" STUB_CALLS="$calls" SMOKE_PKG_DIR="$tmp/pkg" SMOKE_LOG_DIR="$tmp/logs" \
    bash "${BASH_SOURCE[0]}" "$tmp/pkg/app.rpm" >/dev/null 2>&1 || rc=$?
  _expect_rc "docker 腿放行" 0 "$rc"
  _expect_line "docker run 有 --rm" "docker run --rm"
  _expect_line "产物目录只读挂载" "-v $tmp/pkg:/pkg:ro"
  _expect_line "共享库挂载" "-v $SCRIPT_DIR/lib:/smoke-lib:ro"
  _expect_line "inner 脚本挂载" "-v $SCRIPT_DIR/smoke-linux-rpm-inner.sh:/smoke-inner.sh:ro"
  _expect_line "日志目录可写挂载" "-v $tmp/logs:/smokelogs:rw"
  _expect_line "包名透传" "-e SMOKE_PKG_NAME=app.rpm"
  _expect_line "应用路径透传" "-e SMOKE_APP_BIN=/usr/bin/deepseek-harness-desktop"
  _expect_line "镜像与入口" "fedora:44 bash /smoke-inner.sh"

  rc=0
  out="$(PATH="$bin:$PATH" STUB_CALLS="$calls" STUB_DOCKER_RC=7 SMOKE_PKG_DIR="$tmp/pkg" \
    SMOKE_LOG_DIR="$tmp/logs" bash "${BASH_SOURCE[0]}" "$tmp/pkg/app.rpm" 2>&1)" || rc=$?
  _expect_rc "容器非零退出码原样透传" 7 "$rc"
  [[ "$out" == *"容器退出码 7"* ]] || { echo "FAIL: 非零退出缺留痕" >&2; fails=$((fails + 1)); }

  rc=0
  out="$(PATH="$bin:$PATH" STUB_CALLS="$calls" SMOKE_PKG_DIR="$tmp/pkg" \
    bash "${BASH_SOURCE[0]}" 2>&1)" || rc=$?
  _expect_rc "缺 rpm 参数即 fail loud" 1 "$rc"
  [[ "$out" == *"usage:"* ]] || { echo "FAIL: 缺 rpm 参数缺用法提示" >&2; fails=$((fails + 1)); }

  rc=0
  out="$(env -u SMOKE_PKG_DIR PATH="$bin:$PATH" STUB_CALLS="$calls" \
    bash "${BASH_SOURCE[0]}" "$tmp/pkg/app.rpm" 2>&1)" || rc=$?
  _expect_rc "缺 SMOKE_PKG_DIR 即 fail loud" 1 "$rc"
  [[ "$out" == *"usage:"* ]] || { echo "FAIL: 缺 SMOKE_PKG_DIR 缺用法提示" >&2; fails=$((fails + 1)); }

  if [[ $fails -eq 0 ]]; then
    echo "self-test: PASS"
    return 0
  fi
  echo "self-test: FAIL（$fails 项）" >&2
  return 1
}

if [[ "${1:-}" == "--self-test" ]]; then
  smoke_rpm_self_test
  exit $?
fi

RPM_PATH="${1:?usage: SMOKE_PKG_DIR=<dir> smoke-linux-rpm.sh <rpm>}"
PKG_DIR="${SMOKE_PKG_DIR:?usage: SMOKE_PKG_DIR=<dir> smoke-linux-rpm.sh <rpm>}"
APP_BIN="/usr/bin/deepseek-harness-desktop"

# 窗口解析（覆写旋钮 → 库读的全局）与判定串 ①②的唯一家在共享库
smoke_resolve_windows
APP_TIMEOUT=$((SMOKE_WAIT + 20))
base="$(basename "$RPM_PATH")"

# W2 落盘：SMOKE_LOG_DIR 置时挂进容器供容器的 EXIT trap 落日志；未置则挂一次性 tmp（跑后清掉）。
rpm_log_host=""
if [[ -n "${SMOKE_LOG_DIR:-}" ]]; then
  rpm_log_host="$SMOKE_LOG_DIR"
else
  rpm_log_host="$(mktemp -d)"
fi
mkdir -p "$rpm_log_host" 2>/dev/null || true

# 无人值守环境里的常量项从宿主环境透传给容器（`-e VAR` 不带 =value = 取本进程同名变量），
# 值只在 smoke-verdict-lib.sh 写一处（ADR proxy-log-noise-reduction）；
# 容器自建 home，故不透传 home。
smoke_unattended_env

echo "== [rpm] fedora 容器安装冒烟: $base"
# `-e SMOKE_WAIT_SECONDS`/`-e SMOKE_SETTLE_SECONDS` 透传窗口旋钮；判据串①②不传——
# 容器内 source 同一份共享库即得（判据串曾因各处手抄错位致冒烟恒败）。
docker_rc=0
docker run --rm \
  -v "$PKG_DIR:/pkg:ro" \
  -v "$SCRIPT_DIR/lib:/smoke-lib:ro" \
  -v "$SCRIPT_DIR/smoke-linux-rpm-inner.sh:/smoke-inner.sh:ro" \
  -v "$rpm_log_host:/smokelogs:rw" \
  -e SMOKE_PKG_NAME="$base" \
  -e SMOKE_APP_BIN="$APP_BIN" \
  -e SMOKE_SETTLE_SECONDS="${SMOKE_SETTLE_SECONDS:-}" \
  -e SMOKE_WAIT_SECONDS="${SMOKE_WAIT_SECONDS:-}" \
  -e APP_TIMEOUT="$APP_TIMEOUT" \
  -e DEEPSEEK_API_KEY \
  -e DSH_DESKTOP_PROXY_TRACE \
  fedora:44 bash /smoke-inner.sh || docker_rc=$?
# rc 传给调用方（调用方记数判红）；一次性 tmp 在两条路径上都回收。
# 非零留痕：docker 退出码是内外腿分界（inner 结论行在上文），哑巴 exit 让顶层
# 分不清容器内红还是 docker 本体红。
if [[ -z "${SMOKE_LOG_DIR:-}" ]]; then rm -rf "$rpm_log_host"; fi
if [[ "$docker_rc" -ne 0 ]]; then error "[rpm] 容器退出码 $docker_rc（inner 结论行见上文）"; fi
exit "$docker_rc"
