# Disposable Vintage Story 1.22.7 integration tests

This optional server mod invokes real game handlers with two registered `ServerPlayer` instances, real inventories, spawned entities, generated chunks, and WorldAudit's persisted SQLite history. It checks:

- Player names and UIDs on ordinary placement and breaking.
- Axe felling of grown logs and foliage, followed by rollback and undo.
- Door halves, paired-door toggles, rotated server collision boxes, and rollback/undo without collateral `#unknown` events.
- Chisel conversion and a voxel packet, including exact geometry and snow-cover roundtrip through rollback/undo.
- Shared-chest inventory packets from two players; selective rollback preserves the other player's slot, and undo restores contents without duplicate ordinary events.
- Refusal to overwrite a newer block placed by the other player.

Build and run from the repository root with .NET 10 and Vintage Story 1.22.7 installed:

```powershell
dotnet publish WorldAudit/WorldAudit.csproj -c Release
dotnet build tests/WorldAudit.ServerTests/WorldAudit.ServerTests.csproj -c Release
./tests/WorldAudit.ServerTests/Run-ServerTests.ps1
```

For a non-default game installation, set `VINTAGESTORY_API_PATH`, pass `-p:GameDir=<installation>` to the test project build, and `-GameDir <installation>` to the runner.

The runner creates a new directory under `artifacts/live-1.22.7/`, binds to `127.0.0.1:42429`, disables advertising, UPnP and WorldAudit telemetry, runs `/watest`, then shuts the server down. It retains the disposable world, database, JSON results, and logs for inspection. It never reuses a normal player world. The test command requires an explicit `worldaudit-server-tests.enabled` file in that data directory. Do not install the test mod on a production server.

These are server behavior tests. Players use the game's in-memory transport and registered server identities; no authenticated network clients or client UI are driven. Third-party mods, client rendering, reconnects, and multiplayer timing races are outside this suite. Direct fixture setup writes intentionally have unknown provenance; assertions distinguish them from player operations and rollback records.
