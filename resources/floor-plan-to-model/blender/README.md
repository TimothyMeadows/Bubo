# Fixed architectural authoring bundle

`author.py` and `admission.py` form the dedicated Blender 4.5 LTS construction boundary. The compiled runtime resource catalog audits both files. The native caller also binds their exact SHA-256 values in each immutable job. The bootstrap verifies the raw job hash and both fixed resource hashes before importing admission code.

Run only through Bubo's bounded native process owner with an explicitly selected existing Blender executable, factory startup, offline mode, disabled auto-execution, isolated configuration/temp paths and finite native memory/time/log budgets. These application guards are not an OS sandbox or a filesystem quota. The script installs nothing, loads no input `.blend`, imports no external meshes and accepts no code, callbacks or module names from job data.

```text
blender --background --factory-startup --offline-mode --disable-autoexec \
  --python-exit-code 1 --python author.py -- ABSOLUTE_JOB_JSON EXPECTED_JOB_SHA256
```

## Job and output

The closed `bubo-floor-plan-blender-job/v1` object contains:

- `operation: construct`, absolute `workspaceRoot` and `outputDirectory`.
- `scene`: the exact `bubo-floor-plan-construction-scene/v1` native mesh/material/anchor/clearance DTO.
- `identities`: `planSha256`, `designFingerprint`, `runtimeSha256`.
- `bundle`: exactly `author.py` and `admission.py`, each with its lowercase SHA-256.
- `limits`: positive integer `maxTriangles`, `maxVertices`, `maxObjects`, `maxOutputBytes`, `maxJobBytes`.

The job is an existing nested `job.json` within the declared workspace. Its output must be the same attempt's absent `output` child. All parent components are checked for symbolic links/reparse points, traversal and platform aliases. Existing outputs are never reused or overwritten. Source/job identities are checked again before saving and after writing.

Success produces only `building.blend` and `construction-report.json`. The editable scene retains per-storey collections, exact semantic properties, separate visual/static-collision/dynamic-collision collections, declared material assignments and initial door-pivot anchors. Parenting preserves the supplied world-metre coordinates. Collision objects are excluded from renders. No whole-building hull, automatic remesh, geometry correction, GLB export or runtime asset promotion occurs.

The report contains actual Blender counts, world bounds, per-mesh index-manifold/positive-component-volume checks, coordinate error, material-assignment hashes, exact source/job/bundle identities and the saved Blender-source hash. Source and actual geometry hashes encode each world coordinate as little-endian float64, then triangle indices as little-endian uint32; signed zero is canonical positive zero. Material-assignment hashes encode each triangle's UTF-8 material ID with a little-endian uint32 byte-length prefix.

Geometry must remain within `scene.linearTolerance`, which comes from the approved plan. Unsupported float32 precision at a large world datum fails explicitly; the authorer never relaxes tolerance or rebases the building. Disconnected closed cell shells may touch on coplanar boundaries and retain separate index topology. Indexed closedness/positive volume does not independently prove absence of self-intersection, inter-object overlap, traversability or correct source-plan compilation. Those obligations remain with the native compiler and independent construction/export checks.

## Finite bounds and evidence limits

Hard ceilings are 256 MiB for raw jobs, 20,000 combined mesh/anchor objects, 4,096 palette materials independently of object count, 2,000,000 vertices, 4,000,000 triangles and 16 GiB for the source/report output allowance; tighter approved limits always apply. Unused approved palette entries do not consume mesh/anchor slots. Native memory/time budgets may make a large request unavailable before these ceilings. Admission checks counts before Blender mesh allocation. Actual source bytes are checked after Blender's native save; report UTF-8 bytes are checked incrementally with an 8 MiB hard ceiling and the remaining output allowance. A failed save or report leaves its attempt/partial evidence for the native caller to mark failed. There is no automatic cleanup, truncation or silent simplification.

`independentReimport`, `humanModelAccepted` and `runtimeAccepted` remain false. The report is a measured authoring receipt, not independent re-import or human visual acceptance. The pure Python suite in `tests/test-floor-plan-authoring.py` exercises admission/path/hash/report guards without importing or running Blender; simulated reparse checks and unavailable real symlink creation are labeled separately. Real Blender and independent output evidence are required in addition.

## Separate source-preserving QA views

`review.py` is a separate fixed QA tool, not a production export or an approval recorder. Invoke it through the same owned native process boundary, replacing `author.py` in the command above. Its exact job schema is `bubo-floor-plan-blender-review-job/v1`, with fields `workspaceRoot`, `source`, `sourceSha256`, `outputDirectory`, `bundle`, `limits` and `floors`. The bundle contains exactly `review.py` and `admission.py` hashes. The source must be an exact existing project-local `.blend`; the review output is a new attempt's absent `output` child.

Review limits are `maxObjects`, `maxVertices`, `maxTriangles`, `maxOutputBytes`, `maxSourceBytes`, `maxJobBytes`, `maxViews` and `imageSize`. Jobs are at most 1 MiB; `imageSize` is exactly 768. Each floor has `id`, `elevation`, `height`. Every floor is required and the complete view inventory is exactly the floor count plus four, at most 132 images: two exterior obliques, a roof plan, one oblique cutaway per floor and one vertical cutaway. Filenames use fixed indices (`view-000.png` onward), never source IDs as paths.

Rendering explicitly uses Cycles CPU, eight samples, two threads and 768×768 PNGs, with adaptive sampling and denoising disabled. There is no renderer/device fallback. The original loaded geometry, material shader slots, hierarchy and pivot transforms are inventoried before adding temporary cameras/lights or changing render-only visibility. Cutaways hide roof/ceiling and front-facing wall assemblies in memory; they are diagnostic views, not new model geometry. No source save operator is called. Source/job/bundle hashes are checked before loading, between views and after writing; a changed source fails rather than becoming a new approval identity.

After opening the exact trusted authored source, and before an explicit view-layer update or diagnostic rendering, review rejects enabled compositing, nonempty compositor graphs, any sequence editor (including an empty editor), mesh shape keys and animation data on scenes, meshes, materials, worlds or their node trees. These features are absent from the fixed constructor and could otherwise make rendered geometry or output differ from the measured static source. Compositing, sequencing, multiview, render borders, border cropping and Freestyle are explicitly disabled and checked before every render, so inherited output paths, sequence inputs, stereo views, crops and style modules cannot alter the fixed diagnostic workload. `renderSettings` records these false flags and the fixed source-feature rejection policy. This is inspection of trusted source after loading, not an OS sandbox for arbitrary foreign Blender files. Plain Python stub tests exercise these guards without claiming actual Blender execution.

Success writes only the view PNGs and bounded `inspection.json`. Its `sourceIndependentlyLoaded` and `sourceUnchanged` assertions refer to inspection of the original authored Blender source. `exportReimportVerified`, `humanModelAccepted` and `runtimeAccepted` remain false. This QA does not perform the later GLB export/re-import checks or establish human acceptance.
