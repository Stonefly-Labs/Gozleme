#!/usr/bin/env bash
# GOZ-0000 autonomous demo: start the cumulative stack, drive the dashboard with Playwright,
# induce real failures, scan evidence for secrets and write a run record.
# Usage: scripts/demo/run-demo.sh   (or `make demo`)
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SPEC_DIR="docs/specs/000-repository-bootstrap"
EVIDENCE="$ROOT/$SPEC_DIR/evidence"
TMP="$ROOT/.demo-tmp"
cd "$ROOT"

STARTED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
mkdir -p "$TMP" "$EVIDENCE/screenshots"
rm -f "$EVIDENCE"/screenshots/*.png "$TMP"/*.json "$TMP"/*.log

COMMANDS=()
run() { COMMANDS+=("$*"); echo "+ $*"; "$@"; }

status=0
up_started=$(date +%s)
run docker compose up -d --build --wait || status=$?
STARTUP_SECONDS=$(( $(date +%s) - up_started ))
if [ "$status" -ne 0 ]; then
  echo "Stack failed to start; demo BLOCKED" >&2
  result=blocked
else
  run pnpm install --frozen-lockfile >/dev/null || status=$?
  run pnpm --filter @gozleme/e2e exec playwright install chromium >/dev/null || status=$?
  GOZLEME_DEMO_RESULTS="$TMP/playwright-results.json" run pnpm --filter @gozleme/e2e run demo || status=$?
  result=$([ "$status" -eq 0 ] && echo passed || echo failed)
fi

docker compose logs --no-color > "$TMP/compose.log" 2>&1 || true
COMMANDS+=("docker compose logs --no-color")

FINISHED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
python3 "$ROOT/scripts/demo/write_demo_record.py" \
  --spec GOZ-0000 --evidence "$EVIDENCE" --results "$TMP/playwright-results.json" \
  --log "$TMP/compose.log" --started "$STARTED_AT" --finished "$FINISHED_AT" --result "$result" --startup-seconds "$STARTUP_SECONDS" \
  --commands "${COMMANDS[@]}"
record_status=$?

if [ "$status" -ne 0 ] || [ "$record_status" -ne 0 ]; then
  echo "DEMO $result (exit $status, record $record_status)" >&2
  exit 1
fi
echo "DEMO passed — evidence in $SPEC_DIR/evidence"
