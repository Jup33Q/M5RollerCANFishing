# RollerCAN Reel Model — Blender Modeling Prompt (Kimi K3, English)

> 中文版本：[BLENDER-MODEL_zh.md](BLENDER-MODEL_zh.md)
>
> How to use: paste this entire file to Kimi K3. The workspace must already contain
> the four reference renders under `assets/model-ref/`.
> Deliverables: `blender/roller_model.py` (parametric build script) +
> `roller_haptic.blend` + a preview render + `RollerHaptic.fbx` for Unity.

---

## Task

Rebuild the RollerCAN fishing-reel model parametrically in Blender (macOS Steam
build 5.2.1, headless: `blender --background --python roller_model.py`).
**Look at the reference renders BEFORE modeling**:

- `assets/model-ref/preview.png` — base + rotor, first version
- `assets/model-ref/preview_crank.png` — final form with the crank (THIS is the target)
- `assets/model-ref/axis_check.png` — axis-check reference
- `assets/model-ref/fbx_reimport_check.png` — FBX reimport check reference

View each with ReadMediaFile and model against them.

## Model spec

The whole machine is scaled up by `GLOBAL_SCALE = 10.0` (real millimeter scale is too
small; apply scale via `data.transform` + positions ×10 — **never via ops**, which
double-scales parented children). Overall width ≈ 0.54 m.

Parts (real dimensions ×10):

1. **RollerCAN_Rotor** (motor rotor, cylinder, 32 sides): rotates about the Z axis
   (the motor axis).
   - Bright-yellow side-wall stripe `Rotor_Stripe` (near the crank side) + opposite
     rim dot `Rotor_RimDot` (16 sides) — both rotate with the rotor = rotation
     indicator. **The rotor's top face is fully covered by the square body, so the
     indicator must live on the side wall.**
   - The crank set (CrankHub axle seat + CrankArm arm + CrankAxle + CrankGrip barrel
     grip) hangs off the **rotor's free end face (z=0 bottom face), outside it**;
     after modeling, **join the four crank parts into the RollerCAN_Rotor mesh**
     (keep the multiple material slots).
2. **Static_Group** (static parts): square body (Square), flange (Flange), hub (Hub),
   shell (Shell), face plate (Face), screen (Screen = single quad plane, UV 0-1
   full-frame).
3. Hierarchy: `Assembly` (empty root) → `RollerCAN_Rotor`, `Static_Group`.

Materials: 11 separately named per-part materials
(Rotor/Square/Flange/Hub/Arm/Axle/Grip/Shell/Face/Screen/AccentYellow).

## Crank modeling (the hard part — follow exactly)

- CrankArm: **tapered NURBS path** — a NURBS curve as the path, radius tapering from
  9.6 mm at the root to 6.4 mm at the tip, arm length 75 mm. NURBS resolution 8.
- CrankGrip: **hand-lathed barrel grip** (30 mm) from a NURBS profile — **the Screw
  modifier no longer acts on curves in Blender 5.2**, so lathe manually: evaluate the
  NURBS profile to a polyline → spin it yourself (32 segments).
- Capping the arm ends: **deterministic manual center fans** — set the curve's
  `use_fill_caps=False`, convert to mesh, then with bmesh find the 2 boundary loops,
  add a center vertex to each and build a triangle fan (same style as the TRIFAN
  cylinder caps). **The curve's built-in caps / poke produce unreliable topology —
  do not use them.**
- Acceptance topology: the arm has 248 quad faces + 2×8 triangle fans, no n-gons,
  no open edges.

## Poly budget (whole model ≈ 1700 vertices)

- Cylinders uniformly 24-sided (main rotor 32, pins/dots 16)
- Caps use `TRIFAN` center-fan topology, fan faces forced flat to prevent shading
  blotches
- Bevel segments 2; NURBS resolution 8; lathe 32 segments

## Rig & export (critical)

- The .blend may keep a rig: Bone_Root (bone-parents Static_Group) + Bone_Rotor
  (bone-parents the rotor mesh, about the Z axis) — for Blender-side animation only.
- **The FBX export must strip the rig**: before exporting, re-parent rotor/grp_st
  from the bones back to Assembly, and exclude ARMATURE from `object_types`.
  Importing a rigged FBX into Unity double-renders the skinned mesh (white ghosting)
  and wrecks the axis/scale — never export ARMATURE.
- FBX node names/hierarchy must match the spec above (the Unity scene references
  fileIDs that stay stable only if names do).

## Preview & acceptance (do every step — this is what guarantees success)

1. `blender --background --python roller_model.py -- --preview` renders
   `blender/preview_crank.png` (pose Bone_Rotor at 35° to verify follow-through).
2. **Inspect the preview with ReadMediaFile**: compare against
   `assets/model-ref/preview_crank.png` — overall proportions, crank shape, stripe
   position, full-frame screen UVs.
3. The script prints vertex/face counts — verify ≈1700 vertices and clean arm
   topology (no n-gons, no open edges).
4. On failure: read the error, fix the script, re-run — iterate until the preview
   matches the reference. **Never deliver a model you have not eyeballed.**

## Verified pitfalls (do not step on these again)

- Blender 5.2 Screw does not act on curves → manual lathe
- Built-in curve caps are topologically unreliable → bmesh manual center fans
- Scale via data.transform + positions ×10, never ops (double-scaling children)
- Fan faces not forced flat → shading blotches
- FBX with ARMATURE into Unity → instant failure
