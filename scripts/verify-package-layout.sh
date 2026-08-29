#!/usr/bin/env bash
# verify-package-layout.sh — 包内容布局断言（批次一，ADR artifact-verification-chain）。
# online-first（ADR online-first-unbundled-runtime）后安装器只带壳 + 插件资源，断言两件事：
#   ①无闭包残留——resources/runtime 出现即打包漂移（旧缓存/手工产物混入），fail loud；
#   ②插件资源——resources/plugins/dsh-desktop-companion.tgz 存在、过体积/名称关，
#     并与**现打源 tgz** 逐字节比对（直击 v0.2.0「缓存陈旧带出旧插件」事故类）。
#
# 用法: verify-package-layout.sh --plugins <built tgz> --target <app-content-root>
#   target 约定为「内容根」，插件资源相对它的位置固定为 resources/plugins：
#     Windows→ --target <Inno staging 根>（staging 是安装器唯一内容源）
#     macOS  → --target <dmg 挂载点>/…app/Contents/Resources --rt .
#     Linux  → --target <staging>/usr/lib/deepseek-harness-desktop（staging 级断言）
set -euo pipefail

PLUGINS="" TARGET="" RT_REL=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --plugins) PLUGINS="${2:?}"; shift 2 ;;
    --target) TARGET="${2:?}"; shift 2 ;;
    --rt) RT_REL="${2:?}"; shift 2 ;;
    *) echo "error: 未知参数 $1" >&2; exit 1 ;;
  esac
done
[[ -n "$PLUGINS" && -n "$TARGET" ]] || { echo "error: 需要 --plugins 与 --target" >&2; exit 1; }
[[ -f "$PLUGINS" ]] || { echo "error: 现打源 tgz 不存在: $PLUGINS" >&2; exit 1; }
[[ -d "$TARGET" ]] || { echo "error: 包内容根不存在: $TARGET" >&2; exit 1; }

errors=()
size_of() { stat -c%s "$1" 2>/dev/null || stat -f%z "$1" 2>/dev/null || echo 0; }

# 两个 tgz 的 payload 是否一致：解包后 diff -r。两次独立构建的 tar 因 mtime/属主等
# 元数据字节必然不同，字节比对不可用——内容级比对才与构建时点无关。
same_tarball_payload() { # $1=源 tgz $2=包内 tgz
  local a b
  a="$(mktemp -d)" b="$(mktemp -d)"
  tar -xzf "$1" -C "$a" && tar -xzf "$2" -C "$b"
  if diff -r "$a" "$b" >/dev/null 2>&1; then
    rm -rf "$a" "$b"; return 0
  fi
  rm -rf "$a" "$b"; return 1
}

# ①闭包残留检测（RT_REL 允许 --rt 显式给相对路径；默认 resources/runtime）
RT_REL="${RT_REL:-resources/runtime}"
if [[ -e "$TARGET/$RT_REL" ]]; then
  errors+=("内容根出现 $RT_REL（闭包已退役，属打包漂移——旧缓存/手工产物混入）")
fi

# ②插件资源：存在 + 名称正确 + 体积下限 + 与现打源 tgz 逐字节一致
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
    elif ! same_tarball_payload "$PLUGINS" "$dst"; then
      errors+=("$name 与现打源 tgz 内容不一致——陈旧产物或错误来源混入（v0.2.0 事故类）")
    else
      echo "  ok: $name ($(du -h "$dst" | cut -f1)，与现打源 tgz 内容一致)"
    fi
  fi
fi

if [[ ${#errors[@]} -gt 0 ]]; then
  echo "error: 包内容布局断言失败（${#errors[@]} 项）：" >&2
  for e in "${errors[@]}"; do echo "  ✗ $e" >&2; done
  exit 1
fi
echo "== 布局断言通过：${TARGET}（无闭包残留、插件资源供给且与源一致）"
