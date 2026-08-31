---
name: dsh-prose-standard
description: Use when writing, reviewing, restoring, trimming, or auditing prose in this repo (dotnet-deepseek-harness-desktop) — deciding where documentation or comments are required across Markdown, XML doc (`<summary>/<param>/<returns>`), code and test comments, prompts, descriptions, diagnostics, and CLI/UI strings. This repo is .NET (XML doc, not JSDoc) and bilingual (中文主文档 + .en.md mirrors for README/user-guide/faq).
---

# Prose Standard In This Repo

**This skill is guidance, not a script.** Write enough to preserve the contract, then remove reasoning transcripts, repetition, and decoration. A contract is an obligation, invariant, precondition, postcondition, or compatibility promise that a caller, callee, implementer, producer, or consumer relies on. This repo is .NET: public-API contracts go in XML doc (`<summary>/<param>/<returns>`), not JSDoc; durable prose follows the root「文档纪律」and the doc placement tiers. Use [dsh-doc-standards](../dsh-doc-standards/SKILL.md) for placement, budgets, and documentation gates; use [dsh-trim-cot-leakage](../dsh-trim-cot-leakage/SKILL.md) for hunting reasoning-transcript leakage.

## Sources of truth (read, don't re-summarize)

- Root [AGENTS.md](../../../AGENTS.md)「文档纪律」 — every fact has one home (rationale → Agent Notes, procedure → cookbook, contract → README, rule → AGENTS); durable docs write current state, not change history; relative links + machine-checkable; no bare filenames.
- [docs/coding-standards.md](../../../docs/coding-standards.md) — naming/format conventions and the XML doc contract (`<summary>/<param>/<returns>`) for public API;「行为契约」for async/exception/logging.
- [docs/architecture-standards.md](../../../docs/architecture-standards.md) — R1/R2/R3 (compose root, dependency direction, boundary abstraction); prose must not contradict the layer map.

## Preserve the complete proposition

Before editing, identify every proposition in the passage. Preserve each relevant actor/action, condition/timing/ordering, modality (must/may/never), negative guarantee and exception, and ownership/side-effect/failure-mode/consequence. Remove adjectives, repetition, and narration only when every factual clause survives and the result is clearer — a smaller word count alone is not an improvement.

Keep a complete local contract at the point of use. Aggressively link to the owning document for architecture, rationale, algorithms, history, or extended examples. One explanation has one home; essential contract facts may repeat locally.

## Required coverage by location

This is not a one-way shortening pass. Add or restore prose when code, types, and structure do not communicate a required contract below. Do not add a comment when those facts are already obvious locally.

- **Public XML doc (`<summary>/<param>/<returns>`):** document caller-visible return distinctions, throws/rejections, side effects, ownership, timing, cancellation, durability.
- **Internal comments:** orient non-local structure and obviously complicated local structure — invariants, race ordering, ownership, security boundaries, surprising failure behavior. Delete control-flow narration and code restatement.
- **Tests:** explain only non-obvious test design—why a fixture, assertion, platform accommodation, real entry path, or indirect observation is necessary. Delete walkthroughs and inventories.
- **Cookbook (`docs/cookbook.md`):** include prerequisites, required actions, the real entry path, observable verification, concise warnings; each entry carries a stage label.
- **READMEs:** include the consumer contract: configuration, semantics, failures, limitations, and extension points. For bilingual (`README.md`) keep the `.en.md` mirror in the same change.
- **Agent Notes:** retain unique rationale, mechanisms, alternatives, consequences, shipped verification evidence, named coverage gaps. Implemented notes state shipped reality in present tense; remove planning checklists.
- **Prompts and visible strings:** treat wording as behavior. Inspect generated output and run behavior validation or state why no snapshot applies.
- **Diagnostics:** name the failing subject/path, violated rule, and correction when non-obvious. Remove internal execution narration.

## Bilingual discipline (committed counterparts)

This repo keeps `.md` ↔ `.en.md` counterparts for `README`, `user-guide`, and `faq`. Editing either side obligates the counterpart in the same change. Preserve stable product-visible text verbatim; compare meaning and terminology on both sides — a green link/pair check does not prove translation quality.

## Workflow

1. Require an explicit `scope`. If it is missing, report the required input and stop — do not infer a repository-wide scope. Then confirm the mode (`automatic | interactive`, default automatic; `mode` controls questions, not write authority — review/audit tasks report findings without editing) and branch/PR base. Inspect only the requested scope, not the whole repo.
2. Read the owning document and code before judging a passage. For calibration read the ownership docs above.
3. Classify each candidate as keep/add/trim/restore/restructure/defer; apply clear changes only when the task authorizes edits.
4. Update the owner before derivative artifacts; re-check analogous passages after learning a new rule.
5. Run the narrow relevant gates (`verify-md-links`, `verify-doc-budgets`, `git diff --check`) and, for public API doc changes, `dotnet build` (0 warnings). Note: the main project does **not** open `GenerateDocumentationFile`/CS1591 — public-API XML doc is required by root `AGENTS.md`「编码约定」and is enforced by code review, not a build gate.
6. Report the inspected scope, clear changes, deliberate keeps, deferred cases, and checks actually run.

## Borderline decisions

A case is borderline only when at least two versions satisfy the complete-proposition rule but trade accepted principles, and this skill does not already resolve the tradeoff. In automatic mode, apply clear edits when authorized and report genuine borderline cases without asking questions — do not weaken a proposition to make progress. In interactive mode, group analogous passages under the governing principle, present two or three viable versions, recommend one, and state the factual or structural difference. Do not offer inferior distractors.
