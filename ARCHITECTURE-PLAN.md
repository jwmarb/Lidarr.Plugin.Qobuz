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
Current: build succeeded, **0 errors**, **0 warnings from `src/`** (7 from the
`ext/QobuzApiSharp` submodule on a clean build — see BUILD-NOTES.md), **143 tests passing**.

## Independently re-verified 2026-10-04 (after the implementation commits)

Not trusting the earlier in-flight runs, this was all re-checked from scratch:

- **Clean rebuild** (`_temp`, `_plugins`, test `obj`/`bin` deleted first): 0 errors,
  0 warnings, and the ILRepack-merged assembly is produced at
  `_plugins/net8.0/Lidarr.Plugin.Qobuz/Lidarr.Plugin.Qobuz.dll`.
- **Test stability**: 120/120 passed on 5 consecutive runs. Several queue tests are
  timing-sensitive (bounded concurrency, shutdown cancellation), so repetition was
  the point; no flakiness observed.
- **Deleted types leave no references**: `QobuzAPI`, `QobuzURL`, `MetadataUtilities`,
  `DownloadItem` all return 0 hits under `src/` (excluding the unrelated
  `DownloadItemStatus` enum, which is a host type).
- **No stray imports or foreign paths**: `Npgsql` gone from source; the only
  remaining `C:\ProgramData` occurrence is prose inside a csproj comment
  explaining what was removed.
- **Secret handling proven by execution**, not by reading. A throwaway probe was
  compiled against the *shipped merged assembly* in `/tmp` (since deleted) with
  deliberately distinctive secrets. Results:
  - `QobuzCredentials.ToString()` → `QobuzCredentials(email login)`; none of the
    password hash, auth token, or app secret appears, including via the string
    interpolation NLog's `{0}` performs. This matters because
    `QobuzSession.cs:463` logs a credentials object at Debug.
  - `Equals`/`GetHashCode` agree for identical credentials and differ when the
    password differs — the invariant `QobuzSessionProvider`'s session cache keys on.
  - `IsComplete` is true for an email login and false for empty credentials.
  - `AudioQualities.For(FLACLossless).EstimateBytes(600)` = **105,840,000**, i.e.
    bytes not bits, confirming the 8x over-reporting bug is genuinely fixed in the
    artifact that actually ships.

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

## Quality pass: verifier verdict (sub-5c9c, 2026-10-04)

**PASS**, executed inside `ralph-verify/base:latest` under `--network none` against
throwaway copies of the tree. It corrected me twice and flagged one latent trap. All
three are now addressed:

1. **My "0 warnings" claim was wrong.** A clean build emits **7**, all in the
   `ext/QobuzApiSharp` submodule; warnings attributable to `src/` are 0. I had been
   quoting an *incremental* build, which skips recompiling the submodule. BUILD-NOTES.md
   now states both numbers and how to reproduce the honest one.
2. **"120 tests" was stale** by the time it finished, because I was committing while it
   ran. It verified both states: 120/120 on the older snapshot and 140/140 on the newer.
3. **`TryFromContainer` left `MP3320` in its `out` parameter on failure.** Correct at the
   single existing call site (`QobuzProxy.cs` checks the bool and throws), but a trap: a
   future caller who ignored the bool would silently downgrade a hi-res grab to lossy,
   which is the exact bug this module was built to eliminate. The signature is now
   `[NotNullWhen(true)] out AudioQualitySpec?` and sets `null` on failure, so ignoring
   the result is a compiler warning. Enabling that annotation immediately surfaced two
   unchecked dereferences in my own tests, which are also fixed.

It independently confirmed, by reflection against the **shipped merged assembly** rather
than by trusting the suite: `EstimateBytes(600)` = 105,840,000 (bytes, not bits); the
quality round-trip holds for all four values and rejects all 6 bad inputs; ILRepack
genuinely rewrites the merged DLL (977 types, library types inside it); MSB3073 is gone;
and no references to the deleted types remain. It ran the two concurrency tests **20
consecutive times** with zero flakiness, plus 8 full-suite runs.

Its stated limits, which I accept: nothing here proves the plugin loads inside a running
Lidarr host, and no cold NuGet restore or Windows deploy was exercised.

## Regression I introduced and then fixed in the same session

While auditing my own eviction fix I found I had created a **use-after-dispose**: an
evicted session was being `Dispose()`d, but `QobuzProxy.ExecuteAsync` holds a session
across an entire multi-minute album download. Editing credentials mid-download would
have closed the `HttpClient` underneath a live transfer. Eviction now drops the cache
reference without disposing; the GC reclaims the session once the download still holding
it finishes. Only `Dispose()` at process shutdown disposes eagerly. This is a good
illustration of why the bounded-leak-versus-correctness trade has to be made
deliberately rather than by reflex.

## Correction to my own A9 claim (found in loop 1)

I had written that throwing `QobuzAuthenticationException` from
`GetRequestGenerator` means "the real reason reaches the log and the UI". Half of that
was wrong, and I only found it by reading the host's catch chain properly.

`HttpIndexerBase.TestConnection` has a long `catch` ladder
(`HttpIndexerBase.cs:339-430`). Lidarr's own indexer exception types get their message
surfaced — `IndexerException` yields `"Unable to connect to indexer. " + ex.Message`.
Everything else hits the final `catch (Exception)` at `:423`, which returns a fixed
`"Unable to connect to indexer, check the log for more details"` and **discards the
message**. `QobuzAuthenticationException` derives from plain `Exception`, so the settings
test showed the same unhelpful string as before. Deriving from `IndexerException` is not
an option: it requires an `IndexerResponse`, which does not exist at credential-check
time.

Fix: `Qobuz` (indexer) now overrides `Test(List<ValidationFailure>)`. It checks the
credential snapshot first, attempts a login, and on failure adds a `ValidationFailure`
against the `Email` field carrying the real reason — which is the mechanism Lidarr
actually renders in the UI — before deferring to `base.Test` for the live query. The
throw from `GetRequestGenerator` still earns its place: it fixes the *search* path, where
the old `return null` caused a host NRE and silently empty results.

Lesson worth keeping: "I throw a descriptive exception" is not the same claim as "the
user sees a descriptive message". The host decides.
