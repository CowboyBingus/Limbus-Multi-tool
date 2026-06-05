Limbus winhttp shim
===================

The installer deploys this `winhttp.dll` over BepInEx's stock Doorstop proxy.
Limbus Company exposes obfuscated IL2CPP exports, so a fresh BepInEx install
cannot start far enough to generate interop assemblies without this shim.

Expected SHA-256:

```
doorstop.dll:
8C6CDBC38836DEE87E3368F5DE1994D7C0CCEBF29E4CE7ABA3C0981F9375412C

winhttp.dll:
482EF32CD4D933361151867C48114AB52F458394545E09B439713FE24C13EB5E
```
