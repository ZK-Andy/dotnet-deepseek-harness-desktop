---
name: dsh-find-simplifications
description: Use when working in this repo (dotnet-deepseek-harness-desktop) to find non-obvious simplification candidates, write proposed Agent Notes or inline TODO/FIXME/XXX notes, audit or coalesce superseded Agent Notes, or fold worthwhile simplification ideas from another PR — especially dead, duplicated, speculative, over-built, added-then-removed, or hand-rolled-where-a-dependency-exists surfaces. This repo is a single-project .NET desktop shell, not a multi-package monorepo.
---

# Finding Simplifications In This Repo

**This skill is guidance, not a script.** Turn a broad "find things to simplify" request into evidence-backed Agent Notes that remove or collapse existing surface area. Follow the code, keep judgment active, and prefer a few well-proven candidates over a pile of thin guesses. The upstream deepseek-harness concepts (`packages/*/src`, Cordis, schemastery, pnpm, `knip`, Node builtins, `examples/*/src`, twin adapter/dual-persistence) do **not** apply here — judge against this .NET desktop shell.

## Sources of truth

- [root AGENTS.md](../../../AGENTS.md) — conventions (fail loud, strong/branded IDs, config-not-hardcoded) and the「编码约定」review checklist.
- [docs/architecture-standards.md](../../../docs/architecture-standards.md) — R1 compose-root discipline, R2 dependency direction, R3 boundary abstraction; a simplification must beat the layer map.
- [docs/coding-standards.md](../../../docs/coding-standards.md)「行为契约」 — behavior contract; a simplification must not change observable behavior.
- [.agents/notes/README.md](../../notes/README.md) — Agent Note rules (what earns a note, the deletion rule, Alternatives mandate).

## What counts as a strong candidate

A strong simplification removes, folds, or demotes something real and has clear evidence that the current design costs more than it buys:

- A public method, event, config knob, helper, or test artifact has no production consumer.
- Tests or docs are the only consumers, and the behavior they pin is not load-bearing.
- Two representations mirror the same fact, especially across durable session events and transient agent events.
- A seam has methods every implementation must support but no consumer uses.
- Hand-rolled code reimplements what a well-maintained external package or a .NET BCL facility already provides, and the swap deletes the implementation plus its dedicated tests.
- Duplicated logic across `Services/` that could collapse to one home without changing behavior.
- A god object / oversized file that violates the size health gate (`verify-code-health.py`) and could be split without behavior change.

Thin candidates are not enough: deleting one typo, a trivial rename, or flagging "this looks complex" without call-site proof.

## Prefer the .NET body

When deciding whether a hand-rolled implementation should be replaced by a dependency, prefer what the .NET runtime/BCL already offers (built-in collections, `System.Text.Json`, `Task`/`ValueTask`, cancellation primitives) over pulling a new package. A dependency swap must beat the recorded rationale, not just cite the policy.

## Workflow — Prove or reject each candidate

1. Classify consumers before writing. Production corpus: `src/DeepSeek.Harness.Desktop/**` (`Services/`, compose root). Non-production corpus: tests, `docs/`, Agent Notes. A candidate with a production caller is a feature decision, not a cleanup.
2. Read the call sites, not just grep. Use `rg` for the symbol/event/config key/method name, then trace consumers. Confirm the simplified behavior is genuinely equivalent (and easier to explain) before proposing.
3. Reject or downgrade when:
   - A production caller exists and the simplification is a feature decision.
   - The API is explicitly justified by an implemented Agent Note or a hard-won defensive pattern, and the evidence does not beat that reason.
   - The removal forces unrelated churn without reducing the public API or required behavior.
   - The idea is correct but tiny — demote to a targeted `TODO(...)`/`FIXME` (see below).
4. Keep a boundary/invariant safe: do not collapse a documented backend/adapter, a shared persistence boundary (R3), or a hand-routed cancellation/ownership path that protects reentry.
5. **Audit trust and lifecycle boundaries:** for every defensive copy, freeze, validator, and callback capture, name where the value came from and who owns it next. Same-process typed service calls ordinarily borrow readonly values; parsers, config loaders, queues, workers, processes, and wire decoders own or validate their data. For complex async code, draw the ownership graph and map each sentinel, readiness promise, cancellation path, disposer, and state flag to a distinct owner or transition — propose one transaction/lifecycle controller when several mechanisms mirror the same liveness fact, and preserve separate machinery only where it protects synchronous publication/rollback, callback containment, first-terminal-outcome arbitration, worker/process ownership, or dispose-to-quiescence.

## Write the Agent Note

Create one file per durable proposal under `.agents/notes/<lifecycle>/<class>/yyyy-mm-dd-<topic>.md`, following the lifecycle/classification rules in `.agents/notes/README.md`. Prefer this structure, adjusting when the idea needs it:

- `# Agent Note: <action-oriented title>`
- `Status: proposed`
- `## Problem`: name the current API, cite the relevant files, state the consumer evidence (separate production callers from tests/docs).
- `## Proposal`: what to remove/fold/demote/rehome — include tests, docs, XML doc, snapshots, and generated-file cleanup when relevant.
- `## Why not keep it?` / `## What we give up`: make the strongest counterargument legible.
- `## Acceptance criteria`: observable end state and gates.
- `## Risks`: public API changes, behavior changes, future product wants, and why the tradeoff is still reasonable.

Be concrete enough that an implementing PR can follow the trail. When a proposal overlaps an existing note, consolidate the useful details into the existing one rather than creating a duplicate.

## Coalesce superseded Agent Notes

When the user asks to reduce the note corpus, audit `.agents/notes/implemented/`. Use [dsh-archive-agent-notes](../dsh-archive-agent-notes/SKILL.md) for retention judgment and archive mechanics. Follow the deletion rule in [.agents/notes/README.md](../../notes/README.md#何时写). Move every unique rationale/alternative/consequence into the surviving owner before deleting a superseded note.

## Inline TODO/FIXME notes

Use inline `TODO(...)` only for small, clearly-useful cleanups that are not durable design decisions. Keep them short and actionable: name the smell with a stable tag (`TODO(double-default)`), explain why it is safe to revisit and what action would simplify it. Do not add TODOs for speculative complaints or for behavior needing a decision.

## Validation

For docs-only Agent Note work, run `python3 scripts/verify-adr-format.py` and `git diff --check`. For code changes, run `dotnet build` (0 warnings) and `dotnet test` (all green), plus the machine gates the change touches (`verify-code-health.py`, `verify-code-conventions.py` when relevant). When opening a PR, summarize what was added/consolidated/deleted, the main areas surveyed, and what was intentionally excluded.
