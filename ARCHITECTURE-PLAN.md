# Architecture work — plan and state

Durable state for the architecture-improvement effort. Each iteration: read this,
check the status table, continue at the first unfinished step.

HTML candidate report: `/tmp/architecture-review-20261004-234349.html`
Build instructions: `BUILD-NOTES.md` (read this first — four environment traps)

## Verify after every step

```bash
export PATH=/home/joseph/.dotnet8:$PATH
cd /home/joseph/Lidarr/Lidarr.Plugin.Qobuz
dotnet build src/Lidarr.Plugin.Qobuz.sln -c Release -f net8.0 -p:NuGetAudit=false
dotnet test  src/Lidarr.Plugin.Qobuz.Tests/Lidarr.Plugin.Qobuz.Tests.csproj -p:NuGetAudit=false
```

Baseline before any change: build succeeded, 14 warnings, 0 errors, 0 tests.
Current: build succeeded, **0 warnings**, 0 errors, **52 tests passing**.

## Status

| # | Workstream | Candidates | State |
|---|---|---|---|
| 1 | Build hygiene + test project + characterization tests | A11, A10 | done |
| 2 | Quality policy + release projection (incl. 8x size bug) | A2 | done |
| 3 | Album-reference parsing | A7 | done |
| 4 | Authenticated album-download module (the seam) | A1, A5, A6, A9-auth | done |
| 5 | Definition-aware queue/scheduler | A3, A13 | done |
| 6 | Byte progress + ETA | A4 | done |
| 7 | Defensive host integration | A9, A12, nullability | done |
| 8 | Path templating, no new config surface | A8 (reduced) | done · needs tests for queue/progress/templating |

## Oracle's corrections to my original framing — honour these

Consulted 2026-10-04 (task sub-eca7). It changed the plan in five ways:

1. **A1+A5+A6+A9-auth are ONE module, not four refactors.** One seam:
   authenticate → load album plan → download track → tag/lyrics/art. Collapsing
   them separately would just move code.
2. **Do NOT auto-evict completed queue items.** My A3 draft had a TTL on
   terminal items. Lidarr must still observe a Completed item to import its
   files; evicting early makes downloads vanish before import. Retain
   Completed/Failed until the host calls `RemoveItem`.
3. **Do NOT fake async over the sealed blocking client.** Metadata calls stay
   honestly synchronous at the adapter interface; only media transfer (real
   HTTP/file I/O, real cancellation) is async. One deliberate background
   boundary at the scheduler, not `Task.Run` sprinkled per method.
4. **Defer configurable path templates (A8).** Replacing 13 positional params
   with one immutable context is worth it; making layouts user-configurable
   changes import roots and collision behaviour and is a product decision, not
   this refactor. Keep the current templates as defaults.
5. **Keying state by `Definition.Id` cannot fully repair the host's singleton
   mutation.** `ProviderFactory.GetInstance` assigns `instance.Definition`
   (`ThingiProvider/ProviderFactory.cs:156-161`); another thread can replace it
   before our method body runs. So: snapshot `Definition`/`Settings` on the
   FIRST line of every host entry point, pass the snapshot down explicitly, and
   never read `Definition`/`Settings` again deeper in. A dictionary prevents
   cross-contamination after entry; it cannot recover an identity the host
   already lost.

It also corrected two of my factual claims:
- A queued download does **not** survive a restart (`_items` and the channel are
  process-local). The real failure is a *new or retried grab* reaching the
  download path before any indexer search has initialised the global.
- `Task.WhenAll` on the leaked `_runningTasks` would not hang forever; the
  wrapper tasks do complete. The actual harm is unbounded retention and wrong
  active-task bookkeeping.

## New bug the oracle found that I had missed

**The indexer reports every release 8x too large.** `QobuzParser.cs:101-111`
computes `duration[seconds] * bitsPerSecond` and assigns it to
`ReleaseInfo.Size`, which Lidarr reads as **bytes**. The constants are genuinely
bits/s (`1411200` = 16bit x 44100 x 2ch). Missing `/ 8`. This feeds
`SizeSpecification`, which compares against `Gigabytes()`
(`CustomFormats/Specifications/SizeSpecification.cs:25-36`), so every size-based
custom format and every quality-size limit has been evaluated against an
8x-inflated number.

## Verified contracts the implementation must respect

- `QobuzApiService` is `public sealed partial`, **zero** interfaces, **zero**
  virtuals, all calls **synchronous/blocking** (`GetApiResponse` does
  `SendAsync(...).Result`). Unmockable ⇒ any seam must be plugin-owned.
- It holds `public string UserAuthToken { get; set; }`, mutated by every login,
  read on the request path, with **no lock anywhere in the library**. One
  session must own one client and not re-mutate its token.
- Library models carry **no nullable annotations**; `Track.CompleteTitle` and
  `Album.CompleteTitle` are computed getters that dereference `Title.Trim()` ⇒
  NRE on null title. Normalise to plugin-owned records **at the adapter**.
- Lidarr DI (`Common/Composition/Extensions.cs:25-42`): interfaces registered
  `Reuse.Singleton`, concrete types `Reuse.Transient`. Our providers and
  `QobuzProxy` are singletons.
- `DownloadClientItem.TotalSize`/`RemainingSize` are **bytes** in all ~15
  built-in clients. Only two host consumers: `Queue/QueueService.cs:78-80`
  (display + `RemainingTime` is the sort key) and
  `TrackedDownloadService.cs:205` → `SizeSpecification`. Import is gated on
  `Status == Completed` + files on disk, never on size.
- A null `GetRequestGenerator()` does not crash the host: NRE is swallowed by
  catch-alls at `HttpIndexerBase.cs:248` (empty results + recorded failure) and
  `:423` (generic "Unable to connect to indexer").
- `Download(RemoteAlbum, IIndexer)` hands us the **source indexer**
  (`IDownloadClient.cs:12`) — that is where indexer-owned credentials come from.
  The plugin currently ignores that argument.
- ILRepack merges with `Internalize="true"` (`ILRepack.targets`), so merged
  dependency types become internal in the shipped assembly. Another reason the
  seam must speak plugin-owned types, not `QobuzApiSharp.Models.*`.
- Lidarr's own test stack: NUnit 3.14.0, FluentAssertions 5.10.3, Moq 4.18.4,
  Microsoft.NET.Test.Sdk 17.10.0, NUnit3TestAdapter 5.1.0.

## Deliberately not done

- **A8 configurability** — deferred per oracle (import-root risk). The 13-param
  interface is collapsed; no new user-facing setting is added.
- **Multiple simultaneous Qobuz download-client definitions** — cannot be made
  fully correct from inside the plugin (host singleton mutation, point 5).
  Entry-point snapshotting makes the single-definition case correct and keyed
  state prevents cross-contamination after entry.
