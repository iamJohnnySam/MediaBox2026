# MediaBox2026

## Codebase Knowledge Graph (RAG)

A pre-built knowledge graph for this codebase lives in `graphify-out/`. Use it as the first stop when exploring code structure, finding where things are defined, or tracing relationships between components.

### How to use
- **`graphify-out/graph.json`** — full node/edge graph of the codebase. Query this to find file relationships, symbol references, and community clusters before searching raw files.
- **`graphify-out/`** — also contains the HTML viewer and audit report. Open `graphify-out/index.html` in a browser for an interactive visual.

### When to consult it
- Before grepping or globbing to locate a feature or symbol — check the graph first.
- When the user asks "where is X", "what calls Y", or "how does Z work" — the graph edges encode call/import relationships.
- When assessing blast radius of a change — use community clusters to identify related files.

To refresh the graph after significant code changes, run `/graphify`.
