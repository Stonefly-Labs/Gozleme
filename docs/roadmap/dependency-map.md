# Specification dependency model

`.gozleme/spec-registry.yaml` is authoritative for individual prerequisite edges; validate the DAG with `python3 scripts/validate_docs.py`. Phase order is an implementation recommendation, not a substitute for edges. Each spec is a vertical slice with its own demo. Only **human-merged** work counts as present on protected `main`.

```mermaid
flowchart LR
F[0000–0004 Foundation] --> P[0005–0011 Publishing]
P --> D[0012–0017 Discovery]
F --> G[0018–0023 Governance]
P --> G
G --> R[0024–0028 Resolution]
P --> R
G --> X[0029–0036 Federation]
R --> X
G --> S[0037–0038 Playground]
P --> S
S --> O[0039 Recovery]
X --> O
```
