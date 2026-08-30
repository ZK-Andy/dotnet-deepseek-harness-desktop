#!/usr/bin/env bash
# release-notes.sh — 从 git log 生成结构化 Release 正文（中英小节，按 conventional commit 类型归类）。
# 用法：
#   bash scripts/release-notes.sh [from_ref] [to_ref]     # 例：bash scripts/release-notes.sh v0.1.15 v0.1.16
#   bash scripts/release-notes.sh HEAD~10 HEAD            # 也可用范围
#   bash scripts/release-notes.sh --self-test             # 离线自检：断言所有 conventional commit 类型都有归属 bucket
# 省略 from_ref 时自动取当前 tag 的前一个 tag；省略 to_ref 默认 HEAD。
# 输出：markdown 正文到 stdout（供 release.yml 的 body 使用）。中文为主 + 英文小节头，与仓库「默认中文」一致。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REPO_URL="https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop"

# 把 conventional commit 类型归入正文小节 bucket（feat/fix/perf/docs/chore）。
# 无映射的类型（如 test/revert/wip）返回 "" — 从正文丢弃（release-notes 只呈现有用户价值的面）。
# 注意：refactor/style 必须保留 —— 它们承载重构与规范落地（v0.4.1 教训：这两类曾静默丢弃）。
type_bucket() {
  local t="$1"
  case "$t" in
    feat*)                              echo feat  ;;
    fix*)                               echo fix   ;;
    perf*)                              echo perf  ;;
    docs*)                              echo docs  ;;
    chore*|build*|ci*|refactor*|style*) echo chore ;;
    *)                                  echo ""    ;;
  esac
}

# 由全局 LOGS 逐行归类，按 bucket 名过滤，输出 markdown 列表。
# LOGS 形如 "短|subject"（与 main 流程相同）；每行按类型前缀去 scope 后输出；bucket 恒输出节头（即使空）。
render_bucket() {
  local bucket="$1" title="$2" etitle="$3"
  echo
  echo "## $title ($etitle)"
  echo
  local short rest re t show
  re='^[A-Za-z]+(\([^)]*\))?:'
  while IFS='|' read -r short rest; do
    [[ -n "$rest" ]] || continue
    t="${rest%%:*}"
    [[ "$(type_bucket "$t")" == "$bucket" ]] || continue
    show="$rest"
    # 去类型前缀，保留括号内的 scope（如 feat(foo):）
    if [[ "$rest" =~ $re ]]; then
      show="${rest#*: }"; [[ "$show" == "$rest" ]] && show="${rest##*:}"
    fi
    echo "- $show (\`\`\`$short\`\`\`)"
  done <<< "$LOGS"
}

self_test() {
  echo "== release-notes self-test =="
  local failures=0
  # ① 映射函数：每个 conventional 类型/scope 变体应归入预期 bucket（"" 表示丢弃）。
  check() {
    local expect="$1" t="$2" got
    got="$(type_bucket "$t")"
    if [[ "$got" != "$expect" ]]; then
      echo "FAIL: type_bucket('$t') = '$got', expected '$expect'" >&2
      failures=$((failures+1))
    fi
  }
  check feat  "feat: add"
  check fix   "fix: repair"
  check perf  "perf: speedup"
  check docs  "docs: write"
  check chore "chore: housekeep"
  check chore "build: compile"
  check chore "ci: pipeline"
  check chore "refactor: restructure"
  check chore "style: format"
  check chore "refactor(program): split main"
  check chore "style(standards): bump analyzers"
  check feat  "feat(foo): scoped"
  check ""    "test: unit"
  check ""    "revert: undo"
  check ""    "wip: stuff"
  check ""    "no-prefix subject"

  # ② 集成：合成 LOGS 走一遍渲染 — refactor/style 进「构建 · CI · 其他」，未知类型不进任何节。
  #（render_bucket 会剥掉类型前缀，故断言主体而不是带前缀的 subject）
  local out
  LOGS="$(printf '111|feat: add feature\n222|refactor: restructure\n333|style: format\n444|test: unit only')"
  out="$(render_bucket chore "构建 · CI · 其他" "Build · CI · Other")"
  local expected=("restructure" "format")
  local e
  for e in "${expected[@]}"; do
    if ! grep -qF "$e" <<< "$out"; then
      echo "FAIL: '构建 · CI · 其他' 缺少 '$e'" >&2
      failures=$((failures+1))
    fi
  done
  if grep -qF "test: unit only" <<< "$out"; then
    echo "FAIL: 未知类型 'test:' 不应进入任何 bucket" >&2
    failures=$((failures+1))
  fi
  out="$(render_bucket feat "新增" "New Features")"
  if ! grep -qF "add feature" <<< "$out"; then
    echo "FAIL: feat 未进入 '新增'" >&2
    failures=$((failures+1))
  fi

  if [[ "$failures" -eq 0 ]]; then
    echo "== release-notes self-test passed =="
  else
    echo "== release-notes self-test failed ($failures) ==" >&2
    exit 1
  fi
}

# --self-test：离线自检，不依赖 git / tag。
if [[ "${1:-}" == "--self-test" ]]; then
  self_test
  exit 0
fi

FROM_REF="${1:-}"
TO_REF="${2:-HEAD}"
if [[ -z "$FROM_REF" ]]; then
  # 上一个可达 tag（<to 或 <=to 之前的 tag）
  FROM_REF="$(git -C "$ROOT" describe --tags --abbrev=0 "$TO_REF^" 2>/dev/null || echo "")"
  if [[ -z "$FROM_REF" ]]; then FROM_REF="$(git -C "$ROOT" rev-list --max-parents=0 HEAD 2>/dev/null || echo "")"; fi
fi
[[ -n "$FROM_REF" ]] || { echo "error: 无法确定比较起点（from_ref）" >&2; exit 1; }

# 断言：to_ref 必须能从 from_ref 演进（避免向上比较返回空）
if ! git -C "$ROOT" merge-base --is-ancestor "$FROM_REF" "$TO_REF" 2>/dev/null; then
  echo "error: $FROM_REF 不是 $TO_REF 的祖先（范围无效）" >&2; exit 1
fi

LOGS="$(git -C "$ROOT" log --no-merges --format='%h|%s' "$FROM_REF..$TO_REF" 2>/dev/null || true)"

# 首行：标题
echo "# Release $TO_REF"
echo
echo "DeepSeek Harness Desktop 桌面客户端发布包。安装包见下方 Assets；校验见 \`SHA256SUMS\`。"
echo

render_bucket feat  "新增" "New Features"
render_bucket fix   "修复" "Bug Fixes"
render_bucket perf  "优化" "Performance"
render_bucket docs  "文档" "Docs"
render_bucket chore "构建 · CI · 其他" "Build · CI · Other"

echo
echo "**Full Changelog**: $REPO_URL/compare/$FROM_REF...$TO_REF"
