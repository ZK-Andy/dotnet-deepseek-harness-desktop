#!/usr/bin/env bash
# verify-package-layout.sh — 包内容布局断言（批次一，ADR artifact-verification-chain）。
# online-first（ADR online-first-unbundled-runtime）后安装器只带壳 + 插件资源，断言：
#   ①无闭包残留——resources/runtime 出现即打包漂移（旧缓存/手工产物混入），fail loud；
#   ②插件资源——resources/plugins/dsh-desktop-companion.tgz 存在、过体积/名称关
#     （tgz 由 build-companion-tgz.sh 打包时从源码现打、当场校验，新鲜度由「现打直进
#     staging」结构性保证，无独立源可比对，故不做 tar 间比对）；
#   ③主二进制存在且带可执行位——三平台共性（linux/mac 此前只断 Windows 主 exe，
#     可静默发布「无主程序包」）；
#   ④原生库——三平台同形状检查（内容根顶层必须存在）。
#     清单 = 宿主实际加载的面：Skia/HarfBuzz、macOS `libAvaloniaNative`、Windows `av_libglesv2`。
#     `runtimes/<rid>/native` 不再要求：Ryn 包摘除后无任何包向该目录供给原生库，宿主只从
#     内容根顶层加载（Windows WebView2 由控件的 ManagedWebView2Loader 定位已装运行时）。
#     windows 另断壳托管程序集 + wwwroot（PayloadSmoke 跑在 publish 直出目录，Inno [Files]
#     漏配时探针照绿、产物照缺，ADR windows-packaging-parity）；
#   macOS 追加 bundle 元数据：Info.plist 的 CFBundleIconFile 键与 Contents/Resources/icon.icns
#   （图标放别处 Finder/Dock 不读，包「能启动但没图标」是静默缺陷）。
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
# 平台原生库放内容根顶层（宿主实际加载面）；
# macOS 的 bundle 元数据挂在内容根的上一层 Contents/（Info.plist 与 Resources/icon.icns）。
_layout_fixture() { # $1=内容根 $2=平台
  local dir="$1" plat="$2" main="DeepSeek.Harness.Desktop" natives contents
  [[ "$plat" == windows ]] && main="DeepSeek.Harness.Desktop.exe"
  mkdir -p "$dir/resources/plugins" "$dir/wwwroot"
  printf '%s\n' '{"name":"dsh-desktop-companion"}' >"$dir/package.json"
  mkdir -p "$dir/pkg/package"
  cp "$dir/package.json" "$dir/pkg/package/package.json"
  # 随机字节而非零填充：零可被 gzip 压到几百字节，会撞上「过小即假包」的体积下限断言
  head -c 8192 /dev/urandom >"$dir/pkg/package/filler.bin"
  (cd "$dir/pkg" && tar -czf "$dir/resources/plugins/dsh-desktop-companion.tgz" package)
  rm -rf "$dir/pkg" "$dir/package.json"
  printf '#!/bin/sh\necho fake\n' >"$dir/$main"
  chmod +x "$dir/$main"
  case "$plat" in
    windows)
      printf 'x\n' >"$dir/DeepSeek.Harness.Desktop.dll"
      natives=(libSkiaSharp.dll libHarfBuzzSharp.dll av_libglesv2.dll)
      ;;
    linux) natives=(libSkiaSharp.so libHarfBuzzSharp.so) ;;
    macos) natives=(libSkiaSharp.dylib libHarfBuzzSharp.dylib libAvaloniaNative.dylib) ;;
  esac
  for native in "${natives[@]}"; do
    printf 'x\n' >"$dir/$native"
  done
  if [[ "$plat" == macos ]]; then
    contents="$(dirname "$dir")"; mkdir -p "$contents/Resources"
    printf '<plist version="1.0"><dict><key>CFBundleIconFile</key><string>icon.icns</string></dict></plist>\n' >"$contents/Info.plist"
    printf 'icns\n' >"$contents/Resources/icon.icns"
  fi
}

layout_self_test() {
  local tmp fails=0 out rc=0
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
  rm -f "$tmp/linux/libSkiaSharp.so"
  _lt_expect "linux 缺原生库判红" fail linux "$tmp/linux"
  local mac="$tmp/macos/App.app/Contents/MacOS"
  _layout_fixture "$mac" macos
  _lt_expect "macos 合规内容根放行" pass macos "$mac"
  mv "$mac/DeepSeek.Harness.Desktop" "$mac/gone"
  _lt_expect "macos 缺主程序判红" fail macos "$mac"
  _layout_fixture "$mac" macos
  rm -f "$tmp/macos/App.app/Contents/Resources/icon.icns"
  _lt_expect "macos 缺应用图标判红" fail macos "$mac"
  _layout_fixture "$mac" macos
  printf '<plist version="1.0"><dict/></plist>\n' >"$tmp/macos/App.app/Contents/Info.plist"
  _lt_expect "macos Info.plist 缺 CFBundleIconFile 判红" fail macos "$mac"
  _layout_fixture "$mac" macos
  rm -f "$mac/libSkiaSharp.dylib"
  _lt_expect "macos 缺原生库判红" fail macos "$mac"
  _layout_fixture "$mac" macos
  rm -f "$tmp/macos/App.app/Contents/Info.plist"
  _lt_expect "macos 缺 Info.plist 判红" fail macos "$mac"
  _layout_fixture "$mac" macos
  printf '<plist version="1.0"><dict><key>CFBundleIconFile</key><string>icon.icns.bak</string></dict></plist>\n' >"$tmp/macos/App.app/Contents/Info.plist"
  _lt_expect "macos CFBundleIconFile 值非精确 icon.icns 判红" fail macos "$mac"
  _layout_fixture "$tmp/win" windows
  _lt_expect "windows 合规内容根放行" pass windows "$tmp/win"
  rm -f "$tmp/win/libSkiaSharp.dll"
  _lt_expect "windows 缺原生库判红" fail windows "$tmp/win"
  _layout_fixture "$tmp/win2" windows
  rm -rf "$tmp/win2/wwwroot"
  _lt_expect "windows 缺 wwwroot 判红" fail windows "$tmp/win2"
  # 平台参数缺失即 fail loud（退出码 2 契约，不能只断非零）
  bash "${BASH_SOURCE[0]}" --target "$tmp/linux" >/dev/null 2>&1 || rc=$?
  if [[ $rc -eq 2 ]]; then echo "ok: 缺 --platform fail loud（rc=2）"; else
    echo "FAIL: 缺 --platform 应 exit 2（实得 rc=$rc）" >&2; fails=$((fails + 1))
  fi
  rm -rf "$tmp"
  if [[ $fails -eq 0 ]]; then echo "self-test: PASS"; return 0; fi
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

# ④原生库（内容源即 staging；宿主加载 Avalonia 渲染栈与 WebView2 loader，缺了要到首帧才炸）
#   + 平台追加面：Windows 壳托管程序集/wwwroot，macOS bundle 元数据
case "$PLATFORM" in
  linux) natives=(libSkiaSharp.so libHarfBuzzSharp.so); label="Linux" ;;
  macos) natives=(libSkiaSharp.dylib libHarfBuzzSharp.dylib libAvaloniaNative.dylib); label="macOS" ;;
  windows) natives=(libSkiaSharp.dll libHarfBuzzSharp.dll av_libglesv2.dll); label="Windows" ;;
esac
for native in "${natives[@]}"; do
  [[ -f "$TARGET/$native" ]] || errors+=("$label staging 缺原生库: $native（内容根顶层）")
done
if [[ "$PLATFORM" == "windows" ]]; then
  [[ -f "$TARGET/DeepSeek.Harness.Desktop.dll" ]] || errors+=("Windows staging 缺壳托管程序集: DeepSeek.Harness.Desktop.dll")
  [[ -d "$TARGET/wwwroot" ]] || errors+=("Windows staging 缺 wwwroot（首屏壳页面）")
elif [[ "$PLATFORM" == "macos" ]]; then
  bundle="$(dirname "$TARGET")"
  if [[ ! -f "$bundle/Info.plist" ]]; then
    errors+=("macOS 包缺 Info.plist: $bundle/Info.plist")
  elif ! grep -qE '<key>CFBundleIconFile</key>[[:space:]]*<string>icon\.icns</string>' "$bundle/Info.plist"; then
    errors+=("macOS Info.plist 的 CFBundleIconFile 必须指向 icon.icns（Finder/Dock 回退通用图标）")
  fi
  [[ -f "$bundle/Resources/icon.icns" ]] || errors+=("macOS 包缺应用图标: $bundle/Resources/icon.icns（CFBundleIconFile 指向它）")
fi
[[ ${#errors[@]} -gt 0 ]] || echo "  ok: $label 包内容（主程序/原生库）"

if [[ ${#errors[@]} -gt 0 ]]; then
  error "包内容布局断言失败（${#errors[@]} 项，platform=${PLATFORM}）："
  for e in "${errors[@]}"; do echo "  ✗ $e" >&2; done
  exit 1
fi
echo "== 布局断言通过：${TARGET}（platform=${PLATFORM}；无闭包残留、插件资源齐、主程序在位）"
