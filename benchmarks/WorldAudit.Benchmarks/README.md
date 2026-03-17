# WorldAudit Benchmarks

Manual run:

```powershell
dotnet run --project C:\Users\daytonwatson\source\repos\WorldAudit\benchmarks\WorldAudit.Benchmarks\WorldAudit.Benchmarks.csproj -c Release
```

Included scenarios:

- exact block history lookup
- radius-based block lookup
- rollback block selection
- radius-based container lookup
- writer flush throughput
- block-mutation classification
