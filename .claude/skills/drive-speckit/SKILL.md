---
name: drive-speckit
description: Orchestrate one Gözleme Spec Kit feature from prompt through demo, convergence, impact analysis and PR-ready handoff.
---

# drive-speckit

1. Read `.gozleme/manifest.yaml` and `.gozleme/spec-registry.yaml`, then `docs/prompts/const.md` if constitution isn't initialized and the requested `docs/prompts/spec-nnnn.md`. Validate prerequisite specs are merged; identify current stage from durable progress, not assumptions.
2. Verify current Git branch is `chore/*`, `feature/*`, or `bugfix/*`; if on main, create appropriate branch before edits. Never push main, merge PRs, enable auto-merge, or bypass rules.
3. Use **installed native Spec Kit** steps; never reinstall or invent its stages/templates. Track spec/plan/tasks/implementation progress in YAML and regenerate human register at meaningful transitions.
4. Implement bounded vertical slice, run tests and negative security tests, drive cumulative demo via `validate-demo`, capture real screenshots and PR evidence.
5. Run convergence review according to project process. Three **meaningful** repair attempts per distinct failure before recording blocker with evidence, owner and exact resume action; do not recursively launch converge from implement or spin forever.
6. **REQUIRED AUTOMATIC STEP:** after convergence success invoke `analyze-spec-impact` (prefer specialized `.claude/agents/spec-impact-analyst.md`), even if no downstream impacts are expected. The analyzer must actually inspect code/refs/evidence and report findings.
7. Agent-authorized future prompt edits are applied on working branch with evidence, findings IDs and revision metadata. Invoke `review-roadmap` to sync registry, generate human register and run validation. Escalate architecture/security/scope changes for human disposition. Revalidate after corrective loop.
8. Prepare PR with code/demo/screenshots/test evidence and findings. Mark `ready-for-human-merge` only with passing required gates; stop. Human alone merges. Do not mark `complete` until human merge independently verified.
