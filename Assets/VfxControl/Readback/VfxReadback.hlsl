// VFX Control — particle attribute readback (opt-in).
//
// Point a "Custom HLSL" block at this file in the UPDATE context of a particle system you want to
// inspect and select the `VfxReadback` function. Put it in **Update** (not Initialize) so it re-runs
// every frame for every live particle.
//
// MULTI-INSTANCE: the function takes one `instanceId` input port. To capture several VisualEffect
// instances of the asset separately, add an **exposed Int property named `VfxReadbackInstanceId`** to
// the graph and wire it into the block's `instanceId` input — the VFX Control window auto-assigns each
// instance in the scene a distinct id (0,1,2…) via `SetInt`, so they land in separate buffer regions
// instead of overlapping. If you don't wire it the port defaults to 0 and you simply get a single
// (merged) instance, which is fine for one effect. See VfxControl.md → Debug tab → Particles.
//
// Each particle writes to a STABLE slot (so the spreadsheet rows don't jump) and stamps the slot with
// the current frame's `_VfxReadbackGeneration` (set by C# each frame, ≥1; 0 = never written). The tool
// shows the slots whose stamp equals the latest generation present — i.e. the live particles this
// frame; dead particles stop re-stamping and drop out.
//
// Layout (fixed — the tool decodes this exactly): slot = instanceId*kPerInstance + particleId%kPerInstance,
//   _VfxReadbackBuffer[slot*2 + 0] = float4(position.xyz, age)
//   _VfxReadbackBuffer[slot*2 + 1] = float4(color.rgb,    alpha)
//   _VfxReadbackGen[slot]          = generation stamp

#ifndef VFX_CONTROL_READBACK_INCLUDED
#define VFX_CONTROL_READBACK_INCLUDED

#define kVfxReadbackPerInstance  256u  // particle slots per instance (matches the C# tool)
#define kVfxReadbackMaxInstances 16u   // instance regions in the buffer (matches the C# tool)

// Globals — bound from C# via Shader.SetGlobalBuffer / SetGlobalInt.
RWStructuredBuffer<float4> _VfxReadbackBuffer;   // 2 float4 per particle slot
RWStructuredBuffer<uint>   _VfxReadbackGen;      // per-slot generation stamp
int                        _VfxReadbackGeneration; // current frame id (>=1), set by C# each frame

void VfxReadback(inout VFXAttributes attributes, int instanceId)
{
    // A Custom HLSL function body is compiled into EVERY pass that includes it (incl. the output
    // vertex/fragment passes), but only RUNS in the Update compute kernel. UAV writes to a global
    // RWStructuredBuffer aren't valid in the raster passes on all platforms (Metal), so restrict the
    // body to the compute stage — it's never invoked elsewhere anyway.
#if defined(UNITY_COMPUTE_SHADER) || defined(SHADER_STAGE_COMPUTE)
    uint inst = (uint)max(instanceId, 0);
    if (inst >= kVfxReadbackMaxInstances)
        return; // more concurrent instances than the debug buffer holds — skip the overflow

    uint slot = inst * kVfxReadbackPerInstance + (attributes.particleId % kVfxReadbackPerInstance);
    _VfxReadbackBuffer[slot * 2u + 0u] = float4(attributes.position, attributes.age);
    _VfxReadbackBuffer[slot * 2u + 1u] = float4(attributes.color,    attributes.alpha);
    _VfxReadbackGen[slot]              = (uint)_VfxReadbackGeneration;
#endif
}

#endif // VFX_CONTROL_READBACK_INCLUDED
