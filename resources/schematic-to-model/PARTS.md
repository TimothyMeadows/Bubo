# Independent schematic part construction

`build-parts --blender ABSOLUTE_EXE` constructs individual approved part definitions. It requires current whole-design approval, an existing registered Blender 4.5 LTS executable, the exact audited bundle, an explicitly enforced aggregate committed-memory budget and finite process/log/output limits. Construction currently requires the supported 64-bit Windows owned-job memory implementation. Design previews remain available independently.

Every definition is authored before an assembly exists. Repeated physical instances retain separate IDs, transforms, parents and definition mappings; they may share prototype geometry during later assembly. This stage creates no assembled scene, skin rig, portable model package, engine integration or asset promotion.

## Geometry and source

The fixed bundle supports parameter-driven boxes, cylinders, profile extrusions, full/partial revolutions, circular-section path sweeps and controlled profile lofts. Add/subtract operations use the authored feature order and preserve openings. Unsupported, reversing, degenerate, tightly folded or self-intersecting inputs fail explicitly; no proxy, decimation or remeshing fallback is selected. Frames apply the documented Rz * Ry * Rx matrix before translation, in metres with Z up.

Extrusion and revolution profiles are strictly convex. Loft profiles preserve point correspondence, parallel frames and increasing axial positions, with positive independent X/Y scaling and translation of one convex profile. Sweeps use parallel-transported rings; their conservative clearance checks can reject densely sampled paths even when a different representation might be safe. Resolution is never reduced automatically. The finished part must be one connected, consistently oriented closed boundary without self-intersection. Disconnected material islands and fully sealed inner cavity shells require a different explicitly authored design; they are rejected in this construction subset. A hollow body with an actual opening can retain a connected boundary.

Detailed hardware is expressed through its actual approved features: for example a shaft, extruded hexagonal head, slot and explicitly modeled threads. A detail description does not silently manufacture geometry omitted from the specification.

Each prototype has an editable `.blend` and an evaluated binary geometry snapshot. Owned construction operands and attachment/terminal markers remain distinct from the one physical prototype. The dedicated verifier reopens each saved `.blend` with automatic execution disabled and remeasures geometry, ownership and materials. The native reader independently checks the binary positions, triangle topology, dimensions, material values, file hashes, quantities and aggregate budgets.

Authored feature coordinates and saved interface/terminal frames must meet the approved linear and angular tolerances. Blender's numeric representation can make a very strict design unrepresentable; the worker rejects that result rather than widening the approved tolerances. Comparing two equally rounded meshes alone is insufficient dimensional evidence.

## Versioned execution records

The data-only `bubo-schematic-part-job/v1` job binds the complete approved design, per-definition identities, design/runtime/tool identities, four audited script hashes and finite limits. Its operation is `build-parts`. Only the fixed `parts_author.py`, `parts_verify.py`, `parts_geometry.py` and `parts_admission.py` bundle can execute it.

The author and independent verifier both emit `bubo-schematic-parts/v1` reports, in `parts-report.json` and `parts-verification.json`. Reports contain the exact physical-instance mapping, one entry per part definition and measured geometry/material values. `totalBytes` counts prototype `.blend` and binary payloads; the native inventory additionally charges both reports against the complete output-byte limit.

Binary geometry starts with the eight ASCII bytes `BUBOPRT1`, little-endian unsigned 32-bit vertex and triangle counts, float64 XYZ positions, then unsigned 32-bit triangle indices. Coordinates remain part-local to preserve small hardware precision. The format is an internal verification artifact rather than a portable runtime model export.

Native limits include an 8 MiB job/report ceiling, bounded vertices/triangles/files and the approved whole-assembly weighted triangle and byte budgets. Reused definitions are charged for every physical instance. A completed attempt retains design approval, tool/script/job/report and prototype ancestry. Changes invalidate dependent evidence; failed/interrupted attempts remain recorded and cannot overwrite a completed artifact.

`doctor` reports selected tool, bundle and construction prerequisites. `validate` checks the design and its design-review evidence; `status` additionally re-verifies the latest part attempt, payloads and construction ancestry. A capability declaration or prototype report does not establish actual assembly movement, engineering certification or human model acceptance.
