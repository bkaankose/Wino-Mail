# Wino Assistant (WAR) branch: start here

This branch carries the Wino Agent Runtime (`src/Wino.Agent.Runtime`) and its app integration. The full handoff, the
design and the eval tools live in the private API repo (`bkaankose/Wino-Mail-API`):

- `docs/assistant-handoff.md`: state, open work in priority order, constraints, how to measure
- `docs/wino-agent-runtime-plan.md`: the design and decisions
- `tools/assistant-eval/`: evals and corpora (they contain real account data: never copy them into this public repo)

Clone layout: `Wino-Mail-API` and `Wino-Mail` side by side under one folder. Until `Wino.Mail.Contracts` 3.1.0 is
published, `Wino.Core.Domain`, `Wino.Services`, `Wino.Agent.Runtime` and `Wino.Mail.WinUI` reference the contracts by
the relative path `..\..\..\Wino-Mail-API\Wino.Mail.Contracts`; switch them back to the package before a release.

The accuracy loop works on `src/Wino.Agent.Runtime` only (through the API repo's evals). macOS builds; WinUI was edited
without a Windows build. Neither app is part of that loop.
