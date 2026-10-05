# Build notes (working environment, verified)

Scratch notes for reproducing a build of this plugin on this machine. Verified 2026-10-04.

## The one command that works

```bash
export PATH=/home/joseph/.dotnet8:$PATH
cd /home/joseph/Lidarr/Lidarr.Plugin.Qobuz
dotnet restore src/Lidarr.Plugin.Qobuz.sln -p:NuGetAudit=false
dotnet build   src/Lidarr.Plugin.Qobuz.sln -c Release -f net8.0 -p:NuGetAudit=false --no-restore
```

Result: `Build succeeded`, 0 errors.

Warning counts, stated precisely, because this is easy to get wrong:

- **Warnings from this plugin's own code (`src/`): 0.**
- **A truly clean build reports 7 warnings**, all inside the `ext/QobuzApiSharp`
  submodule (`SYSLIB0051` / `CS0672`, obsolete formatter-based serialization on its
  three exception types). They are upstream's, not ours, and predate this work.
- An *incremental* rebuild reports **0**, because the submodule is not recompiled.
  Do not quote that number as "0 warnings" without qualification: a reviewer doing a
  from-scratch build will see 7 and reasonably conclude you were sloppy. To reproduce
  the honest figure, delete `ext/QobuzApiSharp/QobuzApiSharp/{obj,bin}` as well as
  `_temp` and `_plugins` before building.
- Building with `--network none` adds 2 more (`Sentry API request failed` from
  `Sentry.targets`); that is a sandbox artifact, not a code issue.
Output: `_plugins/net8.0/Lidarr.Plugin.Qobuz/Lidarr.Plugin.Qobuz.dll` (ILRepack-merged).

## Four environment traps, and why each flag is needed

1. **Submodules are required and were not checked out.**
   `ext/Lidarr` and `ext/QobuzApiSharp` are git submodules. Without them
   there are no `ProjectReference` targets and nothing compiles.
   ```bash
   git submodule update --init --depth 1 --recursive
   ```
   (`ext/Lidarr` is ~85 MB, `ext/QobuzApiSharp` ~2.4 MB.)

2. **`src/global.json` pins SDK `8.0.0` (`rollForward: latestMinor`).**
   `latestMinor` will not roll forward across a major version, so an SDK-10-only
   machine fails with "A compatible .NET SDK was not found". .NET 8 was installed
   side-by-side rather than editing the pin:
   ```bash
   curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir ~/.dotnet8
   ```
   Installed: SDK 8.0.425. Put `~/.dotnet8` first on `PATH`.

3. **`-p:NuGetAudit=false` is needed because of an upstream advisory.**
   `ext/Lidarr/src/Directory.Build.props` sets `TreatWarningsAsErrors=true`, which
   promotes NuGet audit warning `NU1902` (MailKit 4.14.0, GHSA-9j88-vvj5-vhgr) to a
   build error. The vulnerable package belongs to the pinned **Lidarr submodule**,
   not to this plugin, so the audit is suppressed rather than "fixed" here.
   CI does not hit this only because its NuGet audit database differs by date.

4. **Build the `.sln`, never the `.csproj` directly.**
   `ext/Lidarr/src/Directory.Build.props:145` wires StyleCop up as
   `<AdditionalFiles Include="$(SolutionDir)stylecop.json" />`. Building the
   bare csproj leaves `$(SolutionDir)` empty, so `stylecop.json` is never loaded,
   `usingDirectivesPlacement` falls back to its `insideNamespace` default, and the
   upstream Lidarr tree explodes with ~509 bogus `SA1200` errors. Building the sln
   sets `SolutionDir` and the errors vanish. This matches CI (`dotnet build src/*.sln`).

## Pre-existing warnings (present before any of my changes)

The original baseline was 14 warnings. The 7 that remain are all upstream
submodule warnings; the plugin's own 7 are gone. Original list:

- `CS8632` x4 — `?` nullable annotations without `#nullable enable`
  (`API/QobuzAPI.cs:39,40`, `API/Downloader.cs:77,77,123`).
- `CS1998` x1 — `DownloadItem.SetQobuzData` is `async` with no `await`
  (`Queue/DownloadItem.cs:161`).
- `MSB3073` x1 — the `PostBuild` target in `Lidarr.Plugin.Qobuz.csproj:28` runs a
  Windows `COPY` to a hardcoded `C:\ProgramData\...` path. Fails harmlessly on Linux
  (`ContinueOnError=true`), exit 127.
- `CS0672` / `SYSLIB0051` — obsolete serialization members inside the
  **QobuzApiSharp submodule**. Not ours.

## The test project runs against the ILRepack-merged assembly

A constraint worth knowing before writing tests, because it costs an hour to rediscover.

`ILRepack.targets` merges `QobuzApiSharp`, `Newtonsoft.Json` and `TagLibSharp` into
`Lidarr.Plugin.Qobuz.dll` with `Internalize="true"`, and the test project's
`ProjectReference` copies that **merged** DLL into its own `bin`. So at runtime the
library's types are `internal`.

Consequence: a test whose method signature mentions a merged type — e.g.
`QobuzApiSharp.Models.Content.Album` — compiles fine but fails at runtime with
`MissingMethodException`, because the signature cannot bind against the internalised type.
`InternalsVisibleTo` does not help; the problem is the parameter type, not the member's
accessibility.

This is the seam doing its job. The library's vocabulary is supposed to stop at
`QobuzMetadataMapper`, and tests are supposed to speak `IQobuzSession` and the
plugin-owned record types instead — which the fake session makes easy. If you ever need
to unit-test the mapper directly against real library models, the only clean options are
a separate non-merged test target or dropping `Internalize`, and neither is worth
changing the shipped artifact for.

## Verifying a change

```bash
dotnet test src/Lidarr.Plugin.Qobuz.Tests/Lidarr.Plugin.Qobuz.Tests.csproj -p:NuGetAudit=false
```

148 tests, NUnit 3.14 + FluentAssertions 5.10.3, matching the versions the Lidarr tree
itself uses so the plugin does not introduce a second test toolchain.

Two traps worth knowing when adding tests:

- **Await your async assertions.** A non-awaited `act.Should().ThrowAsync<T>()` returns an
  unobserved `Task` and the assertion never runs — on FluentAssertions 5.10.3 it passes
  even when the target throws nothing at all. The synchronous `.Should().Throw<T>()` on a
  `Func<Task>` does assert, which makes the broken form easy to miss.
- **Tests run against the ILRepack-merged assembly** (see the section above), so a test
  signature may not name a merged library type.
