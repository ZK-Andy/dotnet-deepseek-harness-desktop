#!/usr/bin/env bash
# verify-package-layout.sh — 包内容布局断言（批次一，ADR artifact-verification-chain）。
# online-first（ADR online-first-unbundled-runtime）后安装器只带壳 + 插件资源，断言两件事：
#   ①无闭包残留——resources/runtime 出现即打包漂移（旧缓存/手工产物混入），fail loud；
#   ②插件资源——resources/plugins/dsh-desktop-companion.tgz 存在、过体积/名称关
#     （tgz 由 build-companion-tgz.sh 打包时从源码现打、当场校验，新鲜度由「现打直进
#     staging」结构性保证，无独立源可比对，故不做 tar 间比对）。
#
# 用法: verify-package-layout.sh --target <app-content-root>
#   target 约定为「内容根」（exe 所在目录，资源相对它解析）：
#     Windows→ --target <Inno staging 根>（staging 是安装器唯一内容源）
#     macOS  → --target <dmg 挂载点>/…app/Contents/MacOS
#     Linux  → --target <staging>/usr/lib/deepseek-harness-desktop
set -euo pipefail

TARGET=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --target) TARGET="${2:?}"; shift 2 ;;
    *) echo "error: 未知参数 $1" >&2; exit 1 ;;
  esac
done
[[ -n "$TARGET" ]] || { echo "error: 需要 --target" >&2; exit 1; }
[[ -d "$TARGET" ]] || { echo "error: 包内容根不存在: $TARGET" >&2; exit 1; }

errors=()
size_of() { stat -c%s "$1" 2>/dev/null || stat -f%z "$1" 2>/dev/null || echo 0; }

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
  sz="$(size_of "$dst")"
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

if [[ ${#errors[@]} -gt 0 ]]; then
  echo "error: 包内容布局断言失败（${#errors[@]} 项）：" >&2
  for e in "${errors[@]}"; do echo "  ✗ $e" >&2; done
  exit 1
fi
echo "== 布局断言通过：${TARGET}（无闭包残留、插件资源供给）"
