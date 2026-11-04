# Portable assemblies and motion handoff

After current independent parts, assembly and complete sampled readiness, use `schematic-to-model export --manifest ... --blender ABSOLUTE_EXE --author NAME`. Export requires the same exact selected Blender 4.5 LTS executable, its complete bundled glTF module identity, an explicit committed-memory ceiling and the current approved v2 design. It installs nothing. `doctor` reports dependency inspection separately from executable readiness.

The sealed `bubo-schematic-export-package/v1` contains:

- `assembly.blend` and animation-free `assembly.glb`, with every approved physical occurrence kept separate.
- `assembly-design.json`, `assembly-metadata.json`, a hierarchical `bom.json` and every approved schematic drawing.
- Construction, approval and sampled-motion provenance.
- `diagnostic-motion/diagnostic.blend`, `diagnostic.glb` and `diagnostic-motion.json`. These demonstrate sampled articulation and are explicitly separate from production animation.

Geometry may be shared between identical rigid definitions. Physical instances, pivots, transforms, material assignments, interfaces and terminal ownership remain distinct. Wires, hoses, springs and belts retain separately owned skins and rigs. Source Z-up metres and portable glTF Y-up metres use the recorded coordinate conversion; local precision, world placement and functional clearances must remain within the approved numerical policy. Export fails when those tolerances cannot survive the actual portable representation. It does not silently widen them or remove components.

The fixed audited exporter produces the files; fresh fixed workers parse the actual GLB and independently import it. Native checks compare their measured triangle topology, local geometry, poses, materials, ports, joint ownership, actual inverse binds, skin weights and every diagnostic sample against current verified construction evidence. Split or reordered glTF vertices retain a logical source-index attribute for correspondence; actual positions, oriented triangle multiplicity and weights are still checked. The importer can parent a flexible mesh under its own armature; this measured wrapper is checked explicitly without repairing missing geometry or changing the delivered GLB hierarchy. Every complete certificate has one cumulative bounded geometry ledger, and the entire command shares its deadline, log and file limits.

The fixed producer writes skin positions and inverse binds from measured source-local geometry and actual source bone frames. This preserves small local components instead of retaining a rounded world-space bake. Skin deformation follows joint transforms; the mesh node's own transform is not part of the skin calculation. See the [glTF joint hierarchy contract](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#joint-hierarchy). The raw reader and fresh importer still check the delivered results against construction; source normalization cannot repair verification evidence.

Portable animation names are short ordinal identities such as `bubo.clip.000000`. Exact semantic IDs remain in animation extras and diagnostic metadata. This avoids Blender's bounded action/track names truncating long joint identities. A complete rigid mechanism cycle does not imply a looping belt material phase. Coverage is finite sampled motion, not continuous collision, manufacturing, circuit or physics certification.

## Model review

`review` produces fixed CPU-rendered views from all six orthographic directions, two additional exterior angles, every declared internal section, an exploded component view and every declared diagnostic sample. If a design has no declared section, a clearly named central inspection cut is a diagnostic view, not an approved design drawing. Technical certificates, images and decisions live outside the sealed payload, avoiding manifest hash cycles. `status` validates the current package and reports the complete `reviewArtifacts` mapping needed by a decision.

The approved export-byte allowance covers the new attempt's complete working inventory, including portable copies and verification artifacts. Each child receives only the remaining file/byte allowance after reserving its job, native certificate and permitted logs. Package writes debit that same allowance. Fixed workers preflight direct exporter/render writes and enforce incremental output/report bounds. These cooperative controls do not establish an operating-system disk quota; failed attempts and partial evidence are retained.

Use `record --kind independent-model --reviewer NAME --evidence PROJECT_JSON` with `bubo-schematic-model-review/v1`. The decision binds the exact model/package, both native portable certificates, complete view receipt and every review artifact. It records `independent: true`, a reviewer distinct from the model author, a bounded reason and an approved or rejected verdict. A separate `human-model` decision requires `human: true` and the exact current independent-review hash. Technical success, synthetic fixture assertions and independent review never create human model or runtime acceptance.

Design, parts, assembly, readiness, tool or bundle changes invalidate dependent exports. A failed or interrupted latest attempt is retained and cannot select an older successful attempt automatically. New complete review views require fresh decisions. Review inputs and immutable snapshots remain bound to their submitted bytes.

## MODEL_TO_ANIMATION

A current independent model decision produces a separate `bubo-mechanical-assembly-handoff/v1` descriptor. It binds the sealed package, actual rest GLB node/skin graph, design, metadata, provenance, technical and independent review, and the current design/parts/assembly/export state hashes. Any later relevant state write invalidates that descriptor; it is not refreshed silently. Human model acceptance remains separate.

For explicitly selected downstream work use the versioned `mechanical-assembly` recipe profile and `model-to-animation init --handoff PROJECT_JSON`. The profile preserves the reviewed mixed rigid/flexible hierarchy, local pivots, component inventory and per-rig bone ownership. It permits no new rig, retargeting, missing instance, accidental skin or ambiguous owner. Initialization copies all declared handoff inputs into immutable ancestry and revalidates the original sources before later operations. Existing skinned, articulated and rigid profiles retain their own restrictions.

Diagnostic clips are readiness evidence; they do not become approved finished animation, select a game engine, authorize installations, copy assets into a game or grant runtime acceptance. Those remain explicit downstream operations.
