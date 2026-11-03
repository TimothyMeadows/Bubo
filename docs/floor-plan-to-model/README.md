# Architectural QA examples

These drawings come from the actual floor-plan CLI fixtures. They show the design foundation's empty-room architecture; they are technical QA examples, with no human building approval, 3D model or runtime acceptance implied.

The [contract guide](../../resources/floor-plan-to-model/README.md) defines dimensions, geometry and whole-package review. The [CLI fixtures](../../tests/test-floor-plan-cli.py) generate the windowed variant and verify all floor, roof, elevation and section sheets. These three images are examples from the complete independently reviewed drawing packages.

## Angled courtyard floor

![Dimensioned angled courtyard with its preserved open courtyard](courtyard-plan.png)

## Stacked stair flights

![Middle floor with separate stair UP and DOWN callouts and a labeled slab void](stacked-stairs-plan.png)

## Window section

![Building section with a measured window sill and head opening](window-section.png)

## Actual Blender construction

These owned procedural v2 examples were built twice in Blender 4.5.10 LTS, then independently loaded for source inspection and CPU review renders. Measured mesh geometry, floor identities, material assignments and separate visual/collision objects are verified against the approved engineering fixture inputs. The fixture review assertions are synthetic; they grant no human architectural or model acceptance.

The fixed review bundle uses eight Cycles samples for bounded diagnostic renders. Bright surfaces and visible sampling noise limit appearance judgments. GLB export/re-import and game-engine collision or movement evidence are separate stages.

![Actual single-storey architecture with a separate pivoted entry door](constructed-single-storey.png)

![Actual two-storey cutaway with stairs, landings, railings and preserved upper-floor opening](constructed-multi-storey-section.png)

![Actual angled courtyard with empty interior and an open central courtyard](constructed-courtyard.png)

## Delivered GLB packages

These views render the delivered visual GLBs after separate fresh-process visual and collision imports. Each sealed package preserves the editable source, approved drawings, exact dimensional semantics and construction ancestry. The fixed import adapter verifies SDK geometry and materials before loading the exact delivered corner-normal vectors. Read the [export contract](../../resources/floor-plan-to-model/EXPORT.md) for verification and separate model-review decisions.

The complete engineering run covers all three layouts and 23 views: every floor, four exterior elevations, roof, principal section and each stair connection. The images below are unchanged excerpts from that set. Eight-sample CPU noise and bright highlights limit appearance judgments; synthetic design/review assertions grant no real human architectural/model acceptance, engine behavior or runtime promotion.

![Delivered single-storey GLB floor and entry aperture](exported-single-storey-floor.png)

![Delivered two-storey GLB section through its stairs and slab opening](exported-multi-storey-stair-section.png)

![Delivered angled-courtyard GLB floor with the courtyard preserved](exported-courtyard-floor.png)
