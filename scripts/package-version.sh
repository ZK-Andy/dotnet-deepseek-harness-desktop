#!/usr/bin/env bash
# package-version.sh — 打包版本解析与 tag/输入一致性校验（唯一家，三平台打包腿共用）。
#
# 调用方 = `.github/actions/package-setup`（三条打包腿与 release.yml 的调用链都经它）。
# 解析结果打到 stdout，调用方写入 `$GITHUB_ENV` 的 `VERSION`。
#
# 判据（发布正确性，不是提示）：ref 为 `refs/tags/v*` 时版本只认 tag——dispatch 输入非空且与
# tag 不一致即 fail loud。版本来源因此有三层，优先序固定为「tag > 输入 > csproj <Version>」；
# 三层全空即 fail loud（空版本会令 `dotnet publish -p:Version=` 触发 MSB4044 硬失败）。
#
# 用法:
#   scripts/package-version.sh [--ref <ref>] [--input <version>] [--csproj <path>]
#   scripts/package-version.sh --self-test
# 环境（CI 注入；本地可省）: GITHUB_REF（--ref 缺省）、INPUT_VERSION（--input 缺省）
set -euo pipefail

SELF="${BASH_SOURCE[0]}"

usage() {
  cat <<'EOF'
用法: package-version.sh [选项]
  --ref <ref>        触发 ref（缺省 $GITHUB_REF）；refs/tags/v* 时版本只认 tag
  --input <version>  dispatch 输入的版本（缺省 $INPUT_VERSION）；允许 v0.1.3 形式
  --csproj <path>    <Version> 回退来源（缺省 src/DeepSeek.Harness.Desktop/DeepSeek.Harness.Desktop.csproj）
  --self-test        离线夹具自测（假 ref/输入/csproj 实跑本脚本）
  -h, --help         本帮助
EOF
}

ref="${GITHUB_REF:-}"
raw_input="${INPUT_VERSION:-}"
csproj="src/DeepSeek.Harness.Desktop/DeepSeek.Harness.Desktop.csproj"
self_test=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --ref) ref="${2:?--ref 需要值}"; shift 2 ;;
    --input) raw_input="${2?--input 需要值}"; shift 2 ;;
    --csproj) csproj="${2:?--csproj 需要值}"; shift 2 ;;
    --self-test) self_test=1; shift ;;
    -h | --help) usage; exit 0 ;;
    *) echo "error: 未知参数 $1" >&2; usage >&2; exit 2 ;;
  esac
done

resolve() {
  local tag="" input="${raw_input#v}"
  if [[ "$ref" == refs/tags/v* ]]; then tag="${ref#refs/tags/v}"; fi
  if [[ -n "$tag" ]]; then
    # tag 是版本的权威来源：输入留空或等于 tag（带不带 v 前缀均可）都合法，
    # 非空且不一致即发布正确性错误——不得降级为 ::warning:: 继续走 tag。
    if [[ -n "$input" && "$input" != "$tag" ]]; then
      echo "::error::inputs.version($raw_input) 与 tag v$tag 不一致；版本只能来自 tag（清空输入或填写 v$tag）" >&2
      return 1
    fi
    printf '%s\n' "$tag"
    return 0
  fi
  if [[ -z "$input" ]]; then
    # 输入留空时回退 csproj <Version>：预览包与正式包同源，避免误内嵌旧版本号。
    input="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$csproj" 2>/dev/null | head -1 || true)"
  fi
  if [[ -z "$input" ]]; then
    echo "::error::无法确定打包版本：非 tag ref、输入为空且 $csproj 无 <Version>" >&2
    return 1
  fi
  printf '%s\n' "$input"
}

# 自测：假 ref/输入/csproj 实跑本脚本（子进程），断言语义与 exit 码。
# 覆盖 tag 门全部分支（三形态合法输入 / 两种不一致 / 分支名像 tag 不算 tag）、回退链
# （csproj 缺失）与参数缺值须有诊断。
self_test_run() {
  local desc="$1" want="$2" want_err="$3" ref="$4" input="$5" csproj="$6"
  local out rc err
  set +e
  out="$(bash "$SELF" --ref "$ref" --input "$input" --csproj "$csproj" 2>"$tmp/err")"
  rc=$?
  set -e
  err="$(cat "$tmp/err")"
  if [[ -n "$want" ]]; then
    if [[ $rc -ne 0 ]]; then
      echo "FAIL [$desc]: 期望版本 $want，实得 exit $rc（stderr: $err）" >&2
      return 1
    fi
    if [[ "$out" != "$want" ]]; then
      echo "FAIL [$desc]: 期望版本 $want，实得 '$out'" >&2
      return 1
    fi
  else
    if [[ $rc -eq 0 ]]; then
      echo "FAIL [$desc]: 期望 fail loud，实得 exit 0（输出 '$out'）" >&2
      return 1
    fi
  fi
  if [[ -n "$want_err" && "$err" != *"$want_err"* ]]; then
    echo "FAIL [$desc]: stderr 未含 '$want_err'（实得 '$err'）" >&2
    return 1
  fi
  n=$((n + 1))
  return 0
}

if [[ $self_test -eq 1 ]]; then
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  printf '<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>\n' >"$tmp/with-version.csproj"
  printf '<Project><PropertyGroup></PropertyGroup></Project>\n' >"$tmp/no-version.csproj"

  n=0
  # tag 权威：输入三种形态都合法，版本取 tag
  self_test_run "tag+v0.5.9 输入" "0.5.9" "" refs/tags/v0.5.9 "v0.5.9" "$tmp/with-version.csproj"
  self_test_run "tag 无 v 输入" "0.5.9" "" refs/tags/v0.5.9 "0.5.9" "$tmp/with-version.csproj"
  self_test_run "tag 输入留空" "0.5.9" "" refs/tags/v0.5.9 "" "$tmp/with-version.csproj"
  # tag 权威：不一致即 fail loud，且 ::error:: 点名两个版本号
  self_test_run "tag 输入不一致（带 v）" "" "v0.5.8" refs/tags/v0.5.9 "v0.5.8" "$tmp/with-version.csproj"
  self_test_run "tag 输入不一致（无 v）" "" "0.5.8" refs/tags/v0.5.9 "0.5.8" "$tmp/with-version.csproj"
  # 非 tag：输入优先；输入留空回退 csproj；两者皆空 fail loud
  self_test_run "分支 ref + 输入" "0.1.3" "" refs/heads/main "v0.1.3" "$tmp/with-version.csproj"
  self_test_run "分支 ref 回退 csproj" "1.2.3" "" refs/heads/main "" "$tmp/with-version.csproj"
  self_test_run "分支 ref + 无 Version" "" "无法确定打包版本" refs/heads/main "" "$tmp/no-version.csproj"
  # 分支名恰为 v0.5.9 不是 tag：判定只看 ref 前缀
  self_test_run "分支名像 tag 但仍回退 csproj" "1.2.3" "" refs/heads/v0.5.9 "" "$tmp/with-version.csproj"
  # csproj 不存在（回退源缺失）同样 fail loud，不静默给空版本
  self_test_run "csproj 缺失" "" "无法确定打包版本" refs/heads/main "" "$tmp/absent.csproj"
  # 参数缺值必须带诊断（不是 `shift` 撞 `set -e` 的无声退出）
  if bash "$SELF" --ref refs/heads/main --input 2>"$tmp/err"; then
    echo "FAIL [--input 缺值]: 期望 fail loud，实得 exit 0" >&2
    exit 1
  fi
  grep -q "需要值" "$tmp/err" || { echo "FAIL [--input 缺值]: stderr 无诊断（$(cat "$tmp/err")）" >&2; exit 1; }
  n=$((n + 1))

  echo "package-version self-test OK ($n/$n)"
  exit 0
fi

resolve
