---
name: dsh-code-review
description: Use when reviewing a pull request or a batch of changes in this repo (dotnet-deepseek-harness-desktop) — orients the reviewer to this project's standards (root AGENTS.md review checklist, coding-standards behavior contract, architecture-standards R1/R3/IPC, the feature-flow step-5 review contract) and the semantic checks that machine gates cannot cover. Run before merging non-trivial changes (the "三重审核 / 留评审" path).
---

# Reviewing A Change In This Repo

**This skill is guidance, not a script.** It supplies the semantic review the machine gates cannot: name a defect, location, impact, evidence. Prioritize correctness, lifecycle, security, and broken required behavior over style; a short review with one substantiated blocker beats a list of nits.

## Sources of truth (read, don't re-summarize)

- [root AGENTS.md](../../../AGENTS.md) — standing rules and the「评审检查项（AI 兜底）」checklist (D001–D003 / R1 / R3 / IPC).
- [docs/coding-standards.md](../../../docs/coding-standards.md)「行为契约」 — async tails, cancellation, exception policy, logging (the project's behavior contract; there is no `docs/defensive-patterns.md` here).
- [docs/architecture-standards.md](../../../docs/architecture-standards.md) — R1 compose-root discipline, R2 dependency direction, R3 boundary abstraction, IPC frame contract.
- [.agents/workflows/feature-flow.md](../../workflows/feature-flow.md) step 5 — the review execution contract (trigger enumeration, scope narrowing, bounded parallelism, deterministic report contract, "留评审" items).
- [.agents/notes/README.md](../../notes/README.md) — Agent Note rules (design-rationale home: what earns a note, the Alternatives mandate, and revision/archive discipline).
- [docs/testing.md](../../../docs/testing.md) — test tiers/gates; behavior-level changes carry regression/snapshot.

## Establish the exact scope

1. Confirm the branch and base ref from remote/stack state, not by guessing. Compute the scope with `scripts/change-scope.sh <base> <head>`; re-run after a retarget or base merge. The report names touched paths and dirty layers; it does not replace semantic review.
2. Do not start by reading the whole repo standard surface. This is a single-project .NET desktop shell: sub-tree rules live in the root `AGENTS.md` and the docs above, not in a `packages/` tree (no `packages/AGENTS.md`, `defensive-patterns.md`, `subsystems/`, or `docs/i18n/` exist here).

## Workflow — Review the diff

1. Confirm the scope and base ref (above), then read the diff against the project's own architecture.
2. Apply the semantic checks below; this repo is a .NET single-project desktop shell (Ryn webview + spawned dsh Node process + bundled companion plugin). The upstream deepseek-harness concepts (`packages/*/src`, Cordis, schemastery, pnpm, `knip`, VitePress, stacked PR) **do not apply** — judge against the project's own architecture (`docs/architecture.md`) and layers (compose root / Services / subdomains / external boundaries).

- **Intent and interface contracts:** trace both sides of every changed interface; confirm the implementation matches the PR and any Agent Note, including errors, cancellation, ownership, and disposal.
- **Lifecycle and concurrency:** for async setup, callbacks, processes, or teardown, apply the behavior-contract section of `coding-standards.md`. Check races before publication, cancellation during awaits, independent error reporting, callback containment, ownership before reentry, complete detach cleanup, and quiescent disposal.
- **Capability and consumer fit:** trace every current consumer; flag consumer-specific behavior leaking into the interface. Flag a new public method on a generic service whose only caller is one internal consumer.
- **Scope, ownership, and necessity:** map each abstraction, state machine, option, defensive copy, and compatibility path to its current contract, production consumer, and owning service. Challenge unrelated features and speculative generality.
- **Configuration and public choices:** ask what current-consumer evidence or prior art supports each default, public operation set, format, or imported external concept.
- **Boundary integrity (R3):** external interactions (Ryn/native, the dsh process, companion IPC, file/network, update feed, registry/rc) go through interfaces; nothing leaks into the business layer.
- **Borrowed and derived state:** determine whether each retained value is borrowed or owned under the service contract, then trace notifications and every cache, prompt, UI echo, replay, and query view to the documented success point and authoritative source (applies to config/event frames and GUI echoes here).
- **Real entry path:** tests exercise the shipped compose root / entry (`DesktopBootstrap` / `DesktopBootstrap.Startup`) where relevant. A hand-mounted or mock-only test can bypass real startup wiring — do not let a test claim coverage of a path it never actually enters.

## Required review (本项目留评审项)

The machine gates do not cover the items below; the review must check them explicitly (root `AGENTS.md`「评审检查项（AI 兜底）」):

- **D001** — `async` methods end in `Async`; **D002** — no `async void` outside event handlers; **D003** — empty `catch` names what it swallows.
- **R1** — compose root (`DesktopBootstrap` / `DesktopBootstrap.Startup`) stays wiring-only, no business/domain logic.
- **R3** — external boundaries go through interfaces, not direct into the business layer.
- **IPC strong types** — cross-boundary/event-frame IDs are not bare `string`; frames come from `AppJsonContext` source-gen.

## Report findings

State the defect, location, impact, and evidence. Place a localized defect inline on the tightest relevant diff range; use a PR-level comment for cross-cutting architecture, scope, or review-wide synthesis. Separate blockers from suggestions and omit issues already enforced by a green gate. When receiving review, verify each claim and fix or rebut it on technical grounds without performative agreement.
