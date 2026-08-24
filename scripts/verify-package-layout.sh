#!/usr/bin/env bash
# verify-package-layout.sh — 包内容布局断言（批次一，ADR artifact-verification-chain）。
# 对打包产物的内容根做断言：node 可执行、dsh 入口、两个随包 tgz 存在且过名称/体积关，
# 并与**源闭包**（resources/runtime）逐字节比对 tgz——直击 v0.2.0「缓存陈旧带出旧插件」事故类。
#
# 用法: verify-package-layout.sh --runtime <closure-dir> --target <app-content-root> [--rt <相对路径>]
#   target 约定为「内容根」，运行时目录相对它的位置由 --rt 指定（默认 resources/runtime）：
#     macOS  → --target <dmg 挂载点>/…app/Contents/Resources --rt runtime
#     Windows→ --target <Inno staging 根>（默认 resources/runtime；staging 是安装器唯一内容源）
#     Linux  → --target <staging>/usr/lib/deepseek-harness-desktop（如需 staging 级断言）
set -euo pipefail

RUNTIME="" TARGET="" RT_REL="resources/runtime"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --runtime) RUNTIME="${2:?}"; shift 2 ;;
    --target) TARGET="${2:?}"; shift 2 ;;
    --rt) RT_REL="${2:?}"; shift 2 ;;
    *) echo "error: 未知参数 $1" >&2; exit 1 ;;
  esac
done
[[ -n "$RUNTIME" && -n "$TARGET" ]] || { echo "error: 需要 --runtime 与 --target" >&2; exit 1; }
[[ -d "$RUNTIME" ]] || { echo "error: 源闭包不存在: $RUNTIME" >&2; exit 1; }
[[ -d "$TARGET" ]] || { echo "error: 包内容根不存在: $TARGET" >&2; exit 1; }

R="$TARGET/$RT_REL"
errors=()

size_of() { stat -c%s "$1" 2>/dev/null || stat -f%z "$1" 2>/dev/null || echo 0; }

# node 可执行（win 为 node.exe）
NODE_BIN="$R/node"
[[ -f "$R/node.exe" ]] && NODE_BIN="$R/node.exe"
if [[ -f "$NODE_BIN" ]]; then
  echo "  ok: node → $NODE_BIN ($(du -h "$NODE_BIN" | cut -f1))"
else
  errors+=("缺 node 可执行: $NODE_BIN")
fi

# dsh 入口
ENTRY="$R/node_modules/@deepseek-ai/dsh/lib/bin.js"
if [[ -f "$ENTRY" ]]; then
  echo "  ok: dsh 入口存在"
else
  errors+=("缺 dsh 入口: $ENTRY")
fi

# 随包 tgz：存在 + 名称正确 + 体积下限 + 与源闭包逐字节一致
check_tgz() {
  local name="$1" floor="$2"
  local src="$RUNTIME/$name" dst="$R/$name"
  if [[ ! -s "$src" ]]; then
    errors+=("源闭包缺 ${name}（${src}）——bundle-runtime-ci 未随包，先修上游")
    return
  fi
  if [[ ! -f "$dst" ]]; then
    errors+=("包内缺 $name: $dst")
    return
  fi
  local sz; sz="$(size_of "$dst")"
  if [[ "$sz" -lt "$floor" ]]; then
    errors+=("$name 过小（${sz}B < ${floor}B），疑似假包/半截包")
    return
  fi
  local pkg_name
  # 管道带 || true：pipefail 下 grep 无匹配的非零退出不能炸掉脚本——空值由下方名称比对统一报错
  pkg_name="$(tar -xOzf "$dst" package/package.json 2>/dev/null \
    | grep -oE '"name"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 \
    | sed 's/.*:[[:space:]]*"//; s/"$//' || true)"
  local expect="${name%.tgz}"
  if [[ "$pkg_name" != "$expect" ]]; then
    errors+=("$name 内 package.json name='$pkg_name'，期望 '$expect'")
    return
  fi
  if ! cmp -s "$src" "$dst"; then
    errors+=("$name 与源闭包不一致——陈旧缓存或错误来源混入（v0.2.0 事故类）")
    return
  fi
  echo "  ok: $name ($(du -h "$dst" | cut -f1)，与源闭包逐字节一致)"
}

check_tgz "dshmarket.tgz" 10240
check_tgz "dsh-desktop-companion.tgz" 4096

if [[ ${#errors[@]} -gt 0 ]]; then
  echo "error: 包内容布局断言失败（${#errors[@]} 项）：" >&2
  for e in "${errors[@]}"; do echo "  ✗ $e" >&2; done
  exit 1
fi
echo "== 布局断言通过：${TARGET}（node/入口/tgz×2 全供给且与源闭包一致）"
