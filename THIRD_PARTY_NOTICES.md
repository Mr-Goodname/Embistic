# Third-party components

Embistic uses separate, dynamically linked third-party binaries.

| Component | Version | License and source |
| --- | --- | --- |
| LibVLCSharp | 3.9.6 | LGPL-2.1-or-later; https://code.videolan.org/videolan/LibVLCSharp/-/tree/3.9.6 |
| VideoLAN.LibVLC.Windows | 3.0.23 | LGPL-2.1-or-later as declared by the NuGet package; https://code.videolan.org/videolan/libvlc-nuget |
| NAudio | 2.2.1 | MIT; https://github.com/naudio/NAudio/tree/v2.2.1; license text in licenses/NAudio-MIT.txt |
| VLC / libVLC and bundled modules | 3.0.23 | VideoLAN and module contributors; upstream notices and corresponding sources: https://download.videolan.org/pub/videolan/vlc/3.0.23/ and https://code.videolan.org/videolan/vlc |
| .NET / WPF runtime | 10.0 | MIT and third-party notices included with the self-contained runtime; https://github.com/dotnet/runtime and https://github.com/dotnet/wpf |
| SDK missing-target repair | .NET SDK | MIT, official source and license in tools/sdk-fix |

The libVLC libraries remain separate in the published libvlc/win-x64 directory and may be replaced with compatible builds. LGPL text is included in licenses/LGPL-2.1.txt. Native media modules can have additional upstream licensing terms; retain the package's notices when redistributing.

Album artwork is requested at runtime from the user's configured Emby server. No proprietary application package, source, icon or artwork is included in the program.

LRCLIB and publicly accessible NetEase lyric endpoints supply text at runtime. Embistic does not download music from those lyric services or bundle their lyrics in its distribution. The music download feature saves audio from the user's configured Emby server using their existing access permissions.
