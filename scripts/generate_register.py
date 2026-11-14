#!/usr/bin/env python3
"""Generate/check human-readable registry from authoritative YAML."""
from pathlib import Path
import argparse, yaml, sys
ROOT=Path(__file__).resolve().parents[1]
REG=ROOT/'.gozleme/spec-registry.yaml'; OUT=ROOT/'docs/roadmap/spec-register.md'
def render():
    reg=yaml.safe_load(REG.read_text())
    specs=reg['specs']; status={s['id']:s['status'] for s in specs}
    lines=['# Gözleme — Specification Register','','**Generated from `.gozleme/spec-registry.yaml`; do not edit directly.**','',f"**Specs:** {len(specs)} · **Complete on main:** {sum(s['status']=='complete' for s in specs)} · **Blocked:** {sum(s['status']=='blocked' for s in specs)} · **Ready for human merge:** {sum(s['status']=='ready-for-human-merge' for s in specs)}",'', '| Spec | Phase | Status | Stage | Dependencies | Next action |','|---|---|---|---|---|---|']
    for s in specs:
        deps=', '.join(s['dependencies']) or '—'
        title=s['title'].replace('|','/')
        nxt=s['nextAction'].replace('|','/')
        lines.append(f"| [{s['id']}](../prompts/spec-{s['number']:04d}.md) — {title} | {s['phase']} | {s['status']} | {s.get('stage') or '—'} | {deps} | {nxt} |")
    lines+=['','## Progress and governance','','- All branch and gate state is provisional until reviewed; `complete` only after verified **human** PR merge on protected `main`.','- Source documentation: `docs/decisions/decision-register.md`; per-position historical Round 2 verification remains pending.','- Post-convergence Spec Impact Analyst can update future unimplemented prompts in branch within approved architecture; Roadmap Steward syncs this report.','- Every spec includes demo and PR screenshot evidence where visually meaningful.','']
    return '\n'.join(lines)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--check',action='store_true'); a=p.parse_args(); expected=render()
 if a.check:
  if not OUT.exists() or OUT.read_text()!=expected:
   print('Generated register is stale. Run python3 scripts/generate_register.py');sys.exit(1)
  print('Human register synchronized')
 else:
  OUT.parent.mkdir(parents=True,exist_ok=True);OUT.write_text(expected);print(f'Wrote {OUT.relative_to(ROOT)}')
