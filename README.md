# Waffles Crate Ripper

A Unity Editor tool for recovering BONELAB spawnable crates and complete mod ZIPs into editable project folders.

## Install

Unity **2021.3 or newer**, Windows x64. The Marrow SDK is recommended for reconnecting BONELAB components.

1. Open **Window â†’ Package Manager**.
2. Choose **+ â†’ Add package from git URL**.
3. Paste:

   ```text
   https://github.com/Clyppe/waffles-crate-ripper.git#v1.0.2
   ```

4. Open **Tools â†’ Waffles â†’ Crate Ripper**.
5. For packed mods, click **Download official AssetRipper 2.0.0** once. The release is verified against its SHA-256 checksum and stored under your project's `Library/CrateRipper/Backend` folder. You can also select an existing compatible AssetRipper executable.

The tool contains Editor code only. It adds no runtime components to your mod. Unity installs the Newtonsoft JSON and Addressables package dependencies automatically.

## Rip one crate

- The picker automatically lists crates from **Marrow's Asset Warehouse**, including built-in game crates, loaded mod pallets and project crates. Use **Find crate** to search the list, or **All available crates** to refresh it manually. The list also refreshes as the warehouse finishes loading pallets.
- For a packed mod, choose **Load mod ZIP** or **Load mod folder**, then pick a crate from the searchable list. Loading your BONELAB `Mods` folder indexes its pallets too.
- Click **Rip selected crate**.

The output is `Assets/Ripped Crates/<crate name>`. Existing output folders get a numbered suffix. The import includes the selected prefab's referenced meshes, textures, materials, animation, audio and other recoverable assets. Streaming files are included only when owned or referenced by a selected asset.

For packed crates, the tool starts at the selected crate's main asset GUID in its Addressables catalog and follows that asset's bundle dependencies. Only those bundle files are staged for AssetRipper, including required files from other pallets. Missing catalogs or dependency bundles stop extraction with an error; selected-crate extraction never falls back to processing the whole game or pallet folder. Warehouse crates use their registered catalogs and pallet locations automatically. ZIP/folder sources use their packaged catalogs, while project crates use their authored prefab directly.

If the exported prefab cannot be matched uniquely, the tool shows the recovered prefab list so you can select the correct one before importing. Clear **Spawnables only** to see other crate types; individual extraction requires a prefab, while full-mod extraction includes scenes.

## Rip a whole ZIP or mod

Load the ZIP/folder and click **Rip entire mod / ZIP**. All recoverable assets are imported into a folder named after the pallet, or the ZIP/folder when it contains multiple pallets. Platform-specific duplicate pallets remain separate in the picker; choose a Windows mod build when editing a PC project.

Large file work runs in the background. **Cancel operation** stops extraction. Job files and backend logs stay under `Library/CrateRipper/Jobs` for diagnosis; they are not included in your packed mod.

## Prefab and script repair

Every recovered prefab is scanned. Identifiable MonoScript references are matched by **full type name** to components already installed in Unity, including Marrow and extended SDK components. Both the script GUID and local file ID are restored, preserving the component's serialized settings. Matching uses actual Unity MonoScript assets, including scripts imported from DLLs.

The importer gives copied assets fresh GUIDs and updates their references together. Native SDK script references continue pointing at the installed SDK. Shaders with an exact installed shader name can also be reconnected; their exported source is retained for inspection.

Generated script files, assemblies and compilation configuration are preserved as text in `_RecoveredSource`, so importing a mod does not compile duplicate game assemblies. Unsupported binary serialized Unity assets are preserved in `_QuarantinedAssets` and described in the report.

Each output includes:

- `recovery-report.json`: repaired references, imported prefabs, unresolved scripts and asset warnings.
- `unity-prefab-check.txt`: the result of loading every imported prefab and counting missing-script components in Unity.
- `RECOVERY-README.txt`: import notes and recovery limits.

## Recovery limits

Asset bundles usually contain script type identifiers and serialized fields, **not executable script implementations**. The importer can reconnect matching installed code; it cannot reconstruct absent arbitrary gameplay code. Install the original SDK/package for types listed as missing, then rerun the import. It preserves missing components for diagnosis rather than deleting them.

Compiled shaders also lack the original authoring source. AssetRipper's free shader export often produces placeholders. Reconnecting an installed shader with the same name can restore its implementation, but custom shaders may need their original source or manual work. The report calls this out. No extractor can promise that every compiled script or shader becomes the original editable source.

Missing bundles or assets excluded when the mod was built cannot be recovered. Geometry, references and prefab loading are checked; gameplay, shader appearance and multiplayer behavior require testing in BONELAB.

Use the tool with assets you own or are allowed to recover. Extracted assets stay on your computer; this repository contains only the importer and synthetic tests.

## Implementation and validation

- Official [AssetRipper](https://github.com/AssetRipper/AssetRipper) 2.0.0 is launched as an owned, temporary loopback server. Imports and exports use its local backend routes.
- Selected-crate operations stage only the root asset's recursive bundle dependency set; whole-mod operations require the separate **Rip entire mod / ZIP** action.
- AssetRipper exports into a fresh empty staging folder, and never receives the project's `Assets` folder as an export target.
- ZIP extraction rejects traversal, duplicate paths, links, oversized archives and file/folder aliases.
- Cancellation closes the backend process started by this operation.
- Exported code is quarantined; project assets are written only after dependency and reference processing completes.
- The synthetic core suite can be run using `dotnet run --project "Tests~/CrateRipper.Tests.csproj"`. Local validation also compiles the package with Unity 2021.3 and exercises real bundle export.

Unity's [Git package documentation](https://docs.unity3d.com/2021.3/Documentation/Manual/upm-ui-giturl.html), AssetRipper's [script recovery notes](https://assetripper.github.io/AssetRipper/articles/CommonIssues.html) and [shader recovery notes](https://assetripper.github.io/AssetRipper/articles/PremiumFeatures.html) explain the underlying constraints.

## License

This Editor package is MIT licensed. AssetRipper is a separate dependency with [its own license](https://github.com/AssetRipper/AssetRipper/blob/master/LICENSE.md). It is downloaded from its official release and is not bundled in this repository. Recovered assets retain their original owners and licenses.
