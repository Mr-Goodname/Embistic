# Slider input checks

Run from the repository root:

```powershell
./tools/build.ps1 run --project tests/sliders/SliderChecks.csproj -c Release
```

These checks load the production application resources and exercise rail and Thumb
dragging through real Windows mouse input. They cover 100%, 150% and 200% logical
scaling, both slider orientations and reversed directions, capture release outside
the control, external capture loss, two-way binding preservation, both tick snapping
forms, disabled controls, and native arrow/Home keyboard input. Logical scaling
exercises coordinate conversion without changing the user's Windows DPI settings.

A faint test window briefly appears in the screen corner. The fixture restores the
mouse pointer and foreground window afterwards. Avoid moving the mouse during the
check. It never starts the real application, audio playback, or a server connection.
