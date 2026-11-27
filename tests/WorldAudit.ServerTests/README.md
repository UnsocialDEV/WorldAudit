# Disposable Vintage Story 1.22.7 integration tests

This optional server mod invokes real game handlers with two registered `ServerPlayer` instances, real inventories, spawned entities, generated chunks, and WorldAudit's persisted SQLite history. It checks:

- Player names and UIDs on ordinary placement and breaking.
- Axe felling of grown logs and foliage, followed by rollback and undo.
- Burned ground-storage piles and detonated bombs rebuild inert. The runner opens the same save a second time to verify the marked firewood, pile orientation, bomb variant, and adjacent blocks after restart.
- Door halves, paired-door toggles, rotated server collision boxes, and rollback/undo without collateral `#unknown` events.
- Chisel conversion and a voxel packet, including exact geometry and snow-cover roundtrip through rollback/undo.
- Shared-chest inventory packets from two players; selective rollback preserves the other player's slot, and undo restores contents without duplicate ordinary events.
- Breaking a filled trunk retains its native inventory snapshot; rollback restores quantities, durability and custom item attributes, and undo returns the location to air.
- Refusal to overwrite a newer block placed by the other player.
- Game ticks continue during lookup, near, rollback preview, purge preview, and confirmed purge while the test holds a database lock. Lookup and near return correctly ordered rows from a fixture with 33,000 blocks and 33,000 container transactions. With a preview batch size of 32, the mixed job includes every matching block edit and storage transaction.
- A mixed rollback started immediately after a trunk break includes pending writes. Apply and undo preserve independent withdrawals and native item attributes.
- Lookup output and persisted rollback entries agree on block-action, storage-code, and moved-item filters.
- Deferred inventory callbacks retain their initiating player when two players use a chest.
- Two moderator connections receive General chat. Only the requester receives their inspect pages, lookup results and job details in the WorldAudit group.
- Configuration reload succeeds and leaves block capture and the container bridge active, including when optional client libraries are absent from a dedicated installation.

Build and run from the repository root with .NET 10 and Vintage Story 1.22.7 installed:

```powershell
dotnet publish WorldAudit/WorldAudit.csproj -c Release
dotnet build tests/WorldAudit.ServerTests/WorldAudit.ServerTests.csproj -c Release
./tests/WorldAudit.ServerTests/Run-ServerTests.ps1
```

For a non-default game installation, set `VINTAGESTORY_API_PATH`, pass `-p:GameDir=<installation>` to the test project build, and `-GameDir <installation>` to the runner.

The runner creates a new directory under `artifacts/live-1.22.7/`, binds to `127.0.0.1:42429`, disables advertising, UPnP and WorldAudit telemetry, runs `/watest`, then shuts the server down. It retains the disposable world, database, JSON results, and logs for inspection. It never reuses a normal player world. The test command requires an explicit `worldaudit-server-tests.enabled` file in that data directory. Do not install the test mod on a production server.

These are server behavior tests. Players use the game's in-memory transport and registered server identities; no authenticated network clients or client UI are driven. Third-party mods, client rendering, reconnects, and multiplayer timing races are outside this suite. Direct fixture setup writes intentionally have unknown provenance; assertions distinguish them from player operations and rollback records.
