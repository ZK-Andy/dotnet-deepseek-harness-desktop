#!/usr/bin/env bash
# npm-prefix-lib.sh — npm 全局落点（userconfig `prefix`）的判定与改写。
# 消费者：scripts/prepare-windows-smoke.sh（Windows 冒烟腿的 runner 前置）。
#
# 旋钮（读方 = relocate_npm_prefix）：SMOKE_NPM_PREFIX 覆写落点；置空串 = 不重定向（保留 npm 默认
# %APPDATA%\npm）；未设 = 用 NPM_PREFIX_DEFAULT。
# 判门语义：本库的降级路径一律只 warn 并返回 0——慢不是错，冒烟腿不得因此判红。唯一会返回 1 的是
# write_npm_prefix（改写失败，原文件保持不动），由调用方决定降级。
# 自测：npm_prefix_self_test（0 = 全过；逐条打印 ok/FAIL；不碰真实 userconfig）。
# 本库只含定义，source 无副作用（临时根只在自测函数内建）。

_NPM_PREFIX_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./common.sh
source "$_NPM_PREFIX_LIB_DIR/common.sh"

# npm 全局落点默认值（D: 是 Windows runner 上快一个量级的那块卷；实测见 ADR
# process/2026-10-01-windows-smoke-npm-install-speedup）。
NPM_PREFIX_DEFAULT='D:/npm-global'

# 卷可用判定：能建出目录即算可用（D: 缺失/只读时退回默认落点，不判门）。
volume_usable() { # $1=目录；0=可用
  local dir="$1"
  mkdir -p "$dir" 2>/dev/null || return 1
  [[ -d "$dir" ]]
}

# 把 prefix 写进 npmrc：保留既有其他设置，只增改 prefix 行（幂等；`prefix = x` 变体同换）。
# 0 = 已写入；1 = 改写失败（原文件保持不动，由调用方 warn）。**原子替换**：先在同目录写 `.tmp`，
# 权限按原文件复制（`chmod --reference`；不支持该选项的 chmod 落回默认位），再 `mv` 覆盖——原文件
# 在替换前不被截断，写入中途失败（ENOSPC/EIO）也不会毁掉既有设置。
write_npm_prefix() { # $1=npmrc 路径 $2=前缀
  local file="$1" prefix="$2" tmp="${1}.tmp" rc=0
  mkdir -p "$(dirname "$file")" || return 1
  if [[ -f "$file" ]]; then
    # grep 只有 exit 0/1 是合法语义（有/无匹配行）；exit ≥2（读失败）必须上抛——吞掉它会让
    # $tmp 停在截断状态，既有 registry/_authToken 被单行 prefix= 顶掉。
    grep -vE '^[[:space:]]*prefix[[:space:]]*=' "$file" >"$tmp" || rc=$?
    if [[ "$rc" -gt 1 ]]; then
      rm -f "$tmp"
      return 1
    fi
    chmod --reference="$file" "$tmp" 2>/dev/null || true
  else
    : >"$tmp" || return 1
  fi
  if ! printf 'prefix=%s\n' "$prefix" >>"$tmp"; then
    rm -f "$tmp"
    return 1
  fi
  if ! mv "$tmp" "$file"; then
    rm -f "$tmp"
    return 1
  fi
}

# 落点重定向编排：目标卷不可用、改写失败都退回 npm 默认落点（只 warn，不判门）。
# 证据位（同样不判门）：改写后回读 npm 解析到的前缀。
# $1 = userconfig 路径（默认 `$HOME/.npmrc`；自测注入临时路径用）。
relocate_npm_prefix() {
  local userconfig="${1:-${HOME}/.npmrc}"
  local prefix="${SMOKE_NPM_PREFIX-$NPM_PREFIX_DEFAULT}"
  if [[ -z "$prefix" ]]; then
    log '[win] SMOKE_NPM_PREFIX 为空：保留 npm 默认全局落点'
    return 0
  fi
  if ! volume_usable "$prefix"; then
    warn "[win] 全局落点目标卷不可用（$prefix）：退回 npm 默认落点（安装会慢一个量级，见 ADR）"
    return 0
  fi
  # 改写失败（userconfig 不可写/父目录被占）同样只 warn：慢不是错，本步不得把腿判红。
  if ! write_npm_prefix "$userconfig" "$prefix"; then
    warn "[win] npm userconfig 改写失败（$userconfig）：退回 npm 默认落点（安装会慢一个量级，见 ADR）"
    return 0
  fi
  log "[win] npm 全局落点已指向 $prefix（userconfig=${userconfig}）"
  log "[win] npm config get prefix = $(npm config get prefix 2>/dev/null || echo '(查询失败)')"
}

# 自测：0 = 全过。覆盖写入四态（新建/保留既有/替换既有 prefix 行/幂等）与三条降级路径
# （落点被普通文件占位、空串旋钮、userconfig 改写失败）。
npm_prefix_self_test() {
  local fails=0 dir f rc
  common_tmp_trap
  dir="$(common_tmp_dir)"
  _np_assert() { # $1=描述 $2=期望 prefix 行数 $3=期望保留的既有行（可空）
    local want_n="$2" want_keep="${3:-}" got_n keep=1
    got_n="$(grep -cE '^prefix=' "$f" || true)"
    if [[ -n "$want_keep" ]] && ! grep -qF "$want_keep" "$f"; then keep=0; fi
    if [[ "$got_n" == "$want_n" && "$keep" == 1 ]]; then
      echo "ok: $1"
    else
      echo "FAIL: $1（prefix 行数=$got_n 期望=$want_n，既有设置保留=$keep）" >&2
      fails=$((fails + 1))
    fi
  }
  f="$dir/new.npmrc"
  write_npm_prefix "$f" "D:/npm-global"
  _np_assert "新建 npmrc 写入 prefix" 1
  f="$dir/keep.npmrc"
  printf 'registry=https://example.invalid/\n' >"$f"
  write_npm_prefix "$f" "D:/npm-global"
  _np_assert "既有设置保留" 1 "registry=https://example.invalid/"
  f="$dir/replace.npmrc"
  printf 'prefix = C:/old\nregistry=https://example.invalid/\n' >"$f"
  write_npm_prefix "$f" "D:/npm-global"
  _np_assert "既有 prefix 行被换掉不重复" 1 "registry=https://example.invalid/"
  write_npm_prefix "$f" "D:/npm-global"
  _np_assert "重复调用幂等" 1
  if ! grep -qF 'prefix=D:/npm-global' "$f"; then
    echo "FAIL: prefix 值未写成期望形态" >&2
    fails=$((fails + 1))
  fi

  # 降级路径：落点被普通文件占位 → 判不可用；空串旋钮 → 不落盘；不可写 userconfig → 不上抛。
  : >"$dir/occupied"
  if volume_usable "$dir/occupied"; then
    echo "FAIL: 普通文件占位的落点应判不可用" >&2
    fails=$((fails + 1))
  else
    echo "ok: 普通文件占位的落点判不可用"
  fi
  rc=0
  SMOKE_NPM_PREFIX='' relocate_npm_prefix "$dir/empty-knob.npmrc" >/dev/null 2>&1 || rc=$?
  if [[ "$rc" -eq 0 && ! -e "$dir/empty-knob.npmrc" ]]; then
    echo "ok: 空串旋钮不重定向且不判失败"
  else
    echo "FAIL: 空串旋钮（rc=$rc，产物存在=$([[ -e "$dir/empty-knob.npmrc" ]] && echo yes || echo no)）" >&2
    fails=$((fails + 1))
  fi
  # 判据必须跑在「未被测试的上下文」里：父侧 `|| rc=$?` 会让 bash 连子壳体一并忽略 errexit
  # （体内显式 `set -e` 也救不回来），故改用独立子进程做实判别——有守卫 rc=0，去守卫 rc=1
  # （评审以删守卫的 mutation 实证；原地 `( set -e … )` 写法对该回归恒真）。
  rc=0
  SMOKE_NPM_PREFIX="$dir/unwritable/child" bash -c '
    set -euo pipefail
    source "$1"
    source "$2"
    relocate_npm_prefix "$3"
  ' _ "$_NPM_PREFIX_LIB_DIR/common.sh" "$_NPM_PREFIX_LIB_DIR/npm-prefix-lib.sh" "$dir/occupied/x.npmrc" >/dev/null 2>&1 || rc=$?
  if [[ "$rc" -eq 0 ]]; then
    echo "ok: 改写失败不判门（只 warn）"
  else
    echo "FAIL: 改写失败被上抛（rc=$rc）" >&2
    fails=$((fails + 1))
  fi

  [[ "$fails" -eq 0 ]]
}
