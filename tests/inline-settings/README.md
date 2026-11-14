# Inline lyric settings checks

Run `powershell -ExecutionPolicy Bypass -File tools/build.ps1 run --project tests/inline-settings/InlineSettingsChecks.csproj -c Release`.

The 28 native WPF checks cover continuous fractional slider input, preserved control identity, coalesced appearance notifications, complete color validation, and every appearance tab at a 384-pixel panel width. Source checks cover automatic updates, preserved text fields and caret, enable state, lookup order, validated import/export, and the absence of repeated save callbacks or unrelated lyric redraws.

The real MainWindow wiring is exercised with its storage callback replaced by an in-memory recorder: valid changes must reach persistence, switching or closing panels must flush the final edit synchronously, and retired editors must not overwrite the current session. Tests wait for actual callback delivery within a bounded timeout instead of assuming a fixed dispatcher schedule.

Fixtures stay outside the visible desktop and never initialize playback, tray integration, Emby connections, network lyric requests, or real audio capture. A before/after hash verifies that the user's saved settings remain unchanged. Synthetic screenshots are written to `artifacts/inline-settings/`.
