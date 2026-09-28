#!/usr/bin/env bash
# verify-package-layout.sh — 包内容布局断言（批次一，ADR artifact-verification-chain）。
# online-first（ADR online-first-unbundled-runtime）后安装器只带壳 + 插件资源，断言：
#   ①无闭包残留——resources/runtime 出现即打包漂移（旧缓存/手工产物混入），fail loud；
#   ②插件资源——resources/plugins/dsh-desktop-companion.tgz 存在、过体积/名称关
#     （tgz 由 build-companion-tgz.sh 打包时从源码现打、当场校验，新鲜度由「现打直进
#     staging」结构性保证，无独立源可比对，故不做 tar 间比对）；
#   ③主二进制存在且带可执行位——三平台共性（linux/mac 此前只断 Windows 主 exe，
#     可静默发布「无主程序包」）；
#   ④--platform windows 追加：壳托管程序集 + 原生库 + runtimes/win-*/native + wwwroot
#     （PayloadSmoke 跑在 publish 直出目录上，Inno [Files] 漏配时探针照绿、产物照缺；
#     断 staging 即断安装器，ADR windows-packaging-parity）。
#
# 用法: verify-package-layout.sh --platform <linux|macos|windows> --target <内容根>
#   --platform 必填：原先按「内容根里有没有 .exe」隐式判断平台，Linux/mac 腿整段静默跳过
#   （名不符实）。改为显式后，跳过只可能是漏传参数（fail loud），不是默默发生。
#   内容根 = exe 所在目录（资源相对它解析）：
#     Windows→ Inno staging 根；macOS → …app/Contents/MacOS；Linux → staging/usr/lib/<app>
# 自测: verify-package-layout.sh --self-test（离线夹具：假内容根上逐条判红/放行，
#   三平台分支都能跑起来——否则平台分支只有各自 runner 上才被执行）
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
source "$SCRIPT_DIR/lib/common.sh"

# 夹具：造一个合规内容根（真 tgz，含 package/package.json 与足量填充），按需破坏某一处。
_layout_fixture() { # $1=目录 $2=平台
  local dir="$1" plat="$2" main="DeepSeek.Harness.Desktop"
  [[ "$plat" == windows ]] && main="DeepSeek.Harness.Desktop.exe"
  mkdir -p "$dir/resources/plugins" "$dir/runtimes/win-x64/native" "$dir/wwwroot"
  printf '%s\n' '{"name":"dsh-desktop-companion"}' >"$dir/package.json"
  mkdir -p "$dir/pkg/package"
  cp "$dir/package.json" "$dir/pkg/package/package.json"
  # 随机字节而非零填充：零可被 gzip 压到几百字节，会撞上「过小即假包」的体积下限断言
  head -c 8192 /dev/urandom >"$dir/pkg/package/filler.bin"
  (cd "$dir/pkg" && tar -czf "$dir/resources/plugins/dsh-desktop-companion.tgz" package)
  rm -rf "$dir/pkg" "$dir/package.json"
  printf '#!/bin/sh\necho fake\n' >"$dir/$main"
  chmod +x "$dir/$main"
  if [[ "$plat" == windows ]]; then
    printf 'x\n' >"$dir/DeepSeek.Harness.Desktop.dll"
    for native in WebView2Loader.dll saucer.dll saucer-bindings.dll saucer-bindings-desktop.dll; do
      printf 'x\n' >"$dir/$native"
    done
  fi
}

layout_self_test() {
  local tmp fails=0 out
  tmp="$(mktemp -d)"
  _lt_expect() { # $1=名字 $2=fail|pass $3=平台 $4=内容根
    local rc=0
    out="$(bash "${BASH_SOURCE[0]}" --platform "$3" --target "$4" 2>&1)" || rc=$?
    if [[ "$2" == fail && $rc -eq 0 ]]; then
      echo "FAIL: $1（应判红，实得 rc=0）" >&2
      fails=$((fails + 1))
    elif [[ "$2" == pass && $rc -ne 0 ]]; then
      echo "FAIL: $1（应放行，实得 rc=$rc：$out）" >&2
      fails=$((fails + 1))
    else
      echo "ok: $1"
    fi
  }
  _layout_fixture "$tmp/linux" linux
  _lt_expect "linux 合规内容根放行" pass linux "$tmp/linux"
  chmod -x "$tmp/linux/DeepSeek.Harness.Desktop"
  _lt_expect "linux 主程序缺可执行位判红" fail linux "$tmp/linux"
  chmod +x "$tmp/linux/DeepSeek.Harness.Desktop"
  mv "$tmp/linux/resources/plugins/dsh-desktop-companion.tgz" "$tmp/linux/resources/plugins/other.tgz"
  _lt_expect "插件 tgz 缺失/改名判红" fail linux "$tmp/linux"
  mv "$tmp/linux/resources/plugins/other.tgz" "$tmp/linux/resources/plugins/dsh-desktop-companion.tgz"
  mkdir -p "$tmp/linux/resources/runtime"
  _lt_expect "闭包残留 resources/runtime 判红" fail linux "$tmp/linux"
  rmdir "$tmp/linux/resources/runtime"
  mkdir -p "$tmp/macos"
  _layout_fixture "$tmp/macos" macos
  _lt_expect "macos 合规内容根放行" pass macos "$tmp/macos"
  mv "$tmp/macos/DeepSeek.Harness.Desktop" "$tmp/macos/gone"
  _lt_expect "macos 缺主程序判红" fail macos "$tmp/macos"
  _layout_fixture "$tmp/win" windows
  _lt_expect "windows 合规内容根放行" pass windows "$tmp/win"
  rm -f "$tmp/win/saucer.dll"
  _lt_expect "windows 缺原生库判红" fail windows "$tmp/win"
  _layout_fixture "$tmp/win2" windows
  rm -rf "$tmp/win2/wwwroot"
  _lt_expect "windows 缺 wwwroot 判红" fail windows "$tmp/win2"
  # 平台参数缺失即 fail loud（不再隐式跳过）
  if bash "${BASH_SOURCE[0]}" --target "$tmp/linux" >/dev/null 2>&1; then
    echo "FAIL: 缺 --platform 应 fail loud" >&2
    fails=$((fails + 1))
  else
    echo "ok: 缺 --platform fail loud"
  fi
  rm -rf "$tmp"
  if [[ $fails -eq 0 ]]; then
    echo "self-test: PASS"
    return 0
  fi
  echo "self-test: FAIL（$fails 项）" >&2
  return 1
}

if [[ "${1:-}" == "--self-test" ]]; then
  layout_self_test
  exit $?
fi

usage_die() { error "$*"; exit 2; }  # 用法错误 = 2（docs/script-standards.md 的退出码语义）

PLATFORM=""
TARGET=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --platform) PLATFORM="${2:-}"; [[ -n "$PLATFORM" ]] || usage_die "--platform 需要值"; shift 2 ;;
    --target) TARGET="${2:-}"; [[ -n "$TARGET" ]] || usage_die "--target 需要值"; shift 2 ;;
    *) usage_die "未知参数 $1（用法见文件头）" ;;
  esac
done
case "$PLATFORM" in
  linux | macos | windows) ;;
  "") usage_die "需要 --platform <linux|macos|windows>（平台不再由内容根隐式推断）" ;;
  *) usage_die "未知 --platform: $PLATFORM（仅 linux|macos|windows）" ;;
esac
[[ -n "$TARGET" ]] || usage_die "需要 --target"
[[ -d "$TARGET" ]] || die "包内容根不存在: $TARGET"

errors=()

# ①闭包残留检测
if [[ -e "$TARGET/resources/runtime" ]]; then
  errors+=("内容根出现 resources/runtime（闭包已退役，属打包漂移——旧缓存/手工产物混入）")
fi

# ②插件资源：存在 + 名称正确 + 体积下限
name="dsh-desktop-companion.tgz"
dst="$TARGET/resources/plugins/$name"
if [[ ! -f "$dst" ]]; then
  errors+=("包内缺插件资源: $dst")
else
  sz="$(file_size "$dst")"
  if [[ "$sz" -lt 4096 ]]; then
    errors+=("$name 过小（${sz}B < 4096B），疑似假包/半截包")
  else
    pkg_name="$(tar -xOzf "$dst" package/package.json 2>/dev/null \
      | grep -oE '"name"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 \
      | sed 's/.*:[[:space:]]*"//; s/"$//' || true)"
    expect="${name%.tgz}"
    if [[ "$pkg_name" != "$expect" ]]; then
      errors+=("$name 内 package.json name='$pkg_name'，期望 '$expect'")
    else
      echo "  ok: $name ($(du -h "$dst" | cut -f1))"
    fi
  fi
fi

# ③主二进制 + 可执行位
case "$PLATFORM" in
  windows) main_bin="DeepSeek.Harness.Desktop.exe" ;;
  *) main_bin="DeepSeek.Harness.Desktop" ;;
esac
if [[ ! -f "$TARGET/$main_bin" ]]; then
  errors+=("内容根缺主程序: $TARGET/$main_bin")
elif [[ "$PLATFORM" != "windows" && ! -x "$TARGET/$main_bin" ]]; then
  errors+=("$main_bin 缺可执行位（staging 组装后必须 chmod +x，否则装上是不可执行的死包）")
else
  echo "  ok: 主程序 $main_bin"
fi

# ④Windows 安装器内容断言（Inno [Files] 的内容源就是 staging）
if [[ "$PLATFORM" == "windows" ]]; then
  [[ -f "$TARGET/DeepSeek.Harness.Desktop.dll" ]] \
    || errors+=("Windows staging 缺壳托管程序集: DeepSeek.Harness.Desktop.dll")
  for native in WebView2Loader.dll saucer.dll saucer-bindings.dll saucer-bindings-desktop.dll; do
    if [[ ! -f "$TARGET/$native" && ! -f "$TARGET/runtimes/win-x64/native/$native" && ! -f "$TARGET/runtimes/win-arm64/native/$native" ]]; then
      errors+=("Windows staging 缺原生库: $native（根与 runtimes/win-*/native 均无）")
    fi
  done
  if [[ ! -d "$TARGET/runtimes/win-x64/native" && ! -d "$TARGET/runtimes/win-arm64/native" ]]; then
    errors+=("Windows staging 缺 runtimes/win-*/native（Ryn NativeLibraryResolver 唯一探测源）")
  fi
  [[ -d "$TARGET/wwwroot" ]] || errors+=("Windows staging 缺 wwwroot（首屏壳页面）")
  [[ ${#errors[@]} -gt 0 ]] || echo "  ok: Windows 安装器内容（exe/dll/原生库/runtimes/wwwroot）"
fi

if [[ ${#errors[@]} -gt 0 ]]; then
  error "包内容布局断言失败（${#errors[@]} 项，platform=${PLATFORM}）："
  for e in "${errors[@]}"; do echo "  ✗ $e" >&2; done
  exit 1
fi
echo "== 布局断言通过：${TARGET}（platform=${PLATFORM}；无闭包残留、插件资源齐、主程序在位）"
