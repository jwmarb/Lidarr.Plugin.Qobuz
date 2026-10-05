# Build notes (working environment, verified)

Scratch notes for reproducing a build of this plugin on this machine. Verified 2026-10-04.

## The one command that works

```bash
export PATH=/home/joseph/.dotnet8:$PATH
cd /home/joseph/Lidarr/Lidarr.Plugin.Qobuz
dotnet restore src/Lidarr.Plugin.Qobuz.sln -p:NuGetAudit=false
dotnet build   src/Lidarr.Plugin.Qobuz.sln -c Release -f net8.0 -p:NuGetAudit=false --no-restore
```

Result: `Build succeeded`, 14 warnings, 0 errors.
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

Baseline is 14 warnings. Not introduced by this work:

- `CS8632` x4 — `?` nullable annotations without `#nullable enable`
  (`API/QobuzAPI.cs:39,40`, `API/Downloader.cs:77,77,123`).
- `CS1998` x1 — `DownloadItem.SetQobuzData` is `async` with no `await`
  (`Queue/DownloadItem.cs:161`).
- `MSB3073` x1 — the `PostBuild` target in `Lidarr.Plugin.Qobuz.csproj:28` runs a
  Windows `COPY` to a hardcoded `C:\ProgramData\...` path. Fails harmlessly on Linux
  (`ContinueOnError=true`), exit 127.
- `CS0672` / `SYSLIB0051` — obsolete serialization members inside the
  **QobuzApiSharp submodule**. Not ours.

## No test infrastructure

There is no test project in this repo and no `dotnet test` target. "Verification"
for this codebase currently means: the sln compiles, and the ILRepack merge produces
a single assembly. Any behavioural claim beyond that is unverified by execution.
