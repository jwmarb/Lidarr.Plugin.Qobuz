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
`ext/QobuzApiSharp` submodule on a clean build — see BUILD-NOTES.md), **163 tests passing**.

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

## Quality pass: reviewer verdict (sub-cf0e, 2026-10-04)

**"Ship after Critical #1."** It verified the build, re-ran the suite, and proved two of
its findings by execution rather than by reading. Every finding was real; all are fixed.

**Critical — a partially-failed album was stranded forever.** I reported partial success
as `DownloadItemStatus.Warning`, with a comment asserting the downloaded files were
"still importable". They were not. `CompletedDownloadService.Check` returns unless the
status is `Completed` (`CompletedDownloadService.cs:59`) and `FailedDownloadService` acts
only on `Failed` (`:79`), and upstream pins this deliberately — its own test lists
`Warning` under `should_not_process_if_download_status_isnt_completed`
(`ProcessFixture.cs:105`). So an 11-of-12-track album was never imported, never failed,
never blocklisted and never retried. The terminal decision is now binary: a shortfall
reports `Failed` with the counts in the message, so Lidarr can blocklist and re-search.
I confirmed all three host facts in the submodule before changing anything.

**Warning — delete-before-cancel.** `RemoveItem` deleted `OutputPath` recursively *before*
cancelling, so a still-running download re-created the directory and its `.part` file on
the next track, orphaning files under a path Lidarr believed gone. Cancel now runs first.

**Warning — the shutdown path was unreachable in production.** Lidarr never disposes its
container, so `IDisposable` on the proxy was decorative and the 10s bounded shutdown I
wrote in `DownloadTaskQueue.Dispose` never ran. `QobuzProxy` now implements
`IHandle<ApplicationShutdownRequested>`, the host's own convention (used by `Scheduler`,
`CommandExecutor`, `DatabaseTarget`).

**Warning — terminal state was split across two modules.** The executor set the terminal
status while the queue set the running one, so neither owned a job's lifecycle and the
early-return path left a job stuck at `Queued` — falsifying the guarantee in the queue's
own doc comment. `ExecuteAsync` now returns an `AlbumDownloadOutcome` and the queue
applies every transition.

**Warning — a test that asserted nothing.** `Rejects_a_null_job` used a non-awaited
`act.Should().ThrowAsync<T>()`, which returns an unobserved `Task`. I reproduced this in a
scratch project on FluentAssertions 5.10.3: the non-awaited form passes even when the
target throws nothing at all. Fixed, then **mutation-tested** — removing the guard makes
it fail, restoring it makes it pass. It was the only occurrence in nine test files.

Suggestions taken: stale `net6.0` ILRepack `LibraryPath` now follows `$(TargetFramework)`
(the `net6.0` directory does not exist); the orphaned "No test infrastructure" section in
BUILD-NOTES.md is replaced with accurate guidance including the await-your-async-assertions
trap; `HarnessSmokeTest` deleted as noise. The big one — **the host-facing boundary was
entirely untested, and the reviewer noted every remaining defect lived in that band** — is
addressed by `QobuzProxyQueueReportingTests`, 13 tests asserting what Lidarr is actually
told: bytes not track counts, no fabricated ETA, the title format, and that bad URLs,
unknown containers and non-Qobuz indexers are rejected at the door.

Suggestions deliberately **not** taken, with reasons:
- **Collapse `TrackPathTemplate`.** It is more machinery than two fixed paths need, but it
  is the right shape if user-configurable paths ever land, and it is covered by 16 tests.
  Deleting working, tested code to save ~15 lines is not worth the churn now.
- **Simplify `DownloadProgressSnapshot`'s derived properties.** The special cases
  (0.99 cap, completed-overrides-estimate, non-negative clamp) each exist because a naive
  version reports something wrong to the user, and each is pinned by a test.

Its "Unverified" items I accept as open and low-priority: `_apiLock` contention cannot be
measured without live credentials; the library's 1200-track `GetAlbum` cap is academic;
session eviction is by arbitrary key order rather than recency, which costs a re-login
rather than correctness now that eviction no longer disposes.

## Defects I found in my own new code, after the implementation was "done"

Recorded because the pattern matters more than the list: every one of these was in code I
had already declared working, with a green build and a green suite.

| # | Defect | How it surfaced |
|---|---|---|
| 1 | Byte progress double-counted (`Progress<T>` dispatches async) | my own new test failed |
| 2 | Filename sanitising was platform-dependent (`GetInvalidFileNameChars` returns only NUL and `/` on Linux) | my own new test failed |
| 3 | `QobuzAlbumDownloadPlan` allowed an empty track list while a caller indexed `Tracks[0]` | self-audit |
| 4 | `Dispose` disposed primitives the worker was still using after a timed-out wait | self-audit |
| 5 | Pointless `Volatile.Read` on a write-once field | self-audit |
| 6 | Session cache never evicted (resource leak) | self-audit |
| 7 | My own fix for 6 introduced a **use-after-dispose** across a live download | re-reading my own diff |
| 8 | `TryFromContainer` left `MP3320` in its `out` on failure | verifier flagged it as a latent trap |
| 9 | Claimed "0 warnings" from an incremental build; a clean build shows 7 | verifier corrected me |
| 10 | Dead surface: `TrackLyrics.HasAny`, `TryGetExistingSession` | self-audit, deletion test |
| 11 | `QobuzParser` duplicated the mapper's null-safe title reader | self-audit |
| 12 | `QobuzAuthenticationException` message was discarded by the host, so A9 did not actually improve the UI message | reading the host's catch ladder |
| 13 | ETA cache leaked on the `Warning` status I introduced | auditing the ETA arithmetic |

Two lessons worth carrying forward:
- A green build and a green suite are necessary, not sufficient. Six of these were found
  only by re-reading the code with fresh suspicion.
- Fixing a leak by reflex created a worse correctness bug (7). Trade bounded resource
  waste for correctness deliberately, not automatically.

## Host-integration contracts, verified by reflection on the shipped assembly

The refactor moved or rewrote every type Lidarr has to discover, so I checked discovery
itself rather than assuming it survived. A throwaway probe (in `/tmp`, since deleted)
loaded the ILRepack-merged `Lidarr.Plugin.Qobuz.dll` and enumerated exported types:

| Contract | Implementations found |
|---|---|
| `IIndexer` | 1 — `Indexers.Qobuz.Qobuz` |
| `IDownloadClient` | 1 — `Download.Clients.Qobuz.Qobuz` |
| `IDownloadProtocol` | 1 — `QobuzDownloadProtocol` |
| `IBlocklistForProtocol` | 1 — `QobuzBlocklist` |
| `Plugin` | 1 — `QobuzPlugin` |
| `IHandle<ApplicationShutdownRequested>` | 1 — `QobuzProxy` |

The shutdown hook mattered most, because the reviewer's point was that the queue's
shutdown logic was unreachable without it. I traced the host's mechanism to
`EventAggregator` → `ServiceFactory.BuildAll<IHandle<TEvent>>()` →
`_container.GetServices<T>()`, which is container-based and therefore includes plugin
assemblies, and `LifecycleService.Shutdown` publishes the event on a real shutdown. The
probe then confirmed on the shipped DLL that `QobuzProxy` is public, implements the exact
closed generic, and exposes a public `Handle(ApplicationShutdownRequested)`. So the hook is
genuinely wired, not merely plausible.

Also confirmed the protocol name cannot drift: all three places that declare it
(`Indexers/Qobuz/Qobuz.cs:34`, `Download/Clients/Qobuz/Qobuz.cs:32`,
`Blocklisting/QobuzBlocklist.cs:21`) use `nameof(QobuzDownloadProtocol)` rather than a
string literal, and Lidarr matches download clients to indexers on that string
(`DownloadClientProvider.cs:46`).

## Second reviewer (sub-62da) — findings and what I did

I launched a second, tightly-scoped reviewer when the first appeared wedged. It was worth
it: it independently found the `.part` collision I had just fixed (good corroboration) and
one thing I had measured *wrongly*.

**`HttpClient.Timeout` does not bound a body read.** It claimed this and I verified it
myself: against a server that sends headers immediately and then dribbles one byte per
500ms, a client with `Timeout = 3s` **completed normally after 9.5s**. With
`HttpCompletionOption.ResponseHeadersRead` the timeout covers the header exchange only.
That matters here because a stalled transfer holds a track permit in the downloader *and*
an album permit in the queue, so three stalls wedge the single worker with no recovery.
`CopyToFileAsync` now bounds each individual read with a linked CTS and a 60s stall
timeout, and reports the stall as a `QobuzApiException` rather than hanging.

**Where it was wrong, and I only know because I checked.** It rated truncation detection
Critical, arguing a clean mid-body close ends the read loop silently. I built that exact
case — declared `Content-Length: 4096`, sent 1000 bytes, clean FIN — and .NET 8 raises
`HttpIOException`; a reset raises `IOException`. So the transport already surfaces it. I
kept the length comparison as cheap defence in depth and labelled it honestly in the code
rather than claiming a fix for a bug that does not exist.

**Also fixed from its report:**
- `Dispose` disposed the sealed library client *without* `_apiLock`, the one place the lock
  most matters, and `_disposed` was a non-atomic check-then-set. Both now under the lock.
- `QobuzSessionProvider` had no `IHandle<ApplicationShutdownRequested>`, so its `Dispose`
  was unreachable — which also undermined the eviction policy's premise that shutdown
  disposes sessions eagerly. Added; verified by reflection that **both** handlers are
  discoverable on the shipped assembly.
- `AppId` read library state outside `_apiLock`. Now consistent.

**Three cuts it recommended, all taken after verifying each was safe:**
- `MonotonicDeltaProgress` (~35 lines + a lock) guarded against out-of-order progress
  reports that its only producer cannot emit — a single-threaded loop reporting a strictly
  increasing total. The session now reports per-chunk deltas and a 15-line `ChunkProgress`
  replaces it. This change *broke two tests*, which was the useful part: it proved the
  suite actually verifies the progress contract rather than echoing it.
- `BuildApiUrl`/`AppId`/`AuthToken` existed on `IQobuzSession` only so the indexer could
  hand-roll HTTP, and forced `FakeQobuzSession` to reimplement production string building —
  the one thing a fake must never do. `BuildApiUrl` is now a static `QobuzApiUrl.For`; the
  fake's duplicate is deleted.
- `AlbumDownloadResult.IsCompleteSuccess` was used only by tests while the proxy
  hand-rolled the same predicate. The proxy now uses it.

**Its one finding I declined:** filtering `catch (OperationCanceledException)` in
`RunJobAsync` on `linked.Token.IsCancellationRequested`. It conceded it could not construct
a live path, and I traced the same candidates to the same conclusion — per-track timeouts
surface as `TaskCanceledException` and are already counted as failed tracks. Adding a
branch for an unreachable case is speculative.

It confirmed the `SemaphoreSlim` + `Task.WhenAll` shape I was unsure about is correct:
`WhenAll` waits for every task to reach a terminal state before `using` disposes the
semaphore, and a task cancelled inside `WaitAsync` never enters the `try` so never
releases. Nothing to change.
