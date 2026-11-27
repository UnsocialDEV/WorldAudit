# WorldAudit

WorldAudit is a server-side moderation mod for **Vintage Story**. Use it to find who broke a block or emptied a chest, review the exact changes, and roll back the part of an incident you want to repair. Players can join with an unmodified client.

**[Download WorldAudit 1.1.1 for Vintage Story 1.22.7 (mod ZIP)](https://github.com/UnsocialDEV/WorldAudit/releases/download/Release-1.1.1/WorldAudit-vintagestory.zip)**

This guide covers the stable 1.1.1 release. Run `/wa status` to check the installed version. [Release notes](https://github.com/UnsocialDEV/WorldAudit/releases/tag/Release-1.1.1) · [All releases](https://github.com/UnsocialDEV/WorldAudit/releases)

[Install and configure](#install-and-configure) · [Inspect a chest](#inspect-a-chest-or-block) · [Search an area](#search-an-area) · [Review and apply a rollback](#review-and-apply-a-rollback) · [Grant staff access](#grant-staff-access) · [Maintain the audit history](#maintain-the-audit-history)

## Install and configure

1. Stop the server cleanly. For an upgrade, back up the world save, the WorldAudit database directory and `ModConfig/worldaudit.json` together.
2. Download [WorldAudit-vintagestory.zip](https://github.com/UnsocialDEV/WorldAudit/releases/download/Release-1.1.1/WorldAudit-vintagestory.zip) and place it in the server data folder’s `Mods/` directory, replacing the older copy. Leave the ZIP intact. GitHub’s automatic *Source code* archives require building first.
3. Choose your telemetry settings **before the first start**. By default, WorldAudit sends startup and error reports to a preconfigured Discord webhook and includes online player names. To keep reporting local, create `ModConfig/worldaudit.json` with the following contents, or set the same property in your existing config.

   ```json
   {
     "EnableDiscordTelemetry": false
   }
   ```

4. Start the server and run `/wa status`. Check the version, database and writer health. Give a staff account inspect and lookup access, then place or break a test block and inspect that location. Capture begins when the mod is running.

<details>
<summary>Build the ZIP for this revision</summary>

From a checkout of this repository, install the .NET 10 SDK and have Vintage Story 1.22.7 available. Set the environment variable `VINTAGESTORY_API_PATH` to the full path of the installed `VintagestoryAPI.dll`. Run this from the repository root.

```sh
dotnet publish WorldAudit/WorldAudit.csproj -c Release
```

Install `WorldAudit/bin/Release/net10.0/WorldAudit-vintagestory.zip`. The default package includes native SQLite for Windows x64, Linux x64/ARM64 and macOS x64.

</details>

## Inspect a chest or block

1. Run `/wa i` and interact with the chest or block at the reported location.
2. Open the **WorldAudit** chat tab. Inspect results go to the requesting moderator. The first page contains the latest three entries, with the newest at the bottom. Interact with the same location again to cycle through older pages.
3. Read the chest visit’s **Removed** and **Added** lines separately. One visit can include several stacks and transfers in both directions. For the individual transactions, use the details command printed beside the entry. For example, `/wa i details 42 2` opens the second page for entry 42 and shows timestamps and slot changes.
4. Run `/wa i` again when you finish inspecting.

A **visit** groups transactions attributed to the player during a recorded opening of that storage container. **Grouped activity** is an estimate for older history. Consecutive transactions by the same player at the same storage can be grouped when each gap is at most five seconds. Another actor’s activity or a block change at that position breaks the group. Each recorded transaction remains available for details and rollback. An actor shown as `#unknown` means the server could not reliably attribute the change.

## Search an area

Stand near the incident. Searches and rollback previews use your position when you issue the command. The radius is a sphere measured in blocks, and chat coordinates are relative to spawn. Start with a time window and a small radius, then widen one if needed.

| What you want to find | Example |
| --- | --- |
| Recent activity near your feet | `/wa near t:30m` |
| Blocks broken by a player | `/wa lookup u:River t:2h r:20 a:break` |
| Transfers involving chests, across all players | `/wa lookup -c i:game:chest t:2h r:12` |
| Transfers of a particular item, across storage types | `/wa lookup -c i:game:rock-granite t:2h r:12` |
| Fire damage around a building | `/wa lookup -b u:#fire t:1h r:30` |

`/wa l` is short for `/wa lookup`. Lookup returns block edits and individual storage transactions. Use inspect at a specific container when you want visits. Lookup defaults to a five-block radius. `/wa near` uses the server’s configurable nearby radius, also five by default.

| Filter | Meaning |
| --- | --- |
| `t:2h`, `t:1h30m`, `t:2h-30m` | Last two hours, last ninety minutes, or between two hours ago and thirty minutes ago. Units are `s`, `m`, `h`, `d` and `w`. Omitting time includes all retained history in scope. |
| `r:12` | Search within 12 blocks of your position in the current dimension. |
| `u:River`, `u:#fire` | Match a player or recorded cause. Causes include `#player`, `#fire`, `#explosion`, `#gravity`, `#liquid`, `#decay`, `#worldgen`, `#system`, `#rollback`, `#restore` and `#unknown`. |
| `-b`, `-c` | Select blocks or storage. Omitting both searches both. |
| `a:break` | Select a block action. Accepted actions are `place`, `break`, `replace` and `restore`. Action filters select block history even in a mixed search. |
| `i:code,code`, `e:code,code` | Include or exclude matching codes. Blocks match the old or new block. Storage matches a moved item, the recorded container label or inventory type. For example, `game:chest` also matches `game:chest-east`. |
| `p:10` | Return up to ten lookup rows per selected history type (1–100, default 8). This controls lookup output only. A rollback using the same filters can select more records. |

## Review and apply a rollback

Suppose River reported an emptied chest and your search identified Ash as the player who removed the items. Stand beside the chest and use the **same filters** for lookup and preview.

```text
/wa lookup -c u:Ash i:game:chest t:30m r:2
/wa rollback -c u:Ash i:game:chest t:30m r:2
```

The second command creates a preview. **The world changes only when you apply the job.** Check the center, time window, block count, storage visits and individual transaction count. A single visit can account for many transactions. The filters select the transactions first, so a job’s visit details can contain fewer transactions than the full visit in inspect.

Use the job ID and storage entry ID printed in chat. In this example the job is `17` and the storage entry is `42`.

| Command | What to check |
| --- | --- |
| `/wa job 17` | Selection, progress, conflicts and failures. Use `/wa jobs` to find a recent job ID. |
| `/wa job 17 blocks 1` | First page of selected block edits. Storage activity uses separate pages. |
| `/wa job 17 storage 1` | First page of grouped visits and individual storage changes. |
| `/wa job 17 storage details 42 1` | Individual selected transactions for this storage entry, including time and slot. |

Page 1 contains the newest matches, with the newest at the bottom. Increase the last number to see older pages. **Applying a job processes its entire frozen selection, across all pages.** To narrow it, cancel the preview and create another with tighter filters.

| What you want to do | Command |
| --- | --- |
| Reverse matching recorded changes | `/wa rollback <filters>` (alias `/wa rb`), review, then `/wa apply <jobId>`. |
| Replay matching changes forward | `/wa restore <filters>` (alias `/wa rs`), review, then apply its job ID. |
| Reverse what a completed job actually changed | `/wa undo <completedJobId>`, review the **new** job, then apply the new ID. |
| Discard an unwanted preview or the remaining work of a paused job | `/wa cancel <jobId>`. Changes already applied stay in the world. |
| Resume an interrupted job | Review `/wa job <jobId>`, then `/wa apply <jobId>` to resume its remaining work. |

Rollback normally skips edits made by earlier rollback and restore jobs. Use `u:#rollback` or `u:#restore` to explicitly select that history. Before applying a change, WorldAudit checks the expected block state or affected slots. A newer incompatible edit produces a conflict. Inspect the location and read the job’s reported reason before deciding how to repair it.

A mixed rollback of a recently recorded filled-container break uses the block snapshot to restore the container and its contents once. Independent withdrawals remain eligible. For an older break recorded before operation linking, use `a:break` or `-b` to select the block repair. Restored explosive blocks and burned ground-storage piles are rebuilt in an inert state when supported.

## Grant staff access

The root command is `/worldaudit`, with `/wa` as its alias. Staff need the game’s `chat` privilege. Grant the following additional privileges through Vintage Story’s player or role permissions. For read-only moderators, start with inspect and lookup. Add status if they need to diagnose gaps in capture.

| Privilege | Commands it unlocks |
| --- | --- |
| `worldaudit.inspect` | `/wa i` and `/wa i details` |
| `worldaudit.lookup` | `/wa lookup` and `/wa near`, including both blocks and storage |
| `worldaudit.rollback` | `/wa rollback`, `/wa apply`, `/wa cancel`, `/wa jobs` and `/wa job` |
| `worldaudit.restore` | `/wa restore` and `/wa undo`. Staff also need `worldaudit.rollback` to review and apply these jobs. |
| `worldaudit.status` | `/wa status` |
| `worldaudit.reload` | `/wa reload` |
| `worldaudit.consumer` | `/wa consumer pause` and `/wa consumer resume` |
| `worldaudit.purge` | `/wa purge`, including permanent deletion |

Grant the command privileges individually. The registered names `worldaudit.lookup.block`, `worldaudit.lookup.container` and `worldaudit.admin` do not independently restrict lookup results or grant the commands above. Inspect, lookup and job commands require an in-game player. Use `/wa help` for the command list.

## Maintain the audit history

WorldAudit stores audit history in SQLite. By default the database is `WorldAudit/worldaudit.db` under the server data folder. Keep the database directory with the corresponding save when you back up or move a server. With the server stopped cleanly, copy the full directory, including any SQLite sidecar files.

| Situation | Admin action |
| --- | --- |
| A rollback preview asks you to retry | It waits up to ten seconds for earlier queued writes. Run `/wa status`. If the writer is paused, resume it, let the backlog drain, then retry with the same filters. |
| Status reports lost records or a writer fault | Check the server log, disk space and database access. The lost-record count covers this server run. Affected history can be incomplete, and draining the queue cannot reconstruct missing records. |
| Storage traffic is high | Use the independent block and storage job pages. The writer shares a limit of 8,192 pending records and 64 MiB of estimated payload across record types. At capacity it can reject new records. Pausing the consumer increases this risk. |
| You want to remove old history | Preview with `/wa purge t:30d`. Inspect the counts, back up the data, then use `/wa purge confirm t:30d` to permanently delete eligible history. Keep anything you may still need for rollback or undo. |
| Purge or maintenance is blocked | Inspect `/wa jobs`. Finish active work or cancel abandoned previews and paused jobs to release their protection. |
| An expected record is missing | Check the installed version, position, time window, capture settings, retention and writer status. Changes made before capture, purged entries and some third-party mod writes can be absent. |

### Settings admins commonly change

Edit `ModConfig/worldaudit.json` and use `/wa reload` after queued or running rollback work finishes. Invalid startup configuration can be replaced with defaults. Keep a known-good copy, especially if you disable telemetry.

| Setting | Default | Effect |
| --- | --- | --- |
| `EnableBlockAudit` / `EnableContainerAudit` | `true` / `true` | Record subsequent block edits and storage transactions. |
| `EnableNaturalCauseAudit` | `true` | Attribute supported fire, gravity, decay and liquid changes to their natural cause. Disabling attribution can leave the actor unknown. |
| `DefaultNearRadius` / `DefaultPageSize` | `5` / `8` | Nearby search radius and default lookup or job page size. Inspect uses three entries per page. |
| `RollbackPreviewLimit` | `5000` | Number of selected IDs written per database batch. All eligible matches are retained in the preview. |
| `RollbackMaxBlocksPerTick` / `RollbackTickIntervalMilliseconds` | `100` / `50` | Work per rollback tick and time between ticks. Reduce work per tick if applying jobs affects responsiveness. |
| `RetentionDays` | `0` | Zero keeps history indefinitely. A positive value enables age-based pruning. |
| `DatabasePath` | `worldaudit.db` | Absolute path or filename relative to the server’s WorldAudit data directory. |

If Discord reporting is enabled, startup and failure messages can include the server endpoint, world name and ID, host and process information, database path, runtime status, error details, and online player count and names. Set `IncludeOnlinePlayerNamesInTelemetry` to `false` to omit names. To direct reports to your own Discord destination, configure `DiscordWebhookUrl` and `DiscordChannelId`.

### Upgrading an older database

New history follows the save’s unique identifier across world renames. Legacy name-keyed history stays under its original identity. Older entries with an unverifiable layer or missing stack data can appear in history but fail safety checks during rollback. Upgrading very old schemas can cancel unfinished jobs with unverifiable ownership. Existing recorded transactions are preserved when older chest activity is grouped for display.

[Release notes](RELEASE_NOTES.md) · [Report an issue](https://github.com/UnsocialDEV/WorldAudit/issues) · [Support Discord](https://discord.gg/5365aHU7rt) · [License](LICENSE.txt)
