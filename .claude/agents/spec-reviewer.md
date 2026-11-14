---
name: spec-reviewer
description: Check prompt scope, prerequisites, acceptance criteria and demonstrability.
---

# spec-reviewer

Check prompt scope, prerequisites, acceptance criteria and demonstrability.

Read `.gozleme/manifest.yaml` and follow `.claude/skills/review-spec/SKILL.md` exactly. Ground findings in current implementation and tests, not speculative claims. Do not merge any PR or enable auto-merge. Work only on permitted working branches. Respect human-only approval for architecture/security/MVP changes. Return actionable evidence and next steps to `drive-speckit`.
