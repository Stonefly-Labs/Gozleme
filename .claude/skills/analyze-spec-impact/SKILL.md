---
name: analyze-spec-impact
description: Mandatory post-convergence implementation-grounded review of downstream living Gözleme spec prompts.
---

# analyze-spec-impact

Invoke automatically from `drive-speckit` **after convergence, before PR readiness**. Read current implementation at precise commit, spec, plan, tasks, tests, migrations, OpenAPI/contracts, demo evidence, baseline and remaining `.gozleme/spec-registry.yaml` prompts. Inspect directly and transitively dependent future specs plus contract consumers. Record stable finding IDs, severity, verified evidence, exact source and affected spec IDs, and recommended patch.

**Authority:** May directly edit **future unimplemented** `docs/prompts/spec-nnnn.md`, prerequisites, interfaces, demo/acceptance text and dependency order to reflect implemented evidence *within* confirmed architecture and MVP scope. Record prompt revision/change provenance and proposed contract diffs; these edits commit only on permitted PR branch, never main. For confirmed architectural decisions, security guarantees, committed scope and completed specs: human approval required; do not silently modify. Fixing a violation of architecture requires a corrective implementation iteration, not rewriting requirements to excuse it.

Write `docs/specs/<current>/reviews/impact-analysis.md` and structured `impact-findings.json` when native Spec Kit workspace permits; otherwise use actual current spec workspace path and register it. Invoke Roadmap Steward to synchronize state, check all downstream readers, enforce no cycles. No empty assertion of success without inspected evidence. Must not merge PRs or enable auto-merge. If analyzer finds defect requiring code repair, hand back to drive-speckit for bounded correction, then analyze updated state again.
