---
name: review-roadmap
description: Reconcile spec prompts, YAML registry, generated Markdown and implementation impact finding evidence.
---

# review-roadmap

Load manifest, YAML registry and current PR branch; inspect changed living prompts, dependency DAG and versioned impact findings. Maintain authoritative `.gozleme/spec-registry.yaml`; update prompt revision metadata, dependencies, status, gates, blocker/resume fields only with evidence. Run `python3 scripts/generate_register.py` and `python3 scripts/validate_docs.py`. Never hand-edit generated Markdown; CI should run `python3 scripts/generate_register.py --check`. Only human-merged specs can be marked complete, and agent must never merge or enable auto-merge. Maintain historical approved specs by additive amendments rather than retroactive edits; escalate material scope or invariant changes.
