# Construction-ready building plans

`bubo-building-plan/v2` adds an explicit `construction` profile to the existing dimension-driven plan. The complete plan, detail sheet, floors, roof, elevations and sections participate in one whole-building approval. Editing any profile value, material, opening, floor, stair or roof changes source identity and invalidates dependent approval/model readiness.

`bubo-building-plan/v1` remains design-only. Its original schema and examples retain their exact bytes. The new nullable native property is omitted when writing v1, and a v1 input must not contain a `construction` key, even null. Construction never upgrades an old asset, fills missing dimensions, reuses an old approval or rewrites historical source.

The closed [v2 source schema](building-plan-v2.schema.json) requires every construction property. Native validation also checks relational dimensions, actual geometry and finite work bounds; schema conformance alone does not establish readiness. Profiles are authored per building before whole-package approval. Values in executable QA examples are synthetic designs, not general production defaults.

## Shared dimensional conventions

All source and construction positions use the same XY metres and absolute Z-up datum. Floor elevation is finished-floor top and structural slabs extend downward. Storey height preserves the declared clear vertical space. Walls and nominal openings keep their approved dimensions. Roof vertices describe the underside and exact source edge; `roofThickness` extends vertically upward, including on a pitched plane. No automatic eave expansion, remeshing, welding or geometric simplification is authorized.

`ceilingThickness: 0` shares the existing structural slab/roof underside and assigns its ceiling material without creating a coincident duplicate solid. A positive ceiling panel starts at the declared clear ceiling and extends upward. It must fit the actual available geometry and cannot reduce approved clear height or intersect another structural component.

The existing `DetailProfile` supplies plinth/cornice heights and projections, eave overhang and fascia depth. In v2, `eaveOverhang` is the nonnegative outward distance between the physical exterior wall face and the authored roof edge. Zero declares no outward eave. Native bounded straight-edge checks compare this declaration to source geometry; the field never moves a roof vertex. Courtyard openings stay uncovered. A fascia section may derive its horizontal thickness from the explicitly approved roof thickness; its vertical depth is the declared fascia depth.

## Openings and doors

The positive `doorFrameWidth` and `windowFrameWidth` extend outside the nominal opening, preserving its source aperture. Frame depth is the additional projection beyond both structural wall faces. No frame bar silently shrinks a reviewed opening. `glassThickness` stays within the wall depth; glazing uses the declared glass material, transmission and IOR.

The wall's left normal is derived from Start to End. A closed door's leaf midplane is at negative wall face + `doorRevealDepth` + half `doorLeafThickness`. Reveal and leaf must fit the approved wall thickness. `doorLeafGap` bounds each leaf edge and must be at least half leaf thickness plus linear tolerance to clear the adjacent hinge jamb. Leaf dimensions must remain positive and the final-open leaf may not remove player-width clearance.

A Start pivot is at opening offset + gap; an End pivot is at offset + width - gap. Both use the declared leaf midplane and floor elevation + sill. Meshes initially retain the closed pose while parenting preserves world positions. The positive design `doorOpenAngleDegrees` is in [90,180]. Actual rotation is signed by `(Swing.Left ? +1 : -1) * (Pivot.Start ? +1 : -1)`. Full-body sweep evidence uses that signed angle; the anchor initially remains at rotation zero.

Door leaves retain separate dynamic collision geometry. Static proxies preserve the opening; they never pretend a closed dynamic door is absent. Route evidence states its final-open door pose. Door interaction, animation, gameplay authorization and actual physics behavior belong to later engine integration.

## Stairs, landings and railings

In v2, `StairPlan.Width` is the unchanged clear traversal width. `railingOffset` is the gap from this clear edge to the railing inner face. The complete physical tread/support envelope is derived as:

```text
physicalWidth = stair.Width + 2 * (railingOffset + max(railThickness, balusterThickness))
railCentreFromFlightCentre = stair.Width/2 + railingOffset + max(railThickness, balusterThickness)/2
```

This physical envelope must be explicitly contained in the approved upper slab opening and both connected clear rooms. Construction never widens the void automatically. V2 executable examples explicitly author a 1.4m slab opening for a 1.2m clear flight with a 1.34m physical support width. Lower/upper landings preserve their source polygons and dimensions.

`railingHeight` is the minimum top height above a tread or landing. Rail sections and balusters use their declared thicknesses, and the maximum clear baluster gap must be smaller than the player diameter. Endpoints and evenly distributed remainder may make actual gaps smaller, never larger than that explicit maximum. This is geometric consistency, not jurisdictional safety-code certification. Tread/pitch, waist structure and inter-flight headroom retain their explicit source conventions. Real geometry checks must account for rail supports, slabs, landings, roof and all architectural detail.

## Material roles and finite limits

The profile explicitly references declared `ceilingMaterialId`, `doorMaterialId`, `frameMaterialId`, `glassMaterialId` and `railingMaterialId`; wall, floor, stair and roof material assignments remain in their source elements. `glassTransmission` is in [0,1] and `glassIor` is in [1,3]. Transmissive glass cannot silently turn structural, ceiling, door, frame or railing material into glass by sharing its identity.

All production dimensions must meet the plan's finite minimum feature and coordinate policy. No fixed authoring constant supplies an omitted design dimension. Native caps support at most 10,000 declared meshes, 2,000,000 construction vertices, 100,000 arrangement cells and a 256MiB job; job bytes also stay within the source export-byte budget. The v2 source triangle ceiling is at most 4,000,000. Actual resource use, serialized bytes and tool/process ownership are independently bounded and may reject a valid design whose chosen budget is insufficient. Never increase a budget silently.

## Native scene and evidence boundary

`bubo-floor-plan-construction-scene/v1` carries world-metre indexed triangles, exact semantic/storey identities, material roles, stable anchors, source `linearTolerance`, counts/bounds and measured clearance evidence. Visual and collision meshes are separate. A semantic mesh may contain several closed nonoverlapping convex shells; it need not be a single connected topological component. Per-triangle material overrides retain floor/ceiling assignments without duplicating coincident surfaces.

A measured route margin is conservative centerline separation minus the declared player radius. A door-sweep margin covers the complete leaf body and subtracts any conservative envelope error. Both must remain nonnegative within source tolerance; unknown or false measurements reject readiness rather than becoming optimistic flags. Source graph connectivity alone never establishes portal-to-portal navigability, courtyard clearance, door movement or actual mesh readiness.

A dedicated fixed, audited Blender 4.5 LTS bundle consumes the exact hash-bound native scene job. Editable Blender source and construction measurements are versioned non-runtime outputs. Bounded process execution, cancellation, immutable failed attempts and source/resource/tool identities remain mandatory. A successful build is neither independent model acceptance nor runtime promotion. Portable GLB/package export and independent re-import verification are a separate delivery, and no engine collision/movement behavior is certified here.
