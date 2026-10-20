#!/usr/bin/env bash
# release-preflight.sh — 发布总检位（批次一，ADR artifact-verification-chain）。
# 在 release.yml 聚合三平台产物之后、创建 Release 之前执行：
#   ①资产矩阵完备——deb×2 / rpm×2 / dmg×2 / setup.exe×1 按发布命名模式精确匹配；
#   ②单资产体积下限——拦截空壳/半截包（online-first 后包 = 壳 + 插件资源，实测 ~26-36MB）；
#   ③SHA256SUMS 全量复核。
# 任一不满足 fail loud 终止发布。
#
# 用法: release-preflight.sh <release-assets 目录>
# 自测: release-preflight.sh --self-test（离线夹具：假资产目录实跑本脚本，正反两面都断言）
set -euo pipefail

# shellcheck source=lib/common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/common.sh"

SELF="${BASH_SOURCE[0]}"

# ── 自测（离线夹具）────────────────────────────────────────────────────────
# 本脚本是「平时不跑、发布才炸」的那类洞：判据只有真 tag 触发时才会执行。
# 自测把矩阵七件套用稀疏文件（dd 只落首字节，其余空洞）在临时目录里造出来，
# 逐条实跑本脚本（子进程）并断言退出码与关键诊断——正例与反例都要钉住。
fake_asset() { # $1=路径 $2=字节数（表观大小；实际占块近零）
  dd if=/dev/zero of="$1" bs=1 count=1 seek=$(( ${2} - 1 )) 2>/dev/null
}

# 矩阵七件套 + 校验和文件（命名与 release.yml 资产矩阵一致）
fake_matrix() { # $1=目录 $2=单件字节数（默认 16MB）
  local d="$1" bytes="${2:-16777216}"
  fake_asset "$d/deepseek-harness-desktop_0.6.0_linux-amd64.deb" "$bytes"
  fake_asset "$d/deepseek-harness-desktop_0.6.0_linux-arm64.deb" "$bytes"
  fake_asset "$d/deepseek-harness-desktop-0.6.0_linux-x86_64.rpm" "$bytes"
  fake_asset "$d/deepseek-harness-desktop-0.6.0_linux-aarch64.rpm" "$bytes"
  fake_asset "$d/DeepSeek.Harness.Desktop_0.6.0_macos-arm64.dmg" "$bytes"
  fake_asset "$d/DeepSeek.Harness.Desktop_0.6.0_macos-x64.dmg" "$bytes"
  fake_asset "$d/DeepSeek.Harness.Desktop_0.6.0_windows-x64-setup.exe" "$bytes"
  fake_sums "$d"
}

# 校验和文件（改过资产后须重算，否则红的是校验和而不是本用例要验的那条判据）
fake_sums() { # $1=目录
  (cd "$1" && sha256sum ./*.deb ./*.rpm ./*.dmg ./*.exe > SHA256SUMS.txt)
}

expect_rc() { # $1=描述 $2=期望退出码(0/1) $3=期望诊断子串（空=不查） $4=资产目录
  local desc="$1" want_rc="$2" want_msg="$3" dir="$4" out rc
  set +e
  out="$(bash "$SELF" "$dir" 2>&1)"
  rc=$?
  set -e
  if [[ "$rc" -ne "$want_rc" ]]; then
    echo "FAIL [$desc]: 期望 exit $want_rc，实得 $rc（输出：$out）" >&2
    return 1
  fi
  if [[ -n "$want_msg" && "$out" != *"$want_msg"* ]]; then
    echo "FAIL [$desc]: 输出未含 '$want_msg'（实得：$out）" >&2
    return 1
  fi
  n=$((n + 1))
  return 0
}

self_test() {
  local tmp base d
  common_tmp_trap
  tmp="$(common_tmp_dir)"
  base="$tmp/assets"

  # 正例：矩阵齐、体积达线、校验和一致
  mkdir -p "$base"
  fake_matrix "$base"
  expect_rc "矩阵完备即通过" 0 "" "$base"

  # 反例一：缺一件（臂 deb）
  d="$tmp/missing"
  mkdir -p "$d"
  fake_matrix "$d"
  rm -f "$d/deepseek-harness-desktop_0.6.0_linux-arm64.deb"
  expect_rc "缺资产判红" 1 "缺 linux arm64 deb" "$d"

  # 反例二：体积不足（14MB < 下限 15MB）
  d="$tmp/undersized"
  mkdir -p "$d"
  fake_matrix "$d"
  fake_asset "$d/DeepSeek.Harness.Desktop_0.6.0_macos-arm64.dmg" 14680064
  fake_sums "$d"
  expect_rc "体积不足判红" 1 "下限" "$d"

  # 边界一：体积恰在下线（15MB）即判过——钉住下限线的取值
  d="$tmp/at-floor"
  mkdir -p "$d"
  fake_matrix "$d"
  fake_asset "$d/DeepSeek.Harness.Desktop_0.6.0_macos-arm64.dmg" 15728640
  fake_sums "$d"
  expect_rc "体积恰达下限判过" 0 "" "$d"

  # 边界二：14MB+1B 按向上取整计 15MB 判过——钉住取整口径（改向下取整即红）
  d="$tmp/rounding"
  mkdir -p "$d"
  fake_matrix "$d"
  fake_asset "$d/DeepSeek.Harness.Desktop_0.6.0_macos-arm64.dmg" 14680065
  fake_sums "$d"
  expect_rc "体积取整向上判过" 0 "" "$d"

  # 反例三：校验和不一致（包被改）
  d="$tmp/tampered"
  mkdir -p "$d"
  fake_matrix "$d"
  printf 'x' >>"$d/DeepSeek.Harness.Desktop_0.6.0_macos-x64.dmg"
  expect_rc "校验和不一致判红" 1 "校验失败" "$d"

  # 反例四：矩阵之外的额外产物（打包漂移）
  d="$tmp/extra"
  mkdir -p "$d"
  fake_matrix "$d"
  fake_asset "$d/deepseek-harness-desktop_0.6.0_linux-i386.deb" 16777216
  expect_rc "意外资产判红" 1 "命名模式之外" "$d"

  # 反例五：意外产物藏在子目录（递归扫描面——顶层完备、深处藏副本也要拦住）
  d="$tmp/extra-nested"
  mkdir -p "$d/nested"
  fake_matrix "$d"
  fake_asset "$d/nested/deepseek-harness-desktop_0.6.0_linux-i386.deb" 16777216
  expect_rc "嵌套目录意外资产判红" 1 "命名模式之外" "$d"

  # 反例六：同一模式匹配到两个（命名漂移）
  d="$tmp/duplicate"
  mkdir -p "$d"
  fake_matrix "$d"
  cp "$d/deepseek-harness-desktop_0.6.0_linux-amd64.deb" "$d/deepseek-harness-desktop_0.6.1_linux-amd64.deb"
  expect_rc "同模式重复判红" 1 "应恰为 1" "$d"

  # 反例七：校验和文件缺失（复核面无从下手即判红）
  d="$tmp/no-sums"
  mkdir -p "$d"
  fake_matrix "$d"
  rm -f "$d/SHA256SUMS.txt"
  expect_rc "缺 SHA256SUMS 判红" 1 "缺 SHA256SUMS.txt" "$d"

  # 反例八：资产目录不存在（用法面）
  expect_rc "资产目录不存在判红" 1 "资产目录不存在" "$tmp/no-such-dir"

  echo "release-preflight self-test OK ($n/$n)"
}

if [[ "${1:-}" == "--self-test" ]]; then
  n=0
  self_test
  exit $?
fi

DIR="${1:?usage: release-preflight.sh <assets-dir>}"
[[ -d "$DIR" ]] || { echo "error: 资产目录不存在: $DIR" >&2; exit 1; }
cd "$DIR"

# 体积下限 MB：online-first 去闭包后 deb/dmg/exe 实测 ~26-36MB；15M 为保守线
#（正常包一半，仍拦空壳/半截包）。重新调线前先本地 publish 实测压缩体积。
FLOOR_MB=15

shopt -s nullglob
errors=()

check_pattern() { # $1=glob 模式 $2=描述
  local pattern="$1" desc="$2"
  # shellcheck disable=SC2206  # 故意的：$pattern 就是 glob，需就地展开成匹配集（上方已开 nullglob）
  local -a files=($pattern)
  if [[ ${#files[@]} -eq 0 ]]; then
    errors+=("缺 ${desc}（无匹配 ${pattern}）")
    return
  fi
  if [[ ${#files[@]} -gt 1 ]]; then
    errors+=("$desc 匹配到 ${#files[@]} 个文件，应恰为 1：${files[*]}")
    return
  fi
  local f="${files[0]}"
  local mb; mb=$(( ($(file_size "$f") + 1024 * 1024 - 1) / (1024 * 1024) ))
  if [[ "$mb" -lt "$FLOOR_MB" ]]; then
    errors+=("$desc 仅 ${mb}MB < 下限 ${FLOOR_MB}MB，疑似空壳/半截包：$f")
    return
  fi
  echo "  ok: $desc → $f (${mb}MB)"
  ALLOWED+=("$f")
}

echo "== 资产矩阵 =="
# 矩阵单一来源：check_pattern 命中的文件记入 ALLOWED，意外资产扫描复用同一份清单
ALLOWED=()
check_pattern 'deepseek-harness-desktop_*_linux-amd64.deb' 'linux amd64 deb'
check_pattern 'deepseek-harness-desktop_*_linux-arm64.deb' 'linux arm64 deb'
check_pattern 'deepseek-harness-desktop-*_linux-x86_64.rpm' 'linux x86_64 rpm'
check_pattern 'deepseek-harness-desktop-*_linux-aarch64.rpm' 'linux aarch64 rpm'
check_pattern 'DeepSeek.Harness.Desktop_*_macos-arm64.dmg' 'macOS arm64 dmg'
check_pattern 'DeepSeek.Harness.Desktop_*_macos-x64.dmg' 'macOS x64 dmg'
check_pattern 'DeepSeek.Harness.Desktop_*_windows-x64-setup.exe' 'Windows x64 安装器'

echo "== SHA256SUMS 复核 =="
if [[ -f SHA256SUMS.txt ]]; then
  if sha256sum --quiet -c SHA256SUMS.txt; then
    echo "  ok: SHA256SUMS.txt 全部一致"
  else
    errors+=("SHA256SUMS.txt 校验失败（见上方不一致项）")
  fi
else
  errors+=("缺 SHA256SUMS.txt")
fi

# 意外资产：不在矩阵命中清单内的安装包形态出现即报错（打包漂移信号）。
# 递归扫描（globstar），与 release.yml 的 **/*.ext glob 同语义——嵌套目录（如 rpmbuild
# 残留）里的同名产物同样算意外，杜绝"顶层完备、深处藏副本"漏检。
shopt -s globstar
unexpected=()
for f in **/*.deb **/*.rpm **/*.dmg **/*.exe; do
  [[ " ${ALLOWED[*]} " == *" $f "* ]] || unexpected+=("$f")
done
shopt -u globstar
if [[ ${#unexpected[@]} -gt 0 ]]; then
  errors+=("发现命名模式之外的产物（打包漂移？）：${unexpected[*]}")
fi

if [[ ${#errors[@]} -gt 0 ]]; then
  echo "error: 发布 preflight 失败（${#errors[@]} 项）：" >&2
  for e in "${errors[@]}"; do echo "  ✗ $e" >&2; done
  exit 1
fi

echo "== 发布 preflight 通过：矩阵完备、体积达线、校验和一致 =="
