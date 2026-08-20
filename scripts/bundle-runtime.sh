#!/usr/bin/env bash
# bundle-runtime.sh — thin wrapper to the canonical CI path (pilot-harness 整树方案).
# 历史上的 NODE_SRC/DSH_SRC 本地快拷已折叠，统一走 bundle-runtime-ci.sh 以保输出一致（含 dshmarket.tgz 497K 真包校验）。
# 用法：bash scripts/bundle-runtime.sh [linux-x64]  →  exec bundle-runtime-ci.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# 本地默认把 pnpm store 放工作区可写目录（沙箱 /home 只读时用默认 $HOME 会失败）；
# CI 由 workflow 显式设 PNPM_STORE_DIR 指向 actions/cache 缓存路径。
export PNPM_STORE_DIR="${PNPM_STORE_DIR:-$ROOT/.cache/pnpm-store}"
mkdir -p "$PNPM_STORE_DIR"
exec bash "$ROOT/scripts/bundle-runtime-ci.sh" "${1:-linux-x64}"
