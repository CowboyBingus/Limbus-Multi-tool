# Limbus Ultrawide and Window Resize Fixes

Source for two BepInEx IL2CPP plugins and a Qt installer for Limbus Company.

**AI Usage Disclaimer**: This tool and its associated patches were built for myself using Codex GPT 5.5 High with intent for personal use only. I've decided to share this tool and its source code with the public due to the recent major Unity 6 engine upgrade causing a lot of breaking changes, but make no guarantees regarding their efficacy.

**Game Usage Disclaimer**: While I have not been penalized or banned regarding my use of this tool for several weeks, please understand there is no guarantee that Project Moon will not crack down on this tool, and others, at any time. By using this tool, you are also agreeing to the understanding that *your account could be permanently banned*, so please use this with discretion.

**Regarding Project Moon**: This tool is in no way associated with, or endorsed by, Project Moon or any related entities. It provides no gameplay advantages and its sole purpose is to provide accessibility for players. This tool will always be free of charge, and I will not accept money or other gifts in any form for it or work related to it. If Project Moon, or one of its legally authorized representatives, feels this project should still be shut down regardless of the previous statements, then the project will be removed as soon as reasonably possible.

# Attribution

The Lethe team's source code was referenced as a starting point for patching the game. None of their team was involved in the initial production of this tool. This tool has essentially changed completely in its approach to patching the game since then. If there are any areas where the Lethe team feels their code has still been re-used, and would like it removed, please reach out and I'll do my best to replace it in a timely manner.

## Layout

- `src/LimbusCanvasFix/` - ultrawide UI canvas fix plugin.
- `src/LimbusWindowResizeFix/` - window resizing plugin.
- `tools/patch-libcpp/` - compatibility patcher for BepInEx IL2CPP tooling.
- `tools/test-stock-cpp2il/` - local diagnostic harness for Cpp2IL behavior.
- `limbus-multitool/` - PySide6/Qt installer app for end users.
- `scripts/` - local repair and symbol-map rebuild scripts.
- `data/` - IL2CPP API name list used by the resource rebuild workflow.

## Build

From the repository root:

```powershell
dotnet build .\src\LimbusCanvasFix\LimbusCanvasFix.csproj -c Release -p:SkipDeploy=true
dotnet build .\src\LimbusWindowResizeFix\LimbusWindowResizeFix.csproj -c Release -p:SkipDeploy=true
dotnet build .\tools\patch-libcpp\patch-libcpp.csproj -c Release
```

To build the end-user installer:

```powershell
.\limbus-multitool\build_exe.ps1
```

The distributable app is written to `limbus-multitool\dist\Limbus Multi-tool`. The release build output, generated payload, local BepInEx/runtime copies, and compiled artifacts are intentionally ignored by Git.

## Release

GitHub does not build or attach the executable just because the repository is pushed. Build the Windows release package locally, then attach it to a GitHub Release:

```powershell
.\scripts\build-release.ps1 -Version 1.0.0
.\scripts\publish-release.ps1 -Version 1.0.0 -Draft
```

The first command writes `artifacts\Limbus-Multi-tool-1.0.0-win-x64.zip`. The second command requires the GitHub CLI (`gh`) and creates/pushes tag `v1.0.0`, then uploads the zip as a release asset.

The release is built locally because the plugin projects compile against BepInEx and generated IL2CPP interop assemblies from an installed game. A plain GitHub-hosted runner does not have those game-derived reference assemblies.
