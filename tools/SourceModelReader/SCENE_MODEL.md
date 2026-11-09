# Unified Scene Data Model

## Pipeline

```text
GMA / ZIP / folder / MDL
        |
        v
AssetBundle (random-access sibling resources)
        |
        v
SourceModel (MDL + VVD + VTX parsing)
        |
        v
BodyGroup selection + VTX triangle reconstruction
        |
        v
ModelScene (one vertex pool + one skeleton + material primitives)
       / \
      v   v
   GLB   SMD compatibility
```

The 0.5.0 diagnostic path runs beside the export path. It analyzes the selected geometry and BodyGroup options without changing the scene or modifying the source archive. `validate-glb` then re-opens the emitted GLB as a separate reader pass.

## ModelScene rules

- `bones` contains one ordered skeleton. Bone parent links are retained; all selected geometry refers to this same joint index space.
- `vertices` contains only source VVD vertices referenced by selected triangles. A source vertex used by multiple materials is stored once in the shared pool.
- Each `SceneVertex` carries position, normal, UV0, four glTF joint slots, and four normalized weight slots. Source VVD v4 provides at most three non-zero influences; the fourth glTF slot is zero-padded.
- `primitives` contain per-material index lists into the shared vertex pool. A single glTF mesh node can therefore preserve several material/submesh boundaries without exporting many separate character files.
- `bodygroups` records the selected model option for each BodyGroup so the export is reproducible.
- `SceneBone.inverse_bind_matrix` is derived from the MDL `poseToBone` matrix and converted into the glTF coordinate basis.

## Coordinate basis

Source model coordinates are treated as Z-up. The GLB writer maps a vector `(x, y, z)` to `(x, z, -y)`, a right-handed -90-degree rotation around X. Bone translations, rotations, inverse bind matrices, positions, and normals use the same basis conversion.

Before GLB serialization, the writer cross-checks each bone's hierarchy transform against its converted inverse bind matrix. A warning is emitted if the maximum identity error exceeds `1e-3`.

## Why there are still multiple primitives

The character is one logical skinned mesh and one skin, but it still has different materials. glTF represents these as multiple primitives in one mesh. Welding all vertices into one primitive with one material would discard material boundaries and is not a goal.

## Future HD2 stage

The `ModelScene` object is the intended boundary for a future target-game adapter. A later HD2 exporter can consume this scene and apply a target skeleton mapping, vertex remapping, target material conversion, and game-specific resource writing without depending on SMD as an intermediate format.


## Bone definitions vs. vertex-weight references (0.5.0)

The MDL bone table is the model-level skeleton definition. A selected mesh only references a subset of those indices through positive VVD vertex weights. The reader reports these as separate concepts and deliberately does not label non-referenced definitions as helper, unused, or safe to remove. The `bone-matrix` command measures each BodyGroup option in isolation (all other BodyGroups set to option 0) and compares its weight-reference set with the all-default selection. This matrix describes geometry weight references only; animation and other runtime dependencies require separate analysis.
