# Mechanical assembly and articulation readiness

`assemble --blender ABSOLUTE_EXE` places independently verified parts in an editable Blender assembly. `prove-motion --blender ABSOLUTE_EXE` evaluates its retained diagnostic actions and reopens the saved result in a separate Blender process. Every physical occurrence keeps its own object, identity and transform, including occurrences sharing a rigid mesh. Flexible occurrences own their meshes, armatures and skin weights.

These operations require an explicitly authored `bubo-assembly-design/v2` and `bubo-assembly-profile/v1`, fresh approval of the complete drawing package, and the latest successful part build. A v1 design remains valid for design previews and part construction; its approval never becomes v2 assembly approval. Adding or revising the profile changes the complete design and requires a new version and approval.

## Design choices made before approval

The v2 profile declares minimum diagnostic sample counts, geometry-proof work, minimum surface separation, exact permitted contact boxes, flexible rig/travel choices and coupling pitch geometry. The complete preview adds `assembly-details.svg` and `flex-rigs.svg` to all orthographic, section, part, exploded, connection and BOM sheets. Review every sheet together.

Contact boxes are expressed in the first named instance's local frame. They permit only the explicitly covered material-interference region; a joint or shared parent grants no general collision exemption. The verifier uses actual triangles, containment and surface distances. Conservative configurations it cannot certify fail explicitly. No convex proxy replaces a bore, opening or moving part. Finite sampled clearances do not establish continuous collision or general physics safety.

Distance evidence distinguishes an actual surface minimum from a conservative separation lower bound. Native assembly verification uses clearance certificates: actual triangle, containment and required-gap checks establish the approved separation without chasing every global closest point. Such records have a null `minimumSurfaceDistance` and explicitly identify a triangle-clearance or disjoint-bound certificate. Contact exemptions remain explicit; a zero lower bound never claims a positive gap. Reuse of an unchanged topology or pair proof is confined to one cumulative operation and requires exact current geometry and pose inputs; sampling and approved work limits remain in force.

The pitch-fit records cover external spur gears, rack-and-pinion and open/crossed pulley systems. They bind declared radii, applicable tooth counts, axes, placement and ratios. Four-bar and slider-crank lengths and branches remain in their mechanism declarations. These checks and solid-clearance evidence are separate from manufacturing certification and human review of the chosen tooth/hardware detail.

## Rigid motion

All frames use double-precision, right-handed XY metres and Z up. Column bases contain unit X/Y/Z axes; local Euler declarations apply Rz * Ry * Rx. Joint axes are parent-interface local. Angular joint coordinates are degrees, slider coordinates are metres, and screw pitch is metres per complete revolution. Values are absolute declared coordinates; motion is relative to the declared rest coordinate.

Fixed, hinge, rotary, slider and screw joints preserve their actual interface origins and rest-relative frames. Spur ratios are signed follower angular delta divided by driver delta. Rack travel is driver radians times the declared pitch radius along the approved slider axis. Pulley branch and signed ratio control follower direction. Explicit four-joint slider-crank and four-bar loops use their declared open/crossed branch and measured pivot lengths. Unsupported geometry, singular closure, contradictory axes, conflicting followers or out-of-range travel fails.

Diagnostic clips exercise independent drivers separately with their coupled followers. They include range endpoints, rest and intermediate poses, plus complete rigid pose-return cycles when the approved travel permits one. Sampling is refined to preserve angular turns within finite limits. Clip time is normalized to one second; diagnostics prescribe no production speed. They do not certify arbitrary simultaneous independent-driver combinations or finished animation.

Rigid cycle coverage and belt material travel are different observations. A belt's material rings need not return after one rigid mechanism cycle. The actual sampled phase is retained rather than reset or relabeled as a closed material loop.

## Flexible parts

Wire, hose, spring and belt geometry must be an approved single additive circular sweep. Its original rings must match the owned route after applying the feature and instance frames. Open paths have `skinSegments + 1` rings; closed paths have `skinSegments` rings and explicitly repeat the first point only in the shape feature. Route data omits that closing duplicate. The workflow never invents longitudinal subdivisions or replaces an insufficiently sampled component.

Each ring owns a real bone and one-hot vertex weights under an unweighted root. The armature and mesh share the declared parent and transform. Ring frames are part-local, with Y tangent to the centreline. Source vertices, indices, material, actual ports and prototype ancestry remain bound. Independent reopening verifies actual bones, weights, channels and evaluated vertices; native code recomputes deformation and ring measurements from delivered binary geometry.

Open routes attach to explicitly owned rigid interfaces or terminals. Their policies declare endpoint-distance bounds and minimum segment ratio, alongside the part's arc-length, maximum stretch and bend limits. Springs retain their authored helical source and explicit compression/travel bounds. Closed belts name a rotary pulley driver and explicit metres per degree; material phase follows the original closed path. Unsupported deforming attachment chains, excessive stretch/compression/bend, detached endpoints or inadequate sampling fails without fallback.

Prototype-local linear continuation checks are conservative auxiliary evidence. Actual Blender interpolation and complete reviewed motion remain separate; local bounds do not certify every world-space time or physical behaviour.

## Execution and evidence

The seven-file audited bundle consists of five assembly scripts plus the reviewed part geometry/admission helpers. Jobs are data only, hash bound and offline. Production requires an existing registered Blender 4.5 LTS and supported 64-bit Windows aggregate committed-memory enforcement. No installation or dependency download occurs. Version inspection, authoring and independent reopening share one cumulative time/log allowance; cancellation retires owned descendants.

One asset lock protects design, parts, assembly and proof state. Failed/interrupted attempts remain retained. A new or failed latest part/assembly attempt cannot silently reuse an older proof. Proof binds the exact current assembly scene, original job/reports/native evidence, measured geometry and working bundle. Changing any relevant input invalidates dependent evidence.

The current bounded implementation admits at most 4,096 physical instances, 32 contact zones, 250,000 evaluated vertices/triangles per pose, 16,384 output files, 32,768 native rigid pose records and 16,384 flexible ring-frame records per operation. Jobs and each report/native evidence file have an 8 MiB ceiling. Each complete assembly or diagnostic output certificate has one cumulative geometry ledger, at most 50 million checks for its rest and every diagnostic pose together; actual approved budgets may be lower. Source-certificate revalidation is separate, and the linked command deadline bounds total execution. Partial certificates cannot be combined across fresh ledgers, and verification grants no new generation or attempt allowance. Full-resolution unsupported inputs are rejected rather than simplified. `doctor` checks the exact prospective job before reporting readiness.

Assembly outputs contain `assembly.blend`, indexed rest geometry and identical author/reopen reports. Proof outputs contain `diagnostic.blend`, retained real actions, rest and sampled geometry, and identical motion reports. Jobs, assembly reports and native evidence use compact JSON while retaining every field, instance, rig and sample under the same byte limits. `BUBOPRT1` preserves original prototype vertex and triangle indices here so native code can independently check LBS and ring membership. `BUBOSKN1` weight identities cover actual owned per-vertex ring assignments. Native clearance/deformation evidence remains separately hashed outside producer output.

`status` revalidates current source, approval, parts, tool, scripts, jobs and payloads. Technical assembly and sampled readiness grant no portable export, human model acceptance, engine acceptance or asset promotion. Canonical GLB packaging and the preserve-only mixed mechanical animation handoff are the next delivery.

The versioned examples are explicitly scoped engineering fixtures: a wheeled platform, an opening electronic enclosure with separate threaded fasteners and a curved hull with bearing/rudder geometry. Fixture approvals in automated tests are synthetic; they are never production asset approvals.
