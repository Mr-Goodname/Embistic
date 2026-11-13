# Native spectrum verification

Run:

~~~powershell
powershell -ExecutionPolicy Bypass -File tools/build.ps1 run --project tests/spectrum/SpectrumChecks.csproj
~~~

The 28 checks exercise real generated PCM through the production FFT analyzer, including fragmented frame boundaries, unsigned 8-bit and signed 16/24/32-bit stereo, IEEE float, overlap and calibrated peak frequencies/amplitudes. Silence must produce zeros.

The native WPF tests render all four spectrum modes, verify response to actual FFT data, stop rendering on pause, invoke the settings menu, validate live preferences and incomplete HEX editing, and ensure the settings editor does not mutate the saved preferences.

The test windows are outside the desktop area and do not activate. These tests do not start WASAPI capture, play audio, modify endpoint volume, or operate the user's running app. Real endpoint capture is tested separately through the app.

The settings editor deliberately labels its generated PCM demonstration as a synthetic test signal. Main playback uses the selected endpoint's WASAPI loopback audio; a remote DLNA renderer cannot be captured from this computer.
