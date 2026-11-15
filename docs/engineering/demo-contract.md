# Mandatory autonomous demonstration contract

Every completed spec must leave cumulative stack runnable with local Docker Compose and documented commands, deterministic seeded synthetic sample data and no cloud requirement by default. The demo validator starts stack, waits for health, runs migrations/seed, drives real browser UI via Playwright where feasible, performs UI/API assertions including relevant negative tenancy/security tests, captures meaningful **actual** screenshots, and records run metadata (commit, branch, environment, timestamps, commands, result, evidence paths). Screenshots must not include credentials, API keys, tokens or real customer/private data. Capture screenshots only when useful; for genuinely headless specs attach recorded API/log/test evidence instead. No fabricated/recycled imagery. Playwright traces/videos belong to CI artifacts where bulky; curated PNGs under `docs/specs/<spec>/evidence/screenshots/` as supported by installed Spec Kit location and generated output guidance. Reference PNGs in PR Markdown so reviewers can see them. If a browser or stack is unavailable, explicitly mark demo blocked with reproducible manual steps, never falsely record success. Screenshots are supplemental to assertions, not evidence of correctness by themselves. Existing demonstrations must remain operational.

## Cumulative runner

`make demo SPEC=GOZ-NNNN` (`scripts/demo/run-demo.sh GOZ-NNNN`) starts the stack, runs the demo
seed (`docker compose --profile demo run --rm seed`), then runs Playwright project `goz-NNNN`, whose
project dependencies replay every earlier spec's demo first. Earlier specs write their screenshots
to the current spec's `evidence/screenshots/regression-goz-MMMM/`, never to their own merged
evidence. The run record is the current spec's `evidence/demo-run.json`; the secret scan covers
database and demo passwords, the synthetic demo account emails and session cookie values.
