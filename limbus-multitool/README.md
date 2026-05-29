# Limbus Multi-tool

Qt-based installer for the Limbus ultrawide, window-resize, and FPS/frame-pacing plugins. It can install the official BepInEx Unity IL2CPP Windows x64 build when BepInEx is not already present.

## Developer usage

From the repository root:

```powershell
dotnet build .\src\LimbusCanvasFix\LimbusCanvasFix.csproj -c Release -p:SkipDeploy=true
dotnet build .\src\LimbusWindowResizeFix\LimbusWindowResizeFix.csproj -c Release -p:SkipDeploy=true
dotnet build .\src\LimbusFramePacingFix\LimbusFramePacingFix.csproj -c Release -p:SkipDeploy=true
.\limbus-multitool\prepare_release_payload.ps1
py -3.10 -m venv .\limbus-multitool\.venv
.\limbus-multitool\.venv\Scripts\pip install -r .\limbus-multitool\requirements.txt
.\limbus-multitool\.venv\Scripts\python .\limbus-multitool\limbus_installer.py
```

To build a standalone executable:

```powershell
.\limbus-multitool\build_exe.ps1
```

Distribute the whole `dist\Limbus Multi-tool` folder. The executable depends on its adjacent `_internal` directory, which contains Qt, the backend script, and the release payload.

## User workflow

1. Select the Limbus Company install folder.
2. Choose the plugins to install.
3. Click `Install / Reapply Selected`.
4. Click `Launch Game`.

Expected verification markers:

- `Applied CanvasScaler ultrawide fix`
- `Enabled resizing for HWND`
- `Frame pacing apply`

The installer redistributes only this project's plugin DLLs, patch tooling, and scripts. It derives game-specific IL2CPP symbols from the user's local `UnityPlayer.dll` and `GameAssembly.dll`.
