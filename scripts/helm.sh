#!/usr/bin/env bash
# Runs helm locally if installed, otherwise via a pinned container image.
set -euo pipefail
if command -v helm >/dev/null 2>&1; then
  exec helm "$@"
fi
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
exec docker run --rm -v "$ROOT:/work" -w /work alpine/helm:3.19.0 "$@"
