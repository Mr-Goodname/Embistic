# Optional physical DLNA smoke test

The default command only discovers and lists available renderers. It does not select a device or change playback:

```powershell
powershell -ExecutionPolicy Bypass -File tools/build.ps1 run --project tests/dlna-live/DlnaLiveSmoke.csproj
```

To inspect one device, pass the exact host shown in discovery output with `--device-host`. Hostnames and IP addresses are accepted; do not include a URL scheme, port or path:

```powershell
powershell -ExecutionPolicy Bypass -File tools/build.ps1 run --project tests/dlna-live/DlnaLiveSmoke.csproj -- --device-host speaker.local
```

This reads transport state, media information, volume and mute. To perform a bounded silent FLAC playback test on that selected device, also pass `--exercise`:

```powershell
powershell -ExecutionPolicy Bypass -File tools/build.ps1 run --project tests/dlna-live/DlnaLiveSmoke.csproj -- --device-host speaker.local --exercise
```

Replace `speaker.local` with a host reported by discovery. `EMBISTIC_DLNA_DEVICE_HOST` can supply the target instead; the command-line option takes precedence. Exercise mode refuses to start without an explicit target. The legacy `--run` flag is an alias for `--exercise` and has the same target requirement. No device is chosen by name, discovery order or a built-in address. Multiple renderers matching the same host are skipped.

The test skips a playing or paused speaker, saves the original media URI/metadata/volume/mute, confirms the standard mute flag before loading media, verifies playback/seek/pause/resume/stop, and restores the previous media, volume and mute in `finally`, leaving the speaker stopped. Some devices do not honor numeric volume zero, so confirmed `GetMute`/`SetMute` support is required. Unsupported or unconfirmed mute skips playback entirely.

The Emby token comes only from the existing per-user DPAPI-protected application settings. No source code or diagnostic output contains it or any authenticated stream URL. Test results go to `artifacts/dlna-live/result.json`. A DPAPI-encrypted recovery snapshot is retained only if restoration cannot be verified; it is encrypted for the current Windows user with entropy `Embistic.DlnaLiveSmoke.v1`. If the speaker cannot be confirmed stopped, the test keeps it muted instead of restoring volume.

Seek acknowledgement can be asynchronous, so the check waits for position advancement before treating the seek as completed. Physical device results depend on the selected renderer; offline protocol checks do not establish hardware compatibility.
