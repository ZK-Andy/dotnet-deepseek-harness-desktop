---
name: dsh-pre-push-checks
description: Use before pushing, force-pushing, marking ready for review, or claiming checks pass on this repo (dotnet-deepseek-harness-desktop) to select the smallest tests and checks that cover the outgoing or just-published diff without reflexively running the full repository suite. Aligns with the root AGENTS.md Git discipline (hooks fast / CI exhaustive, --force-with-lease, change-scope).
---

# Pre-Push Checks In This Repo

**This skill is guidance, not a script.** Run relevant local evidence once before a push, then stop. Git hooks are intentionally narrow (fast checks); CI owns exhaustive coverage and the platform matrix. This repo is .NET (single project solution) — the build/test/verify toolchain is `dotnet`, not pnpm. The upstream `gh stack sync` / stacked-PR flow does **not** apply (this repo merges sequentially).

## Sources of truth

- Root [AGENTS.md](../../../AGENTS.md)「Git 纪律」 — `--force-with-lease`, raw `--force` forbidden, change-scope as standard front-check, hooks fast / CI exhaustive.
- [scripts/change-scope.sh](../../../scripts/change-scope.sh) — compute the change scope (git merge-base + diff --name-only).
- [docs/testing.md](../../../docs/testing.md) — test tiers/gates.
- The machine gates in root `AGENTS.md`「质量门」 (`verify-*.py`).

## Inspect the outgoing change

1. Confirm checkout and branch:
   `git status --short --branch`
   `git rev-parse --show-toplevel`
2. Verify the PR base or stack parent, fetch that ref, and inspect the complete scope:
   `scripts/change-scope.sh <base> <head>`
   Never guess/fetch the base; supply the verified ref. After a base merge or retarget, re-run the report and reassess which checks the combined scope invalidates.

## Workflow — Select relevant evidence

There is no universal local baseline beyond the hooks. Every behavior change needs the narrowest available test or purpose-built check that would fail for its regression; add broader checks only for surfaces the diff actually reaches.

- **Code/behavior change:** run `dotnet build` (0 warnings) and the owning `dotnet test` filter or focused tests for the changed source. Leave repository-wide coverage to CI unless the change is genuinely cross-cutting.
- **Documentation, Agent Notes, catalogs, or doc-linked comments:** run the relevant `<repo>/scripts/verify-*.py` gates (`verify-adr-format`, `verify-doc-budgets`, `verify-md-links`, `verify-cookbook`, `verify-handoff-structure` as touched) and `git diff --check`.
- **Skill changes:** run `python3 scripts/verify-skill-format.py`.
- **Config (.editorconfig / csproj / workflow / scripts):** run `dotnet format --verify-no-changes`, and the relevant `.github/workflows/**` path (which must be exercised on a real runner).

Do not manually repeat a passing check merely because commit or push follows. Report pending CI checks as pending; inspect failures before attributing to branch or environment.

## Protect history-rewriting pushes

Rebase is allowed for standalone branches (including after review). Before a rewrite, fetch the current remote branch and record its exact OID; publish with `--force-with-lease=<branch>:<observed-oid>` so a concurrent update aborts the push. Raw `--force` is never allowed. After any rewritten push, fetch the live heads again and re-audit unresolved review threads, approvals, mergeability, and checks — hashes/anchors from before the rewrite are not current evidence.

## Handle failures

If a relevant check fails before an ordinary push, stop and fix or explain the blocker. Do not push and hope CI differs. If a failure looks environment-specific, prove it: record the exact command, failing test, and platform-specific mismatch; confirm the relevant non-platform evidence; prefer fixing cross-platform nondeterminism where the check is required. Bypass a local hook only when the user explicitly asks, and report exactly what failed and why CI is expected to differ.

## Push procedure

1. Run the selected relevant checks once.
2. Commit normally and inspect any files changed by the pre-commit hook before continuing.
3. Push normally, or use the exact lease for an authorized rewritten branch, so the pre-push hook runs.
4. Verify the remote ref matches local HEAD: `git rev-parse HEAD origin/$(git branch --show-current)`.
5. For GitHub PRs, inspect remote CI: `gh pr checks`.

Report pending checks as pending; never claim a push is clean on a green gate the diff did not actually reach.
