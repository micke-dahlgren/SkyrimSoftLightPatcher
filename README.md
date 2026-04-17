# Skyrim Lighting Patcher

## Description
Skyrim Lighting Patcher scans Skyrim SE mesh files and identifies patchable shapes (Eye, Body, Other) that use `Soft_Lighting`.  
It then generates a patch output mod so the updated meshes can be installed as a separate mod.

## Prerequisites
- Windows 10 or 11
- .NET 8 SDK
- PowerShell

Check your .NET version:

```powershell
dotnet --version
```

## Project layout
Common folders you will see:

- `src/SkyrimLightingPatcher.App`  
  Avalonia desktop UI application
- `src/SkyrimLightingPatcher.Core`  
  Core scanning and patching logic
- `src/SkyrimLightingPatcher.NiflyAdapter`  
  NIF adapter/integration layer
- `tests/SkyrimLightingPatcher.Tests`  
  Unit tests
- `src/**/bin/Debug` and `src/**/bin/Release`  
  Standard `dotnet build` outputs (developer build outputs)
- `publish/`  
  `dotnet publish` outputs (distributable artifacts)

## Build vs publish (important)
- `dotnet build` is for local development and testing.
- `dotnet publish` is for creating runnable/distributable app output.


## How to build debug
Use this for day-to-day development:

```powershell
$env:DOTNET_CLI_HOME="$PWD\.dotnet-cli"
$env:DOTNET_CLI_TELEMETRY_OPTOUT="1"
dotnet build .\SkyrimLightingPatcher.sln -c Debug
```

Optional: create a Debug publish artifact:

```powershell
dotnet publish .\src\SkyrimLightingPatcher.App\SkyrimLightingPatcher.App.csproj -c Debug -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\debug-win-x64
```

## How to build release
Use this when preparing a distributable executable:

```powershell
$env:DOTNET_CLI_HOME="$PWD\.dotnet-cli"
$env:DOTNET_CLI_TELEMETRY_OPTOUT="1"
dotnet build .\SkyrimLightingPatcher.sln -c Release
dotnet publish .\src\SkyrimLightingPatcher.App\SkyrimLightingPatcher.App.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\win-x64
```

## Run tests
```powershell
$env:DOTNET_CLI_HOME="$PWD\.dotnet-cli"
$env:DOTNET_CLI_TELEMETRY_OPTOUT="1"
dotnet test .\tests\SkyrimLightingPatcher.Tests\SkyrimLightingPatcher.Tests.csproj -c Debug
```
