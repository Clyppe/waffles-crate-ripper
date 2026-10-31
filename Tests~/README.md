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

Native Unity regressions use generated assets and a disposable project. With Unity **2021.3.16f1** installed, run these commands from the repository root:

```powershell
& './Tests~/Run-UnityRegression.ps1' -Test MapAvatar
& './Tests~/Run-UnityRegression.ps1' -Test StaticMesh
```

Use `-UnityEditor 'D:/Unity/2021.3.16f1/Editor/Unity.exe'` if the editor is elsewhere. The runner creates a new project under the system temporary directory, points its UPM manifest at this checkout with a `file:` dependency, and installs the package's declared dependencies. It starts Unity hidden in batch mode, checks the exit status and success marker, and leaves the test project and log for inspection. It does not open or change your existing Unity project. These fixtures require a working local Unity license; the first run may restore Unity packages.

`MapAvatarRecoveryRegression.cs` checks main map scenes and explicitly packed chunks against a synthetic catalog, excludes an unrelated level, and stages/reimports a generated avatar to verify its mesh, bones and Animator Avatar references. Its expected success marker is `WAFFLES_MAP_AVATAR_REGRESSION_OK`. The minimal synthetic SDK/catalog sources in `Unity/WarehouseFixtures` are copied from the passing isolated fixture project and contain no BONELAB SDK implementation or game assets. **Use these test doubles only in a disposable project without the real Marrow SDK**; their namespace names intentionally stand in for the unavailable SDK. The package's Addressables dependency remains installed.

`StaticMeshNativeRegression.cs` runs independently, without the synthetic warehouse files or the real SDK. It creates native Unity static batches and verifies geometry, materials and mesh channels after repair, including moving recovered scene objects and saving/reinstantiating an editable prefab. Its expected success marker is `WAFFLES_STATIC_NATIVE_OK`. The fixture replaces its disposable test scene, so run it in batch mode in an isolated project.

For a manual setup, create an empty Unity 2021.3 project, add this checkout as `com.clyppe.crate-ripper` via a `file:` UPM dependency, and copy the chosen fixture into `Assets/Editor`. For the map/avatar fixture, also copy the three `.cs` files from `Unity/WarehouseFixtures` into that folder and leave the real SDK absent. For the static fixture, copy only `StaticMeshNativeRegression.cs`. Launch Unity with:

```text
-batchmode -nographics -quit -projectPath <disposable-project> -executeMethod MapAvatarRecoveryRegression.Run -logFile <log-path>
```

Replace the method with `StaticMeshNativeRegression.Run` for the standalone static-mesh check. Native fixture success checks the recovery behavior described above; it does not claim a full map extraction or in-game BONELAB validation.
