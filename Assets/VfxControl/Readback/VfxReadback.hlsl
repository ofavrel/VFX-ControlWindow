// VFX Control — particle attribute readback (opt-in).
//
// Point a "Custom HLSL" block at this file and select the `VfxReadback` function. Works in the
// **Update** context (re-runs every frame for every live particle) or the **Output** context (runs per
// rendered particle — dead particles are naturally excluded). Avoid Initialize: it only fires at
// particle birth, so the list would be empty on frames with no spawn.
//
// MULTI-INSTANCE: the function takes an `instanceId` input port. To capture several VisualEffect
// instances of the asset separately, add an **exposed Int property named `VfxReadbackInstanceId`** to
// the graph and wire it into the block's `instanceId` input — the VFX Control window auto-assigns each
// selected instance a distinct id via `SetInt`. Unwired → id 0 (single merged instance).
//
// MULTI-SYSTEM: the function also takes a `systemId` input port. To debug several systems of one graph
// at once, put a VfxReadback block in EACH system and wire each block's `systemId` to a distinct
// constant Int (0,1,2…). The window shows a legend (systemId → system name) so you know which number to
// use, and a System column. Without this, two systems would write the same slots (particleId restarts
// per system) and clobber each other. Unwired → systemId 0 (must wire it for multi-system / correct name).
//
// This writes a FIXED superset record of common attributes (stride below). Reading an attribute the
// system doesn't write just yields its default and does NOT add it to the system's stored layout — so
// the window uses each system's actual attribute layout to show only the columns it really uses.
//
// Record layout (fixed — the C# tool decodes this exactly), kVfxReadbackStride float4 per particle at
// base = slot*kVfxReadbackStride. The slot partitions the buffer by system then instance then particle:
//   slot = (systemId*kVfxReadbackMaxInstances + instanceId)*kVfxReadbackPerInstance + particleId%kVfxReadbackPerInstance
// (systemId/instanceId are recovered from the slot, so they're not stored in the record):
//   +0 position.xyz, age            +1 velocity.xyz, lifetime      +2 color.rgb, alpha
//   +3 direction.xyz, size          +4 targetPosition.xyz, mass    +5 scale.xyz, texIndex
//   +6 angle.xyz, alive             +7 angularVelocity.xyz, particleId   +8 pivot.xyz, (pad)
// _VfxReadbackGen[slot] = generation stamp.

#ifndef VFX_CONTROL_READBACK_INCLUDED
#define VFX_CONTROL_READBACK_INCLUDED

#define kVfxReadbackPerInstance  256u  // particle slots per instance (matches the C# tool)
#define kVfxReadbackMaxInstances 16u   // instance regions per system (matches the C# tool)
#define kVfxReadbackMaxSystems   8u    // system regions in the buffer (matches the C# tool)
#define kVfxReadbackStride       9u    // float4 per particle record (matches the C# tool)

// Globals — bound from C# via Shader.SetGlobalBuffer / SetGlobalInt.
RWStructuredBuffer<float4> _VfxReadbackBuffer;   // kVfxReadbackStride float4 per particle slot
RWStructuredBuffer<uint>   _VfxReadbackGen;      // per-slot generation stamp
int                        _VfxReadbackGeneration; // current frame id (>=1), set by C# each frame

void VfxReadback(inout VFXAttributes attributes, int instanceId, int systemId)
{
    // Plain RWStructuredBuffer writes (no UAV atomics, no compute-only locals) so this compiles and
    // runs in both the Update compute kernel and the Output vertex/fragment passes. Do NOT add a
    // UNITY_COMPUTE_SHADER guard — it would silently disable the Output-context use.
    uint sys  = (uint)max(systemId, 0);
    uint inst = (uint)max(instanceId, 0);
    if (sys >= kVfxReadbackMaxSystems || inst >= kVfxReadbackMaxInstances)
        return; // more concurrent systems/instances than the debug buffer holds — skip the overflow

    uint slot = (sys * kVfxReadbackMaxInstances + inst) * kVfxReadbackPerInstance
              + (attributes.particleId % kVfxReadbackPerInstance);
    uint b = slot * kVfxReadbackStride;

    _VfxReadbackBuffer[b + 0u] = float4(attributes.position,       attributes.age);
    _VfxReadbackBuffer[b + 1u] = float4(attributes.velocity,       attributes.lifetime);
    _VfxReadbackBuffer[b + 2u] = float4(attributes.color,          attributes.alpha);
    _VfxReadbackBuffer[b + 3u] = float4(attributes.direction,      attributes.size);
    _VfxReadbackBuffer[b + 4u] = float4(attributes.targetPosition, attributes.mass);
    _VfxReadbackBuffer[b + 5u] = float4(attributes.scaleX, attributes.scaleY, attributes.scaleZ, attributes.texIndex);
    _VfxReadbackBuffer[b + 6u] = float4(attributes.angleX, attributes.angleY, attributes.angleZ, attributes.alive ? 1.0f : 0.0f);
    _VfxReadbackBuffer[b + 7u] = float4(attributes.angularVelocityX, attributes.angularVelocityY, attributes.angularVelocityZ, (float)attributes.particleId);
    _VfxReadbackBuffer[b + 8u] = float4(attributes.pivotX, attributes.pivotY, attributes.pivotZ, 0.0f);

    _VfxReadbackGen[slot] = (uint)_VfxReadbackGeneration;
}

#endif // VFX_CONTROL_READBACK_INCLUDED
