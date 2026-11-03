# Schematic-to-model design foundation

The native `bubo-assembly-design/v1` contract describes dimension-driven mechanical assets. It separates reusable part definitions from every physical instance, explicit subassembly/part hierarchy, local frames, material/detail choices, mating interfaces, joint travel, bounded mechanism declarations, flexible routes and terminal connections. This release validates design declarations and produces schematic previews; construction, coupled-motion execution, skinning, model export and production animation remain unavailable.

Every part definition is independently specified before assembly. Identical wheel or bolt definitions may share geometry later, but every physical occurrence must retain its own identity, object and transform. Declared envelopes and dimensioned schematics are design assertions; they do not prove that a later solid or exported mesh matches those dimensions.

`assembly-design.schema.json` documents the closed JSON shape. Native parsing additionally rejects unknown/case-mismatched/duplicate properties and enum values. Native validation checks finite dimensions, budgets, hierarchy, references, local envelopes, profiles, routes and explicit supported mechanism parameter sets. Geometric construction and sampled motion require later stages; no declaration establishes manufacturability, electrical behavior, motion quality or engine acceptance.

## Coordinates and feature conventions

All distances are metres in right-handed XY, Z up. Frames apply X, then Y, then Z rotations about fixed parent axes (matrix Rz × Ry × Rx), then translation. Every feature has a part-local frame. Part-local declared envelopes run from zero to `size`. Subassemblies and physical instances have local frames relative to their named parent. Routes use shared world coordinates.

- `box`: `width`, `depth`, `height`; local extent zero to those dimensions.
- `cylinder`: `radius`, `length`, integer `segments`; centered on local XY origin, extending along positive Z. Translate the feature frame by its radius on X/Y for a zero-based part envelope.
- `extrude`: `depth`; a simple counter-clockwise XY profile extruded along positive Z.
- `revolve`: `angleDegrees`, integer `segments`; profile X is nonnegative radius and Y is axial Z. Declared envelopes conservatively enclose a full radial sweep.
- `sweep`: `radius`, integer `segments`; a tube along the explicit local XYZ path.
- `loft`: no scalar parameters; two or more framed simple XY profiles with matching point counts.

Profiles omit the repeated closing vertex. Additive declarations must fit their part envelope; subtractive openings may extend beyond it. Six orthographic drawings, every declared internal section, independent part sheets, exploded assembly, connections and BOM must fit the complete-package preview budget.

## Mechanism declarations

Joint travel is metres for sliders and degrees for hinges/rotary/screw joints. Screw `pitch` is signed metres per revolution; other joints carry zero pitch. Only fixed joints have zero travel. Every joint names distinct owning physical instances and interfaces; its normalized axis is expressed in the parent interface's local frame. Parent and child interface origins must coincide in the declared rest assembly within the approved linear tolerance. This validates the authored rest assembly; it does not establish swept solid clearance.

Mechanisms declare an ordered driver followed by followers. Supported declarations are spur gears (`ratio`, two rotary joints), rack-and-pinion (`pitchRadius`, rotary then slider), pulley/belt (`ratio`, two rotary joints), slider-crank (`crankLength`, `rodLength`, `offset`, three rotary joints then slider), and planar four-bar (`groundLength`, `inputLength`, `couplerLength`, `outputLength`, four rotary joints). Both linkage families use four ordered connections: ground-to-input, input-to-coupler, coupler-to-output, and ground-to-output. The last joint is an explicit closing constraint, distinct from the acyclic scene hierarchy. A physical instance has one owning joint outside that explicit closure; a follower cannot be independently controlled by multiple mechanisms. Branch is explicitly `open` or `crossed`, with spur gears and rack-and-pinion using `open`. These are typed design inputs, not a claim that an arbitrary linkage is executable or nonsingular across its range.

Every flexible physical occurrence owns one route and explicit deformation limits. Open routes name actual instance-owned interfaces/terminals; electrical connections bind matching terminal kinds and the exact route endpoints. Closed belt routes have no fabricated endpoints. Wires, hoses, springs and belts require actual owned skins and measured deformation in later construction/readiness deliveries.

## Examples and approval

`examples/vehicle.json`, `examples/device.json` and `examples/vessel.json` are serialized from the executable `DesignExamples` fixtures, validated before writing. They use a synthetic repeated `a` style hash. Replace it with the actual selected host style identity for production. The examples illustrate an explicit bounded component inventory, not a complete commercial product or approved production asset.

Whole-design approval must bind the exact specification, every emitted drawing/BOM artifact, selected style and runtime/host identities. Revisions require a new version and retained parent ancestry. Technical preview success and synthetic test reviews never establish human design/model acceptance, runtime acceptance or promotion.
