# Portable floor-plan export v1

Stage 3 consumes the exact current successful construction attempt and complete design approval. It does not revise the v1/v2 building plan, regenerate approved drawings, change the editable source, grant model acceptance, or install an engine asset.

The closed camelCase contracts are `bubo-floor-plan-export-package/v1`, `bubo-floor-plan-semantics/v1`, and the external `bubo-floor-plan-model-review/v1`. `export-package.schema.json#/$defs/modelReview` describes the external decision. Native strict JSON admission additionally rejects duplicate keys, unknown fields, missing/null required values and nonfinite numbers. JSON Schema is a shape description; it cannot establish geometric correspondence, filesystem confinement, freshness or a reviewer's independence.

## Sealed payload

The package contains exactly:

```text
package.json
building.blend
visual.glb
collision.glb
building-plan.json
semantics.json
provenance/construction-report.json
provenance/design-preview.json
provenance/design-approval.json
drawings/<every exact approved SVG name>
```

`building.blend`, the source plan, construction report, design preview/approval and every drawing are byte-identical immutable copies. `package.json.files` is the full strictly ordinal-sorted inventory of every payload file **except package.json**. Each entry records `path`, lowercase SHA-256 and positive byte length. The raw external SHA-256 of `package.json` identifies the sealed package. Do not add its own hash to its inventory or later insert verification/review results into its bytes.

Paths use printable ASCII, forward slashes and relative names, with a maximum of 512 characters and 128 characters per component. Native validation rejects traversal, absolute/device paths, alternate streams, backslashes, trailing spaces/dots, invalid Windows filename characters, case aliases and file/directory collisions. Native filesystem admission must additionally reject links/reparse points, external resources, extra files/directories and source/output overlap before and after copying/hashing. An inventory describes expected bytes; validating DTOs alone does not observe the filesystem.

`ancestry` binds the explicitly declared `modelAuthor`, immutable plan hash, design fingerprint, whole-design approval, runtime and resource catalog; exact construction attempt/job/report/editable-source identities; fixed script bundle; and portable Blender/glTF module identities. Export requires an explicit author identifier. Independent review compares its reviewer against this sealed author identity; changing that declaration changes the package hash and invalidates dependent decisions. `constructionAttempt` is a workspace-relative historical identity, never an absolute workstation path. `toolchain.gltfFiles` contains the exact relative installed module inventory including consumable bytecode when present. The native job holds local executable/module paths outside the payload. Verify native tool/script/catalog identities around each child; portable hashes do not establish that an installed tool is currently unchanged.

## Coordinates and semantics

Source coordinates are XY metres with absolute Z up. GLB is metre/Y-up, with exactly `p_gltf=(x,z,-y)` and row-major matrix `[1,0,0, 0,0,1, 0,-1,0]`. There is no scale, datum shift or second rotation. The inverse is `(x,-z,y)`. Blender's glTF importer returns Blender-space geometry; applying that inverse again would corrupt measured positions. A source +Z door axis is GLB +Y and returns to +Z on import.

`semantics.plan` is an exact deep snapshot of the admitted complete v2 plan. It preserves floor elevations, footprint/courtyard polygons, room clear-space boundaries and access declarations, wall centre lines/thicknesses/heights/adjacency, nominal opening offsets/apertures/pivot/swing, slab openings, explicit stair dimensions/landings/connectivity, roof vertices, source material/detail roles and construction dimensions. Rooms and openings may intentionally have no solid node. Do not infer their identities or nominal dimensions from object bounds. The duplicate snapshot must match the actual immutable `building-plan.json`; native validation compares every field to the admitted source plan.

`materials` preserves scalar construction material identities including transmission/IOR. `meshes` maps each source mesh ID to its exact semantic/storey/kind/representation/collision/material identities, per-triangle material assignments when supplied, source vertex/triangle counts, world bounds, nullable source pivot and explicit GLB node/parent IDs. Source counts describe native geometry before glTF seam splitting. `anchors` preserves every source floor/pivot anchor, its absolute position, +Z axis and closed-at-zero rotation.

Each separate GLB has `building:<planId>`, `storey:<storeyId>` and used representation groups: `representation:visual:<storeyId>`, `representation:collision-static:<storeyId>` or `representation:collision-dynamic:<storeyId>`. Structural transforms are identity. Storeys parent to the building; anchors retain their original IDs and parent to the storey. A mesh parents to its explicit pivot when supplied, otherwise to its role-specific representation group. The same original anchor ID may occur once in each independent GLB. Parenting preserves world positions. Compare exact custom extras `bubo_id`, `bubo_semanticId`, `bubo_storeyId`, `bubo_kind`, `bubo_representation`, `bubo_collisionRole`, `bubo_materialId` and `bubo_parent_anchor_id`; display names do not identify a mesh.

`doorPoseConvention` is `closed-at-zero-final-open-clearance`. `clearanceBasis` is `construction-source`. Routes, signed door angles, full-leaf sweep bounds and clearance margins are exact copied construction evidence. They are not newly observed GLB navigation measurements. Independent geometric preservation requires actual triangle/material/pivot/aperture correspondence. Game-engine movement/collision remains separate.

## Verification, complete views and decisions

Producer receipts, separate visual/collision verification, complete view indexes/images and submitted decisions remain outside the sealed payload. They bind its raw package hash. Separate clean Blender processes import visual and collision GLBs. Bounded raw GLB parsing and actual imported world-space triangle/material/hierarchy evidence must agree with the immutable native scene. Allow seam/index splitting and cyclic triangle order while preserving winding, multiplicity and material identity; a matching box/count or copied producer hash is insufficient.

Approved linear tolerance measures coordinate/bounds/dimensional error. Angular tolerance is in degrees. Material serialization uses its separately named fixed epsilon. Never increase tolerances to hide float32 loss, apply spatial tolerance to shader scalars, or claim actual engine testing from portable export.

Review views cover every floor, four cardinal orthographic exteriors, roof, principal vertical section and a correctly aligned section through every stair/connector. Required count is `storeys + stairs + 6`. Record exact source-derived section planes and clipping, camera/projection/settings, package and both verification hashes, and every image hash. Render the imported delivered visual GLB. A source `.blend` render does not demonstrate delivered geometry. Missing/extra/stale views block admission.

`FloorPlanModelReview` records an explicit `approved` or `rejected` decision, reviewer/role/reason, `kind`, `human`, package hash, both verification hashes, complete view-index hash and the exact complete reviewed artifact inventory. An `independent-model` decision has `human:false`, a reviewer distinct from the model author and `independentReviewSha256:null`. A `human-model` decision requires `human:true` and the exact current independently approved review hash. Caller context must establish that this independent decision is approved and current; a matching arbitrary hash cannot establish that fact. Validation never synthesizes a decision or treats a rejected record as approval.

Root lifecycle admission retains original submitted files and exact task-local references, rechecks both, and owns latest-attempt/latest-decision selection. Any package/source/approval/tool/bundle/catalog/verification/view change invalidates dependent evidence. A current rejection, failed attempt or interrupted attempt cannot fall back to an older success. Technical verification, independent review, human model acceptance and runtime acceptance are separate states. This workflow grants no runtime promotion.

## Finite ceilings

All limits are positive cumulative ceilings, tightened further by the exact approved design and native operation allowance. Insufficient allowance fails explicitly, without simplification or fallback.

| Contract work | Hard ceiling |
| --- | ---: |
| Sealed inventoried payload bytes, including copied blend | 1,073,741,824; also approved `maxExportBytes` |
| Payload files excluding package.json | 2,048 |
| Semantic metadata bytes | 67,108,864 |
| Construction semantic meshes | 10,000; also approved `maxMeshes` |
| Source anchors | 32,768 |
| Source materials | 4,096 |
| Source vertices across representations | 2,000,000; also approved construction cap |
| Source triangles across representations | 10,000,000; also approved triangle cap |
| Fixed bundle basenames | 32 |
| glTF module inventory files / cumulative bytes | 8,192 / 268,435,456 |
| Reviewed artifact entries / cumulative bytes | 4,096 / 4,294,967,296 |
| Reviewer / role / reason characters | 128 / 128 / 4,096 |
| JSON nesting | 64 |

The reused v2 plan schema retains its own finite floor/room/wall/opening/stair/roof/polygon/work ceilings. Semantic schema arrays also bound routes to 100,000, door sweeps to 16,384 and route points to 2,000,000; exact source snapshots, metadata bytes and approved work remain tighter gates. Native execution separately bounds jobs, decoded GLB nodes/meshes/materials/accessors/primitives/vertices/triangles/comparison work, total operation outputs/logs/time, child memory and complete-view count/image bytes. A fresh child does not reset a cumulative operation allowance.

The pure `ExportPackageValidator` validates closed inventories, source snapshots, explicit ancestry, coordinate/numerical consistency and exact external review bindings. Infrastructure owns strict bounded parsing, complete filesystem inventory/hashing, source freshness and process/lifecycle admission. Audited Blender resources own production and measured clean re-import; independent review follows author freeze. Synthetic contract fixtures establish none of the latter evidence.
