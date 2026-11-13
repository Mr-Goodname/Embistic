`Microsoft.NET.NuGetOfflineCache.targets` is the unchanged Microsoft .NET SDK source file from:

https://github.com/dotnet/sdk/blob/v10.0.302/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.NuGetOfflineCache.targets

The source is Copyright (c) .NET Foundation, licensed under the MIT license (https://github.com/dotnet/sdk/blob/v10.0.302/LICENSE.TXT).

The development computer's installed .NET 8 and .NET 10 SDKs both import this file but omit it from disk, causing MSB4019. `tools/build.ps1` uses it only in a project-local SDK copy under `artifacts`. Complete SDK installations use the normal SDK directly. The build script restores all temporary environment changes on exit. Running `dotnet build src/Embistic/Embistic.csproj` directly is supported on a complete .NET 10 SDK installation; on this computer, use `tools/build.ps1`.
