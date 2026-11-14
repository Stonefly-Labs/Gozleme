# Documentation package validation report

**Result:** PASS — generated package structural checks.

Checks executed by `python3 scripts/validate_docs.py` and `python3 scripts/generate_register.py --check`:

- 40 unique GOZ-0000 through GOZ-0039 IDs and starting prompt paths exist
- No missing/duplicate dependencies or dependency cycles
- Machine metadata passes JSON Schema validation
- Manifest document and agent pointers resolve to existing files
- All 14 approved CR review findings appear in review document
- Human Markdown specification register equals generated YAML view
- Human-only PR merge, agent branch restriction, demo and post-convergence impact gates represented in manifest
- Relative Markdown document links resolve
- ZIP archive CRC/integrity checked

**Important limitations:** These checks validate the documentation package's structure, not the future executable application, GitHub-hosted branch protection, actual Claude runtime behavior or the semantics of every security protocol. Exact source wording for 62 earlier interview decisions was not fully recoverable; the decision register flags those positions pending archival reconciliation rather than inventing quotations. Future Spec Kit-generated paths must follow the actual installed Spec Kit workflow.
