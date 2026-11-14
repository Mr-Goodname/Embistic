# Navigation checks

Run `powershell -ExecutionPolicy Bypass -File tools/build.ps1 run --project tests/navigation/NavigationChecks.csproj -c Release`.

The 27 WPF checks click actual recommended album cards, track context-menu actions and library rows against an isolated loopback Emby fixture. They verify that Back restores the original home scroll position, selected sidebar context, search filters, 200-item page, row selection, and multi-level library folders. Collections retain their complete source and remain pageable after returning. Returning must not issue another HTTP request or rebuild the playback queue.

The checks also cover clicking an already-selected sidebar root, the Alt+Left routed command, mouse XButton1, and entering/leaving immersive playback without adding to ordinary history. Synthetic screenshots are saved under `artifacts/navigation/`. Windows remain offscreen, no playback or endpoint capture starts, and the user's settings file is fingerprinted before and after.
