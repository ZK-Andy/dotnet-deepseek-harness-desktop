#!/usr/bin/env bash
# release-preflight.sh — 发布总检位（批次一，ADR artifact-verification-chain）。
# 在 release.yml 聚合三平台产物之后、创建 Release 之前执行：
#   ①资产矩阵完备——deb×2 / rpm×2 / dmg×2 / setup.exe×1 按发布命名模式精确匹配；
#   ②单资产体积下限——拦截空壳/半截包（闭包 ~340MB 压缩后正常包远高于此线）；
#   ③SHA256SUMS 全量复核。
# 任一不满足 fail loud 终止发布。
#
# 用法: release-preflight.sh <release-assets 目录>
set -euo pipefail

DIR="${1:?usage: release-preflight.sh <assets-dir>}"
[[ -d "$DIR" ]] || { echo "error: 资产目录不存在: $DIR" >&2; exit 1; }
cd "$DIR"

# 体积下限 MB：deb 实测 ~90M+、dmg/exe ~100M+；50M 为保守线，误杀风险随瘦身同调
FLOOR_MB=50

shopt -s nullglob
errors=()

check_pattern() { # $1=glob 模式 $2=描述
  local pattern="$1" desc="$2"
  local -a files=($pattern)
  if [[ ${#files[@]} -eq 0 ]]; then
    errors+=("缺 ${desc}（无匹配 $pattern）")
    return
  fi
  if [[ ${#files[@]} -gt 1 ]]; then
    errors+=("$desc 匹配到 ${#files[@]} 个文件，应恰为 1：${files[*]}")
    return
  fi
  local f="${files[0]}"
  # 兼容 GNU/BSD stat
  local mb; mb=$(( ($(stat -c%s "$f" 2>/dev/null || stat -f%z "$f") + 1024*1024 - 1) / (1024*1024) ))
  if [[ "$mb" -lt "$FLOOR_MB" ]]; then
    errors+=("$desc 仅 ${mb}MB < 下限 ${FLOOR_MB}MB，疑似空壳/半截包：$f")
    return
  fi
  echo "  ok: $desc → $f (${mb}MB)"
  ALLOWED+=("$f")
}

echo "== 资产矩阵 =="
# 矩阵单一来源：check_pattern 命中的文件记入 ALLOWED，意外资产扫描复用同一份清单
# linux arm64 停发（2026-08-24 拍板，上游无原生库）；恢复时补回 *_linux-arm64.deb
# 与 *_linux-aarch64.rpm 两行并同步 package-linux.yml matrix。
ALLOWED=()
check_pattern 'deepseek-harness-desktop_*_linux-amd64.deb' 'linux amd64 deb'
check_pattern 'deepseek-harness-desktop_*_linux-x86_64.rpm' 'linux x86_64 rpm'
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

# 意外资产：不在矩阵命中清单内的安装包形态出现即报错（打包漂移信号）
unexpected=()
for f in *.deb *.rpm *.dmg *.exe; do
  [[ " ${ALLOWED[*]} " == *" $f "* ]] || unexpected+=("$f")
done
if [[ ${#unexpected[@]} -gt 0 ]]; then
  errors+=("发现命名模式之外的产物（打包漂移？）：${unexpected[*]}")
fi

if [[ ${#errors[@]} -gt 0 ]]; then
  echo "error: 发布 preflight 失败（${#errors[@]} 项）：" >&2
  for e in "${errors[@]}"; do echo "  ✗ $e" >&2; done
  exit 1
fi
echo "== 发布 preflight 通过：矩阵完备、体积达线、校验和一致 =="
