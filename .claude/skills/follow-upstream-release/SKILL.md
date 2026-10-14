---
name: follow-upstream-release
description: Use when the user asks to align this extension's version with a new release of Toolbelt.Blazor.WebAssembly.ExtensibleDevServer (the upstream dev server package), or asks to bump this package's own version to match — including vague prompts like "the upstream released a new version, follow up", "the dev server released a new version, catch up this extension too", or "bump this extension to match the latest DevServer version".
---

# Following a new Toolbelt.Blazor.WebAssembly.ExtensibleDevServer release

This extension does not reference the stock
`Microsoft.AspNetCore.Components.WebAssembly.DevServer` package at all — it depends on
`Toolbelt.Blazor.WebAssembly.ExtensibleDevServer`, built from the sibling repository
`NET10/DevServer`. When that repository's own version moves, this extension's version number is
conventionally realigned to match, purely so users aren't confused by mismatched version numbers
across the package family — even when nothing about this extension's own behavior changed. Say so
explicitly in the release notes (see below).

Unlike the User Secrets extension, this repository does have a `VersionInfo.props`.

## Steps

1. Confirm the new `Toolbelt.Blazor.WebAssembly.ExtensibleDevServer` version (from the
   `NET10/DevServer` repository's own version bump — see its `follow-upstream-release` skill; that
   repository should be updated, packed, and tested first).
2. Update `VersionInfo.props`: bump both `ExtensibleDevServerVersion` and `ImportMapExtensionVersion`
   to the new aligned version, even though only `ExtensibleDevServerVersion` strictly needs to move.
3. Update `SampleApp/SampleApp.csproj`'s `Microsoft.AspNetCore.Components.WebAssembly`
   `PackageReference` to match the DevServer's targeted WASM runtime version. (The
   `Toolbelt.Blazor.WebAssembly.ExtensibleDevServer` and
   `Toolbelt.Blazor.WebAssembly.ExtensibleDevServer.ImportMapExtension` references already pull from
   the `VersionInfo.props` properties, so they update automatically.)
4. Add a new entry at the top of `RELEASE-NOTES.txt` (see the `release-notes` skill for the house
   style). When nothing functional changed, phrase it like:
   ```
   v.X.Y.Z
   - Improve: Aligned the version number with the Toolbelt.Blazor.WebAssembly.ExtensibleDevServer package. This release contains no functional changes.
   ```
5. If the `ExtensibleDevServer` version this extension now asks for is **not yet published on
   nuget.org**, the containerized E2E tests can't restore it normally. Build it from
   `NET10/DevServer` (`dotnet pack -c Release`) and copy the resulting `.nupkg` from that repo's
   `_dist/` folder into this repository's `_local-packages/` folder — see `AGENTS.md` for the full
   mechanism (`LocalPackages.CopyToLocalFeed()`, wired into `AssemblySetup.cs`, with
   `ImportMapExtension.Test/Internals/SolutionContainer.cs` already excluding `_local-packages` from
   what it copies into each build container).
6. Update the NuGet packages referenced by `ImportMapExtension.Test/ImportMapExtension.Test.csproj`
   to the latest stable versions available at that moment (see the `dotnet-test-projects` skill):
   ```
   dotnet list ./ImportMapExtension.Test/ImportMapExtension.Test.csproj package --outdated
   ```
   - Bump every `PackageReference` it reports, including `NUnit`, `NUnit3TestAdapter`,
     `Microsoft.NET.Test.Sdk`, `Testcontainers`, `Microsoft.Playwright`, `ChainingAssertion-NUnit.Bin`,
     `XProcess`, and the `Toolbelt.*` packages. If the command cannot reach a package, check the
     nuget.org flat-container index JSON instead.
   - Do not touch the `ExtensibleDevServerVersion` reference here; step 2 owns it.
   - If a bump needs a test code change, or a package has no stable release for the new setup, report
     it rather than silently swapping in a replacement.
7. Raise the SDK container image the tests build in to the latest .NET 10 SDK, every time, without
   waiting for a failure. Find the newest published tag:
   ```
   curl -s https://mcr.microsoft.com/v2/dotnet/sdk/tags/list | tr ',' '\n' | tr -d '"[] ' | grep -E '^10\.0\.[0-9]+$' | sort -V | tail -1
   ```
   - Set that tag in both places that pin it: the `SdkImage` constant in
     `ImportMapExtension.Test/Internals/SolutionContainer.cs`, and the `docker pull` step in
     `.github/workflows/tests.yml`. Then `docker pull mcr.microsoft.com/dotnet/sdk:<tag>` locally.
   - The tag must never be older than the SDK that builds the package locally (`dotnet --version`).
   - Leave the minimum-SDK number in the `SdkImage` doc comment (and in `README.md`) alone; it is the
     requirement of this package, not the image tag.
8. Run the tests and confirm everything passes:
   ```
   dotnet test --project ./ImportMapExtension.Test/ImportMapExtension.Test.csproj --output detailed
   ```
   (Packing this extension's own package happens automatically in `AssemblySetup`'s
   `[OneTimeSetUp]` — no need to `dotnet pack` by hand first.)

## Gotchas

- `ImportMapExtension.Test/Internals/SolutionContainer.cs`'s `SdkImage` constant pins an exact
  `mcr.microsoft.com/dotnet/sdk:X.Y.Z` container image tag. This is an independent concern from the
  DevServer version bump, but step 7 still raises it every time. A test failing with "You must
  install or update .NET to run this application" means the tag is older than the SDK that actually
  built a package under test. Its doc comment also states the minimum SDK this package needs for
  import-map generation; update that number only if the actual requirement changed, not as part of
  an ordinary version bump.
- Files in this repository are stored as LF line endings in git; a normal checkout converts them to
  CRLF locally. Editing with a POSIX tool (`sed`, a bash heredoc) leaves LF, which is harmless but
  worth re-normalizing to CRLF before committing if you want a clean diff.
