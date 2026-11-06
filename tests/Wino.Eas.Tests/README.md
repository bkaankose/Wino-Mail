# EAS regression checks

From the repository root:

```powershell
dotnet run --project tests/Wino.Eas.Tests/Wino.Eas.Tests.csproj -p:Platform=x64
```

This executable uses the existing project dependencies without adding a test framework. Assertion failures exit with a nonzero status. It tests the production assembly, scripted HTTP exchanges, and real Wino services against disposable SQLite databases and MIME directories. It never contacts a real mail server or reads application data.

`ProtocolTests` checks encoding, negotiation, security policies and mapping. `SynchronizationTests` checks provider requests, synchronization, persistence, calendar identity reconciliation and local draft completion. See [EAS verification](../../docs/eas.md#verification) for coverage and the remaining real-server checks.
