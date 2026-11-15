#!/usr/bin/env bash
# Cumulative autonomous demo for one spec: start the stack, seed synthetic demo accounts, replay
# every earlier spec's demo and then this spec's with Playwright, scan evidence for secrets and
# write a run record. Earlier specs' screenshots go to this spec's `regression-goz-NNNN/` folders,
# so merged evidence is never overwritten (IF-08).
# Usage: scripts/demo/run-demo.sh [GOZ-NNNN]   (or `make demo SPEC=GOZ-NNNN`; default GOZ-0001)
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SPEC="${1:-GOZ-0001}"
cd "$ROOT"
if ! [[ "$SPEC" =~ ^GOZ-([0-9]{4})$ ]]; then
  echo "Usage: $0 [GOZ-NNNN]" >&2
  exit 2
fi
NUM=$((10#${BASH_REMATCH[1]}))
SPEC_DIR="$(ls -d docs/specs/"$(printf '%03d' "$NUM")"-* 2>/dev/null | head -1)"
if [ -z "$SPEC_DIR" ]; then
  echo "No docs/specs directory for $SPEC" >&2
  exit 2
fi
PROJECT="$(echo "$SPEC" | tr '[:upper:]' '[:lower:]')"
EVIDENCE="$ROOT/$SPEC_DIR/evidence"
TMP="$ROOT/.demo-tmp"
for ((n = 0; n < NUM; n++)); do
  prior="$(printf 'GOZ_%04d' "$n")"
  export "GOZLEME_DEMO_SHOTS_$prior=$EVIDENCE/screenshots/regression-$(printf 'goz-%04d' "$n")"
done

STARTED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
# Tree state at start, ignoring the evidence this run is about to regenerate.
TREE_CLEAN=$([ -z "$(git status --porcelain -- . ':(exclude,glob)docs/specs/*/evidence/**')" ] && echo true || echo false)
mkdir -p "$TMP" "$EVIDENCE/screenshots"
rm -rf "$EVIDENCE"/screenshots/*.png "$EVIDENCE"/screenshots/regression-* "$TMP"/*.json "$TMP"/*.log

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
  [ "$NUM" -ge 1 ] && { run docker compose --profile demo run --rm seed >/dev/null || status=$?; }
  GOZLEME_DEMO_RESULTS="$TMP/playwright-results.json" run pnpm --filter @gozleme/e2e exec playwright test --project "$PROJECT" || status=$?
  result=$([ "$status" -eq 0 ] && echo passed || echo failed)
fi

docker compose logs --no-color > "$TMP/compose.log" 2>&1 || true
COMMANDS+=("docker compose logs --no-color")

FINISHED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
python3 "$ROOT/scripts/demo/write_demo_record.py" \
  --spec "$SPEC" --evidence "$EVIDENCE" --results "$TMP/playwright-results.json" \
  --log "$TMP/compose.log" --started "$STARTED_AT" --finished "$FINISHED_AT" --result "$result" --startup-seconds "$STARTUP_SECONDS" --tree-clean "$TREE_CLEAN" \
  --commands "${COMMANDS[@]}"
record_status=$?

if [ "$status" -ne 0 ] || [ "$record_status" -ne 0 ]; then
  echo "DEMO $result (exit $status, record $record_status)" >&2
  exit 1
fi
echo "DEMO passed — evidence in $SPEC_DIR/evidence"
