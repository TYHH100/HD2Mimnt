# Format notes used by this alpha

- MDL header (`studiohdr_t`) gives bone/bodypart/texture/skin-table offsets.
- `mstudiobodyparts_t` points to `mstudiomodel_t` alternatives.
- `mstudiomodel_t.vertexindex` is a **byte offset** into the VVD vertex pool; divide by 48 to obtain the model's base vertex index.
- `mstudiomodel_t` is 148 bytes in the tested v48 layout. `mstudiomesh_t.vertexoffset` is a **vertex count** relative to its model. Its on-disk record is 116 bytes in the tested v48 layout; the per-LOD vertex counts are stored in the record after the runtime-only pointer field.
- VTX mirrors the MDL BodyPart/Model/LOD/Mesh hierarchy. VTX structures are byte-packed; notably a strip header is 27 bytes, a stripgroup header 25 bytes, a mesh header 9 bytes, and a VTX vertex 9 bytes.
- Each strip index first addresses a VTX stripgroup vertex; that record's `origMeshVertID` maps back into the MDL mesh vertex range. The final VVD vertex index is:

  `model.vertexindex / sizeof(mstudiovertex_t) + mesh.vertexoffset + origMeshVertID`

- Triangle-list and triangle-strip primitive flags are decoded separately.
- VVD v4 interleaved vertex records are 48 bytes: three weights, three bone bytes, influence count, position, normal, and UV.

These notes describe the subset implemented here, not every Source engine branch or every MDL revision. GMod's compiled assets can exceed constants in the vanilla Source SDK (the supplied model has 255 bones), so this tool reads the on-disk bone count rather than imposing `MAXSTUDIOBONES=128` as a parser limit.

## GMA container handling

- GMAD archive header: `GMAD`, one-byte format version, SteamID64, timestamp, required-content string, addon name/description/author strings, then addon version.
- File table entries contain a 1-based index, null-terminated addon-relative path, signed 64-bit byte size, and CRC32. Index `0` terminates the table.
- File payloads follow the complete table in entry order; this reader indexes offsets and seeks to only the requested MDL/VVD/VTX siblings rather than unpacking the whole addon.
- Member CRC32 is verified when a member is read. A zero CRC is treated as the format's `-nocrc` convention. Unsafe absolute/parent-traversal paths and duplicate case-insensitive paths are rejected.
- The tested GMA sample uses the common four-byte zero trailer after concatenated payloads.


## SMD text skeleton sections (0.6.2)

A text SMD has independent `nodes`, `skeleton`, and optionally `triangles` sections. `nodes` defines numeric node IDs, quoted names, and parent IDs. `skeleton` contains one or more `time N` frames, each with local position XYZ and Euler rotation XYZ for node IDs.

The audit classifies SMD roles conservatively using path/name cues and section contents. Ordinary geometry slices are eligible for bind/reference-pose comparison; physics and animation resources are not. The first frame (normally `time 0`) remains available in the report for all files, but it is only labelled/used as a reference pose for ordinary model slices. This prevents animation frame 0 from being mistaken for a rest/bind pose. Unknown SMD types are checked structurally but excluded from bind-pose comparison.

Different slices are compared by node name. Numeric IDs are local identifiers and can be remapped as long as names and parent-name relationships are consistent. The audit does not assume that a mesh slice only contains the bones it weights: its `nodes` section can carry the full skeleton. When present, `AES_T.smd` is selected as the default baseline; otherwise the first ordinary model slice is used.


### Sparse animation frames (0.6.2)

The parser records the transforms explicitly present in each `time` frame. For animation-like file roles, a non-empty frame that omits some declared nodes is an informational sparse-frame diagnostic with explicit/omitted counts and coverage ratio. Empty frames remain warnings. Incomplete model/reference geometry frames remain warnings. The parser does not fill omitted values, carry values forward, or claim the exact runtime/QC interpretation of omitted tracks.


## SMD animation export (0.7.0 alpha)

The parser stores each `time N` block as a sparse map from SMD node ID to local transform. Animation export maps nodes to MDL joints by name and validates the parent-name chain. It converts local XYZ positions by `(x, y, z) -> (x, z, -y)` and SMD XYZ Euler angles (radians) to glTF XYZW quaternions with the same basis conversion.

A single FPS value is required at export time because the SMD text skeleton section supplies frame indices, not a reliable project frame rate. The exported timestamp is `(source_frame_index - first_source_frame_index) / fps`; original frame indices and the time-origin frame are preserved in `animations[].extras`.

Missing transform records are not generated. Tracks that are constant and equal to the target model rest transform are omitted; constant offsets that differ from rest remain. Under automatic interpolation, a bone track that is present in every SMD frame uses glTF LINEAR; a track missing from one or more authored frames uses STEP. This is an explicit exporter playback policy; it is not a universal assertion about Source engine animation evaluation.
