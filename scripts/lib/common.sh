#!/usr/bin/env bash
# common.sh — 脚本层通用助手（唯一家；规范见 docs/script-standards.md）。
# 本文件只含定义，source 无副作用；调用方须已 `set -euo pipefail`。
# 内容：消息模板（log/warn/error/die）、临时文件纪律（mktemp + 单一 EXIT trap）、
# file_size（GNU/BSD stat 双兼容）。

# 消息一律走 stderr（Google Shell Style Guide：进度/错误不得混入 stdout 数据面）。
# 模板 `<级别>: <对象>: <原因>（<修复>）`——对象与原因由调用方按此组织文本。
log() { printf 'note: %s\n' "$*" >&2; }
warn() { printf 'warn: %s\n' "$*" >&2; }
error() { printf 'error: %s\n' "$*" >&2; }
die() { error "$*"; exit 1; }

# ── 临时文件纪律 ──────────────────────────────────────────────────────────
# 一个脚本只有一个 EXIT trap，句柄集中在本库：调用方开跑主体前 `common_tmp_trap`
# 一次，之后用 common_tmp_dir/file 建（自动登记），退出时统一回收。
_common_tmp_paths=""

_common_tmp_cleanup() {
  local p
  # 逐行读而非未加引号的展开：目录名含空白亦安全（SC2086 面）
  printf '%s\n' "$_common_tmp_paths" | while IFS= read -r p; do
    [[ -n "$p" ]] && rm -rf -- "$p"
  done
  return 0
}

common_tmp_trap() { trap '_common_tmp_cleanup' EXIT; }

common_tmp_dir() { # 打印新建临时目录路径
  local d
  d="$(mktemp -d)"
  _common_tmp_paths="${_common_tmp_paths}${d}"$'\n'
  printf '%s' "$d"
}

common_tmp_file() { # 打印新建临时文件路径
  local f
  f="$(mktemp)"
  _common_tmp_paths="${_common_tmp_paths}${f}"$'\n'
  printf '%s' "$f"
}

# 文件字节数；缺失/不可读即 0。顺序不可换：GNU 的 `stat -c` 先试，BSD 落 `-f%z`
# （GNU 的 `-f` 是文件系统语义，对不存在的路径同样失败，故末位 0 兜底成立）。
file_size() { # $1=路径
  stat -c%s "$1" 2>/dev/null || stat -f%z "$1" 2>/dev/null || printf '0'
}
