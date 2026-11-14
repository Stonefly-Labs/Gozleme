---
name: validate-demo
description: Drive reproducible running Gözleme demos, browser assertions and sanitized PR screenshots.
---

# validate-demo

Start complete Compose stack and needed local dependencies, migrations, deterministic synthetic seeds; run smoke/integration tests. Prefer browser automation (Playwright) to navigate actual UI; assert feature behavior, errors and relevant tenant-security negatives. Capture real labeled PNGs and save to current spec's `evidence/screenshots` with stable relative paths; do not use generated/stock imagery or expose secrets, private external data or tokens. If no meaningful UI, capture structured CLI/API/log evidence. Write demo report with environment, tested revision, exact commands, assertions, links and limitations; embed actual PNG links in PR body and attach bulky traces as CI artifacts. Mark blocked rather than inventing success when environment inaccessible. Do not merge PRs.
