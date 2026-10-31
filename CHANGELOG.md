# Changelog

## 1.1.0 — 2026-10-30

- Recover individual BONELAB and modded map scenes and avatars, with explicit crate type filtering.
- Resolve explicitly packed support assets, persistent scenes and level chunks alongside the selected root, without whole-game extraction.
- Restore static-batched map geometry as compact local-space mesh assets so objects and saved prefabs move correctly. Add repair for existing imports.
- Recover source-free SDK spawn policy identities conservatively, preserve pooling settings and report missing policies.
- Validate recovered scenes as well as prefabs.
- Preserve the locally installed 1.0.4 Windows long-path fixes for deeply nested exports.

## 1.0.3 — 2026-10-03

- Fix selected-crate extraction failing when required bundles share a filename, including Backlot's two `polymer.bundle` files. Preserve both bundles and their own sidecars in separate staging folders.
- Add compatible installed LitMAS Standard replacements for missing MonoSH and Bakery MonoSH MAS shaders, preserving shared material properties and reporting the lighting limitation.
- Add an Undo-enabled shader repair action for existing imports.
- Record crate requests in each job folder to make extraction errors diagnosable.
- Validate duplicate-bundle extraction and material repairs in a real Unity SDK project.

## 1.0.2 — 2026-10-03

- Resolve and stage only the selected crate's recursive bundle dependencies before running AssetRipper, instead of loading the entire BONELAB or pallet folder.
- Resolve each dependency through its own pallet location, including unregistered pallets supplied in ZIPs.
- Stop selected-crate extraction when catalogs or dependency bundles are missing, without a whole-folder fallback.
- Import only streaming resources owned or referenced by selected assets; exclude unrelated sidecars.
- Install Addressables automatically for catalog decoding and add crate-isolation regression checks.

## 1.0.1 — 2026-10-03

- Replaced the generic object field with a searchable picker populated from Marrow's Asset Warehouse and project crate assets.
- Included registered built-in game and loaded mod crates, with their pallet source folders and catalog paths.
- Refresh the list automatically as pallets load and deduplicate crates by barcode.
- Restrict picker entries to actual SDK crate types.

## 1.0.0 — 2026-10-03

- Unity Package Manager installation from a GitHub URL.
- Project spawnable crate picker and packed-mod pallet search.
- ZIP/folder loading and whole-mod extraction.
- Native asset dependency collection and fresh GUID remapping.
- Installed SDK MonoScript and shader reconnection.
- Missing-script, shader and prefab import reports.
- Verified official AssetRipper download, background jobs and cancellation.
