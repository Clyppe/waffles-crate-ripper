# Native static-mesh recovery regression

Copy `StaticMeshNativeRegression.cs` into `Assets/Editor` in an isolated Unity 2021.3 test project. Install this package as a local file dependency, then run Unity with `-batchmode -nographics -quit -executeMethod StaticMeshNativeRegression.Run`.

This fixture replaces the active test scene and owns `Assets/StaticRecoveryFixture`. It uses Unity's real `StaticBatchingUtility.Combine`, saves and reopens a scene and prefab, runs recovery, and verifies 127 assertions covering world geometry, mirrored face winding, material submeshes, normal and UV channels, vertex colors, disabled renderers, unmodified ordinary and skinned meshes, movement with colliders, bounds, a newly created reusable prefab, and idempotence. It requires no game assets or SDK source.

A successful run logs `WAFFLES_STATIC_NATIVE_OK checks=127` and writes `native-static-result.json` next to the test project folder.