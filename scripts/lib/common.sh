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
# 一次，之后用 common_tmp_dir/file 建，退出时统一回收。
#
# 回收靠**单一临时根**：`common_tmp_trap` 建根，`common_tmp_dir/file` 建的路径都在根**里面**，
# trap 删根即全清。两条约束：①路径必须建在根内——建到根外就漏，没有人回收；②根是普通变量，
# subshell 照常继承，故 `x="$(common_tmp_dir)"` 这类调用面也照样落进根里。
#
# **EXIT trap 纪律（调用方带 `set -e` 时）**：句柄不得翻转调用方退出码——trap 的末命令失败
# 会把 `exit 0` 翻成 exit 1。故句柄须以 0 返回、体内每一步都不得中断。回归锁见
# `scripts/lib/smoke-selftest.sh` 的 `trap-*` 三条断言（成功侧不翻红 / 根内路径真回收 / 不吞失败）。
_common_tmp_root=""

_common_tmp_cleanup() {
  local root="$_common_tmp_root"
  _common_tmp_root=""
  # 留痕不抛：`rm -rf` 对缺失路径本就返 0，warn 自身失败也被 `|| true` 兜住（本句须恒为 0）。
  if [[ -n "$root" ]]; then
    rm -rf -- "$root" || warn "临时根回收失败（不影响退出码）：$root" || true
  fi
  return 0
}

common_tmp_trap() {
  [[ -n "$_common_tmp_root" ]] || _common_tmp_root="$(mktemp -d)"
  trap '_common_tmp_cleanup' EXIT
}

common_tmp_dir() { # 打印新建临时目录路径（建在临时根内）
  mktemp -d "${_common_tmp_root:?先调 common_tmp_trap 建临时根}/XXXXXX"
}

common_tmp_file() { # 打印新建临时文件路径（同上）
  mktemp "${_common_tmp_root:?先调 common_tmp_trap 建临时根}/XXXXXX"
}

# 文件字节数；缺失/不可读即 0。顺序不可换：GNU 的 `stat -c` 先试，BSD 落 `-f%z`
# （GNU 的 `-f` 是文件系统语义，对不存在的路径同样失败，故末位 0 兜底成立）。
file_size() { # $1=路径
  stat -c%s "$1" 2>/dev/null || stat -f%z "$1" 2>/dev/null || printf '0'
}
