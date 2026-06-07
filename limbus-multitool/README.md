# Limbus Multi-tool

Qt-based installer for the Limbus ultrawide, window-resize, FPS/frame-pacing, HDR balance, and optional runtime UI inspector plugins. It can install the official BepInEx Unity IL2CPP Windows x64 build when BepInEx is not already present.

## Developer usage

From the repository root:

```powershell
dotnet build .\src\LimbusCanvasFix\LimbusCanvasFix.csproj -c Release -p:SkipDeploy=true
dotnet build .\src\LimbusWindowResizeFix\LimbusWindowResizeFix.csproj -c Release -p:SkipDeploy=true
dotnet build .\src\LimbusFramePacingFix\LimbusFramePacingFix.csproj -c Release -p:SkipDeploy=true
dotnet build .\src\LimbusHdrBalanceFix\LimbusHdrBalanceFix.csproj -c Release -p:SkipDeploy=true
dotnet build .\src\LimbusRuntimeUIInspector\LimbusRuntimeUIInspector.csproj -c Release -p:SkipDeploy=true
.\limbus-multitool\prepare_release_payload.ps1
py -3.10 -m venv .\limbus-multitool\.venv
.\limbus-multitool\.venv\Scripts\pip install -r .\limbus-multitool\requirements.txt
.\limbus-multitool\.venv\Scripts\python .\limbus-multitool\limbus_installer.py
```

To build a standalone executable:

```powershell
.\limbus-multitool\build_exe.ps1 -Version 1.2.0
```

Distribute the whole `dist\Limbus Multi-tool` folder. The executable depends on its adjacent `_internal` directory, which contains Qt, the backend script, the embedded app version, and the release payload.

## User workflow

1. Select the Limbus Company install folder.
2. Choose the plugins to install.
3. Click `Install / Reapply Selected`.
4. Click `Launch Game`.

Expected verification markers:

- `Applied CanvasScaler ultrawide fix`
- `Enabled resizing for HWND`
- `Frame pacing apply`
- `HDR output apply`
- `LimbusRuntimeUIInspector` when the optional inspector is selected

When the HDR balance plugin is installed, it applies Unity HDR output paper-white and automatic HDR tonemapping corrections without disabling HDR entirely.

The runtime inspector is a developer tool. When selected, launch the game and open `http://127.0.0.1:43129/` on the same machine to scan and edit live `RectTransform` and transform-only screen objects. It seeds roots from CanvasScaler, RectTransform relayout, and GameObject activation hooks, prunes stale roots before scans, returns 5000 active rows by default, and can include inactive entries when needed.

The app checks GitHub releases automatically on startup and can replace the packaged `dist\Limbus Multi-tool` folder from the latest `Limbus-Multi-tool-*-win-x64.zip` release asset.

The installer redistributes only this project's plugin DLLs, patch tooling, scripts, and a metadata resource carrier template. It derives game-specific IL2CPP symbols from the user's local `UnityPlayer.dll` and `GameAssembly.dll`.
