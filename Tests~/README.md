# Package verification

These console tests create their own synthetic assets and archives. They exercise the editor package's pure C# recovery and archive code without launching Unity or copying game assets into this repository.

Run from the repository root:

```sh
dotnet run --project 'Tests~/CrateRipper.Tests.csproj' --configuration Release
```

The default build restores Newtonsoft.Json from NuGet. For an offline check, pass a compatible .NET assembly, such as the copy bundled with the .NET SDK:

```powershell
dotnet run --project 'Tests~/CrateRipper.Tests.csproj' --configuration Release '-p:NewtonsoftJsonPath=C:/Program Files/dotnet/sdk/6.0.428/Newtonsoft.Json.dll'
```

Unity ignores `Tests~` when it imports this UPM package. The checks do not run the external AssetRipper executable, decrypt assets, compile recovered scripts, or claim in-game validation. Unity editor compilation is a separate check.
