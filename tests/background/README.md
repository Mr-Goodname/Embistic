# Background playback and tray checks

```powershell
./tools/build.ps1 run --project tests/background/BackgroundChecks.csproj -c Release
```

The fixture loads the production main window, its actual tray service, native Shell
notification callbacks and the registered single-instance restore protocol. It
checks close-to-tray, restore, transport menu events, icon recreation, full tray
exit, direct exit, and fallback exit when the tray is unavailable.

The production audio/network startup handler is detached. Settings persistence is
replaced with an in-memory sink, and the user's settings file fingerprint must stay
unchanged. Download-directory checks use a unique temporary directory. Restore
messages target only fixture windows, rather than broadcasting to a running app.
