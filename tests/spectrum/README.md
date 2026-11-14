# Native spectrum verification

Run:

~~~powershell
powershell -ExecutionPolicy Bypass -File tools/build.ps1 run --project tests/spectrum/SpectrumChecks.csproj
~~~

The checks exercise real generated PCM through the production FFT analyzer, including fragmented frame boundaries, unsigned 8-bit and signed 16/24/32-bit stereo, IEEE float, overlap and calibrated peak frequencies/amplitudes. Silence must produce zeros.

The native WPF tests render all four modes at the edges and around circular and rounded-square artwork, verify bounded continuous cubic curves and seams, and assert that the entire 430-pixel record remains unobscured. They check interpolation at zero response smoothing, pause/hide/unload, shared FFT mapping, and dispatch coalescing under 1,000-packet bursts. A five-placement, 192-band native workload asserts that drawing changes do not trigger layout passes, and reports composition timing. A warmed five-curve workload reports allocation and CPU time. Timing is diagnostic and depends on the machine and display; the allocation and layout assertions detect regressions independently.

The inline editor tests exercise automatic preview notifications, invalid HEX/placement suppression, independent snapshots, and flushing the final slider change on unload. There is no save button or duplicate synthetic spectrum inside the production settings panel.

The test windows are outside the desktop area and do not activate. These tests do not start WASAPI capture, play audio, modify endpoint volume, or operate the user's running app. Real endpoint capture is tested separately through the app.

Generated PCM is used only by the checks. Main playback uses the selected endpoint's WASAPI loopback audio; a remote DLNA renderer cannot be captured from this computer.
