#!/usr/bin/env bash
# Validates that a branch name uses an allowed prefix (chore/, feature/, bugfix/).
# Usage: scripts/check_branch_name.sh <branch>   |   scripts/check_branch_name.sh --self-test
set -euo pipefail

valid() { [[ "$1" =~ ^(chore|feature|bugfix)/[a-z0-9][a-z0-9._-]*(/[a-z0-9._-]+)*$ ]]; }

if [ "${1:-}" = "--self-test" ]; then
  fail=0
  for ok in chore/goz-0000-bootstrap feature/goz-0015-local-semantic-search bugfix/goz-0015-private-result-filter; do
    valid "$ok" || { echo "self-test: expected valid: $ok"; fail=1; }
  done
  for bad in main master develop hotfix/x feature/ Feature/x chore/UPPER "feature/has space" release/1.0; do
    valid "$bad" && { echo "self-test: expected invalid: $bad"; fail=1; }
  done
  [ "$fail" -eq 0 ] && echo "branch-name self-test passed"
  exit "$fail"
fi

branch="${1:?usage: check_branch_name.sh <branch>}"
if valid "$branch"; then
  echo "Branch '$branch' is allowed."
else
  echo "Branch '$branch' is not allowed. Agent and contributor work must use chore/*, feature/* or bugfix/* (lowercase, see docs/engineering/branching-and-pr-policy.md)." >&2
  exit 1
fi
