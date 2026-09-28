#!/usr/bin/env bash
# packaging-common.sh — 三包脚本（package-{linux,macos,windows}.sh）共用头部。
# 唯一家：CLI 解析（--stage-only + publish 目录）、ARCH 归一化（→ ARCH/RID/OUT_SUFFIX/
# RPM_ARCH）、VERSION 默认、PUBLISH_DIR/OUT 路径推导、publish 目录存在断言、内容布局
# 断言入口（转 verify-package-layout.sh——闭包残留/插件资源/主二进制只在那里判）。
# 依赖：common.sh（消息模板 die/warn/log——本库自带 source，消费方只需 source 本文件）。
# 本文件只含定义，source 无副作用；调用方须已 `set -euo pipefail`。

_PACKAGING_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
_PACKAGING_ROOT="$(cd "$_PACKAGING_LIB_DIR/../.." && pwd)"
# shellcheck source=./common.sh
source "$_PACKAGING_LIB_DIR/common.sh"

# packaging_init <linux|macos|windows> [args...]
#   args：可选 `--stage-only`，其后可选 publish 目录（两者皆可省，等价各脚本既有 CLI）。
#   ARCH 环境变量可覆写架构名；缺省 linux→amd64、macos→arm64、windows→x64。
# 设置（调用方随后读取）：ROOT STAGE_ONLY ARCH RID OUT_SUFFIX RPM_ARCH PUBLISH_DIR VERSION OUT
packaging_init() {
  local platform="${1:-}"
  shift || true
  [[ -n "$platform" ]] || die "packaging_init: 缺 platform（linux|macos|windows）"

  local arg1="${1:-}" arg2="${2:-}" publish_arg=""
  # shellcheck disable=SC2034  # 产出给调用方读（见文件头契约）
  STAGE_ONLY=0
  if [[ "$arg1" == "--stage-only" ]]; then
    # shellcheck disable=SC2034  # 同上：调用方读 STAGE_ONLY 决定是否只组装 staging
    STAGE_ONLY=1
    publish_arg="$arg2"
  else
    publish_arg="$arg1"
  fi

  local raw="${ARCH:-}"
  if [[ -z "$raw" ]]; then
    case "$platform" in
      linux) raw=amd64 ;;
      macos) raw=arm64 ;;
      windows) raw=x64 ;;
      *) die "packaging_init: 未知 platform=$platform（仅 linux|macos|windows）" ;;
    esac
  fi

  RPM_ARCH=""
  case "$platform::$raw" in
    linux::amd64 | linux::x86_64) ARCH=amd64; RPM_ARCH=x86_64; RID=linux-x64; OUT_SUFFIX=linux-x64 ;;
    linux::arm64 | linux::aarch64) ARCH=arm64; RPM_ARCH=aarch64; RID=linux-arm64; OUT_SUFFIX=linux-arm64 ;;
    macos::x64 | macos::amd64 | macos::x86_64) ARCH=x64; RID=osx-x64; OUT_SUFFIX=osx-x64 ;;
    macos::arm64 | macos::aarch64) ARCH=arm64; RID=osx-arm64; OUT_SUFFIX=osx-arm64 ;;
    windows::x64 | windows::amd64 | windows::x86_64) ARCH=x64; RID=win-x64; OUT_SUFFIX=win-x64 ;;
    windows::arm64 | windows::aarch64) ARCH=arm64; RID=win-arm64; OUT_SUFFIX=win-arm64 ;;
    *) die "不支持 ARCH=$raw（platform=$platform；linux 用 amd64/arm64，mac/win 用 x64/arm64）" ;;
  esac

  ROOT="$_PACKAGING_ROOT"
  VERSION="${VERSION:-0.1.0}"
  PUBLISH_DIR="${publish_arg:-$ROOT/artifacts/publish-$RID}"
  # shellcheck disable=SC2034  # 同上：OUT 由调用方读
  OUT="$ROOT/artifacts/$OUT_SUFFIX"
  [[ -d "$PUBLISH_DIR" ]] || die "publish 目录不存在: $PUBLISH_DIR（先 dotnet publish，或传目录参数）"
}

# 内容布局断言（闭包残留 + 插件资源 + 主二进制/可执行位）——判据唯一家在
# scripts/verify-package-layout.sh；三包脚本一律经此入口，不在此处复写任何判据。
packaging_assert_layout() { # $1=平台（linux|macos|windows） $2=内容根（exe 所在目录）
  bash "$_PACKAGING_ROOT/scripts/verify-package-layout.sh" --platform "$1" --target "$2"
}

# ── 自测（离线夹具）───────────────────────────────────────────────────────
# 覆盖架构映射六格（三平台 × amd64/arm64 别名）与两条 fail loud 路径（非法架构、
# publish 目录缺失）。三包脚本的 `--self-test` 转调本函数：这些 case 模式是
# 「改一处漏一平台」的高危面，机器可查即接进门禁（CI 的 docs job 跑三份自测）。
_pkg_case_ok() { # $1=名字 $2=期望 $3=实得
  if [[ "$2" == "$3" ]]; then
    echo "ok: $1"
    return 0
  fi
  echo "FAIL: $1（期望 $2，实得 $3）" >&2
  return 1
}

# 自测夹具：die 在子 shell 里执行，只结束该子 shell（不带走自测进程）
_pkg_st_tmp=""
_pkg_bad_arch() { ( ARCH=sparc; export ARCH; packaging_init linux "$_pkg_st_tmp/publish" ); }
_pkg_bad_publish() { ( packaging_init linux "$_pkg_st_tmp/absent" ); }

packaging_self_test() {
  local tmp fails=0 spec plat arch got
  tmp="$(mktemp -d)"
  _pkg_st_tmp="$tmp"
  mkdir -p "$tmp/publish"
  for spec in \
    "linux amd64 linux-x64 amd64 x86_64" \
    "linux aarch64 linux-arm64 arm64 aarch64" \
    "macos x64 osx-x64 x64 " \
    "macos aarch64 osx-arm64 arm64 " \
    "windows x86_64 win-x64 x64 " \
    "windows arm64 win-arm64 arm64 "; do
    read -r plat arch want_rid want_arch want_rpm <<<"$spec"
    # 子 shell 内显式 export 而非 `ARCH=x packaging_init ...` 前缀赋值：前缀赋值作用于
    # 函数调用时其可见期只有函数体（bash 5 语义），函数返回后 `$ARCH` 即未绑定 → set -u 炸。
    got="$(ARCH="$arch"; export ARCH; packaging_init "$plat" "$tmp/publish" >/dev/null 2>&1; printf '%s|%s|%s' "$RID" "$ARCH" "$RPM_ARCH")"
    _pkg_case_ok "$plat/$arch → RID|ARCH|RPM_ARCH" "$want_rid|$want_arch|$want_rpm" "$got" || fails=$((fails + 1))
  done
  # 缺省架构（无 ARCH 环境变量）：linux amd64 / macos arm64 / windows x64
  for spec in "linux linux-x64" "macos osx-arm64" "windows win-x64"; do
    read -r plat want_rid <<<"$spec"
    got="$(unset ARCH; packaging_init "$plat" "$tmp/publish" >/dev/null 2>&1; printf '%s' "$RID")"
    _pkg_case_ok "$plat 缺省架构 → RID" "$want_rid" "$got" || fails=$((fails + 1))
  done
  # fail loud：非法架构 / publish 目录不存在（子 shell 内跑，die 只结束该子 shell）。
  # 断言**诊断文本**而非只看非零 rc：曾因本库漏 source common.sh 让 die 退化成
  # 「命令未找到」（rc=127）——只看 rc 的夹具照样绿，遮蔽了全部 fail-loud 路径失效。
  _pkg_fail_loud() { # $1=名字 $2=期望诊断子串 $3=夹具函数（其体内 die 只结束自身子 shell）
    local name="$1" want="$2" out
    shift 2
    if out="$("$@" 2>&1)"; then
      echo "FAIL: $name 应 fail loud" >&2
      return 1
    fi
    if [[ "$out" != *"$want"* ]]; then
      echo "FAIL: $name 的诊断文本缺「$want」（实得：$out）" >&2
      return 1
    fi
    echo "ok: $name fail loud（带诊断）"
  }
  _pkg_fail_loud "非法 ARCH" "不支持 ARCH" _pkg_bad_arch || fails=$((fails + 1))
  _pkg_fail_loud "publish 目录缺失" "publish 目录不存在" _pkg_bad_publish || fails=$((fails + 1))
  rm -rf "$tmp"
  if [[ $fails -eq 0 ]]; then
    echo "packaging self-test: PASS"
    return 0
  fi
  echo "packaging self-test: FAIL（$fails 项）" >&2
  return 1
}
