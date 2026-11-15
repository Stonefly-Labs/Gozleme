#!/usr/bin/env python3
"""Validate Gözleme document package and machine-readable spec governance."""
from pathlib import Path
import json,re,sys
import yaml,jsonschema
from generate_register import render
ROOT=Path(__file__).resolve().parents[1]
errors=[]
def fail(x):errors.append(x)
def checkpath(label,path):
 if not isinstance(path,str) or not (ROOT/path).is_file():fail(f'{label}: missing file {path}')
manifest=yaml.safe_load((ROOT/'.gozleme/manifest.yaml').read_text())
registry=yaml.safe_load((ROOT/'.gozleme/spec-registry.yaml').read_text())
for n,obj in [('manifest',manifest),('spec-registry',registry)]:
 schema=json.loads((ROOT/f'.gozleme/schemas/{n}.schema.json').read_text())
 try:jsonschema.Draft202012Validator(schema).validate(obj)
 except jsonschema.ValidationError as e:fail(f'{n} schema: {e.message}')
for group,keys in [('documentation',['architecture','security','decisions','traceability','consistencyReview']),('constitution',['prompt']),('specifications',['registry','humanRegister']),('engineering',['branching','demo','agentWorkflow'])]:
 for k in keys:checkpath(f'manifest {group}.{k}',manifest[group][k])
for k in ['generalInstructions','claudeInstructions']:
 checkpath(f'agents.{k}',manifest['agents'][k])
for k in ['manifest','registry']:
 checkpath(f'schemas.{k}',manifest['schemas'][k])
for flag in ['humanOnlyMerge','neverEnableAgentAutoMerge','requireWorkingDemo','requirePostConvergenceImpactAnalysis','allowAgentUpdatesToFuturePrompts','futurePromptEditsRequirePR','mainProtected']:
 if manifest['rules'].get(flag)!=True:fail(f'flag required true: {flag}')
if manifest['rules'].get('agentMayPushMain')!=False:fail('agents must not push main')
if set(manifest['rules']['allowBranchPrefixes'])!={'chore/','feature/','bugfix/'}:fail('branch prefixes inconsistent')
specs=registry['specs'];byid={}
for s in specs:
 if s['id'] in byid:fail('duplicate spec ID '+s['id'])
 byid[s['id']]=s
 if s['number']!=int(s['id'][-4:]):fail('ID-number mismatch '+s['id'])
 if s['prompt']!=f"docs/prompts/spec-{s['number']:04d}.md":fail('prompt convention '+s['id'])
 checkpath('prompt '+s['id'],s['prompt'])
 for d in s['dependencies']:
  if d==s['id']:fail('self dependency '+d)
 if s['status']=='complete':
  if s['gates'].get('merge')!='passed' or s['gates'].get('demo')!='passed' or s['gates'].get('impactAnalysis')!='passed':fail('invalid complete gate '+s['id'])
 if s['status']=='ready-for-human-merge' and any(s['gates'].get(k)!='passed' for k in ['convergence','demo','impactAnalysis','roadmapSync']):fail('incomplete readiness gates '+s['id'])
for s in specs:
 for d in s['dependencies']:
  if d not in byid:fail(f"{s['id']} unknown dependency {d}")
visited=set();active=set()
def visit(k):
 if k in active:fail('dependency cycle at '+k);return
 if k in visited:return
 active.add(k)
 for d in byid[k]['dependencies']:
  if d in byid:visit(d)
 active.remove(k);visited.add(k)
for sid in byid:visit(sid)
# 40 approved at package creation; later specs are added only by maintainer-approved roadmap changes.
if len(specs)<40:fail(f'expected at least 40 specs, got {len(specs)}')
if sorted(s['number'] for s in specs)!=list(range(len(specs))):fail('spec numbers must be contiguous from 0 with no duplicates')
for s in specs:
 if s['id']!=f"GOZ-{s['number']:04d}" or s['prompt']!=f"docs/prompts/spec-{s['number']:04d}.md":fail(f"{s['id']} id/number/prompt mismatch")
if registry['project'].get('interviewDecisionCount')!=95 or registry['project'].get('reviewFindingCount')!=14:fail('approved decision count mismatch')
for n in range(1,15):
 txt=(ROOT/'docs/decisions/consistency-review.md').read_text()
 if f'CR-{n:02d}' not in txt:fail(f'missing CR-{n:02d}')
if not (ROOT/'docs/roadmap/spec-register.md').exists() or (ROOT/'docs/roadmap/spec-register.md').read_text()!=render():fail('generated human registry out of sync')
# Validate any existing per-spec progress file, but do not require native Spec Kit outputs before implementation.
for path in (ROOT/'docs/specs').glob('*/progress.yaml') if (ROOT/'docs/specs').exists() else []:
 try:jsonschema.validate(yaml.safe_load(path.read_text()),json.loads((ROOT/'.gozleme/schemas/progress.schema.json').read_text()))
 except Exception as e:fail(f'{path}: {e}')
# verify internal markdown links (relative local links only); skip mermaid/code block and README examples URLs
for p in ROOT.rglob('*.md'):
 if '/node_modules/' in str(p):continue
 body=re.sub(r'```.*?```','',p.read_text(),flags=re.S)
 for link in re.findall(r'\]\(([^)]+)\)',body):
  if link.startswith(('https://','http://','#','mailto:','data:')):continue
  target=link.split('#',1)[0]
  if not target:continue
  if not (p.parent/target).exists():fail(f'broken markdown link in {p.relative_to(ROOT)}: {link}')
if errors:
 print('FAIL — '+str(len(errors))+' issues')
 for e in errors:print(' - '+e)
 sys.exit(1)
print(f'PASS: {len(specs)} unique prompts and IDs; dependency DAG; 14 CR findings; schemas; manifest paths; generated human register; merge/demo gates; documentation references')
