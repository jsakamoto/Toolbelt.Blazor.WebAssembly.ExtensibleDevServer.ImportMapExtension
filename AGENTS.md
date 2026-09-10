# AGENTS.md

Guidance for AI coding agents (and anyone else) working in this repository.

## Testing against an unreleased ExtensibleDevServer build

The sample app that `ImportMapExtension.Test` builds, publishes, and runs references
`Toolbelt.Blazor.WebAssembly.ExtensibleDevServer` (`SampleApp/SampleApp.csproj`, via the
`ExtensibleDevServerVersion` property in `VersionInfo.props`). That package is built and released
from its own repository (`NET10/DevServer` in this checkout). When the version this repository asks
for has not been published to nuget.org yet, there is nothing for `dotnet restore` to pull down
inside the test containers, and the affected tests fail to restore before they even get to building
anything.

To test against such an unreleased build:

1. Build the `ExtensibleDevServer` package from its own repository, e.g. from `NET10/DevServer`:
   ```
   dotnet pack -c Release
   ```
   This writes a `.nupkg` into that repository's own `_dist` folder.
2. Copy that `.nupkg` file into this repository's `_local-packages` folder (see
   `_local-packages/README.md`).
3. Run the tests as usual (`dotnet test`, etc.).

### How it works

- `ImportMapExtension.Test/Internals/LocalPackages.cs` defines `LocalPackages.CopyToLocalFeed()`.
- `AssemblySetup.cs`'s `[OneTimeSetUp]` calls it right after the `_dist` folder (this repository's
  local NuGet feed, registered in `nuget.config`) has been cleared, and before this repository's own
  package is packed.
- It copies every `.nupkg` found in `_local-packages` into `_dist`, so a package dropped there by
  hand restores exactly the way a package this repository builds does.
- `ImportMapExtension.Test/Internals/SolutionContainer.cs` copies the working tree into each
  disposable build/publish/dev-server container it starts. `LocalPackages.FolderName` is in its
  `NotWorthCopying` list, so that copy skips `_local-packages` — by the time a container starts, the
  package already dropped there has been merged into `_dist`, which is copied along as normal.
- `.nupkg` files are gitignored repository-wide, so nothing dropped into `_local-packages` for a
  test run ends up committed. Only `_local-packages/README.md` is tracked.

Once the version this repository asks for is actually published on nuget.org, `_local-packages` can
be emptied again; the tests fall back to restoring from nuget.org when it holds nothing.
