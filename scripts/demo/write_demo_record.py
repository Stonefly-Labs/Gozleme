#!/usr/bin/env python3
"""Write evidence/demo-run.json from Playwright JSON results and scan evidence for secrets.

Exits non-zero (and records result=failed) if any forbidden string is found in captured logs or
text evidence. Never upgrades a failed/blocked run to passed.
"""
import argparse, json, os, platform, subprocess, sys
from pathlib import Path

FORBIDDEN = [
    os.environ.get("GOZLEME_APP_PASSWORD", "local-dev-app"),
    os.environ.get("GOZLEME_MIGRATOR_PASSWORD", "local-dev-migrator"),
    os.environ.get("GOZLEME_DB_SUPERUSER_PASSWORD", "local-dev-superuser"),
    os.environ.get("GOZLEME_GRAFANA_ADMIN_PASSWORD", "local-dev-grafana"),
    "Password=",
]


def sh(*cmd: str) -> str:
    try:
        return subprocess.run(cmd, capture_output=True, text=True, check=False).stdout.strip()
    except OSError:
        return "unavailable"


def assertions(results: Path) -> list[dict]:
    if not results.exists():
        return []
    data = json.loads(results.read_text())
    out = []

    def walk(suite):
        for spec in suite.get("specs", []):
            outcomes = [r.get("status") for t in spec.get("tests", []) for r in t.get("results", [])]
            out.append({"name": spec["title"], "result": "passed" if outcomes and all(o == "passed" for o in outcomes) else "failed"})
        for child in suite.get("suites", []):
            walk(child)

    for suite in data.get("suites", []):
        walk(suite)
    return out


def main() -> int:
    p = argparse.ArgumentParser()
    for a in ("spec", "evidence", "results", "log", "started", "finished", "result"):
        p.add_argument(f"--{a}", required=True)
    p.add_argument("--startup-seconds", type=int, default=None)
    p.add_argument("--tree-clean", choices=["true", "false"], required=True)
    p.add_argument("--commands", nargs="*", default=[])
    a = p.parse_args()
    root = Path(__file__).resolve().parents[2]
    evidence = Path(a.evidence)

    log_text = Path(a.log).read_text(errors="replace") if Path(a.log).exists() else ""
    leaks = sorted({s for s in FORBIDDEN if s and s in log_text})
    checks = assertions(Path(a.results))
    result = a.result
    if leaks or (result == "passed" and (not checks or any(c["result"] != "passed" for c in checks))):
        result = "failed"
    checks.append({"name": "secret scan of compose logs and status evidence", "result": "failed" if leaks else "passed"})

    shots = sorted(str(p.relative_to(root)) for p in (evidence / "screenshots").glob("*.png"))
    record = {
        "specId": a.spec,
        "commit": sh("git", "rev-parse", "HEAD"),
        "branch": sh("git", "rev-parse", "--abbrev-ref", "HEAD"),
        "workingTreeCleanAtStart": a.tree_clean == "true",
        "environment": {
            "os": f"{platform.system()} {platform.release()} {platform.machine()}",
            "docker": sh("docker", "version", "--format", "{{.Server.Version}}"),
            "compose": sh("docker", "compose", "version", "--short"),
            "node": sh("node", "--version"),
        },
        "startedAt": a.started,
        "finishedAt": a.finished,
        "stackStartupSeconds": a.startup_seconds,
        "commands": a.commands,
        "result": result,
        "assertions": checks,
        "evidence": shots,
    }
    if leaks:
        record["secretScanFindings"] = [f"forbidden value #{FORBIDDEN.index(s)} found in compose logs" for s in leaks]
    (evidence / "demo-run.json").write_text(json.dumps(record, indent=2) + "\n")
    print(f"demo-run.json: result={result}, {len(checks)} assertions, {len(shots)} screenshots")
    return 0 if result == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
