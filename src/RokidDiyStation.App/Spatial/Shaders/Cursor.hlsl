// SPDX-License-Identifier: MIT
//
// The pixel shader for the virtual 3D cursor.
//
// Why a separate shader at all
// ----------------------------
// The panel's own fragment shader (Panel.hlsl) clamps anything darker
// than the Lontium DP bridge's limited-range black point (16/255) to
// true black, so that the area around the panel is optically
// transparent on the Micro-OLED display.  That clamp is exactly
// wrong for the cursor: the cursor's legibility comes from a DARK
// OUTLINE around a WHITE core, and a clamp would eat the outline and
// leave a featureless white blob.
//
// So the cursor gets its own shader, with no clamp at all.  It outputs
// the texture's colour verbatim, in full range.
//
// The vertex shader is shared with the panel (Panel.vertex.cso): the
// cursor is a textured quad in world space, exactly like the panel, and
// the two use the same model-view-projection constant buffer.
//
// Constant buffer layout is the panel's (see Panel.hlsl):
//   0: float4x4 mvp        (64 bytes)
//   1: float4   uvTransform (16 bytes)

Texture2D g_cursorTexture : register(t0);
SamplerState g_cursorSampler : register(s0);

struct VSIn
{
    float3 position : POSITION;
    float2 uv       : TEXCOORD0;
};

struct VSOut
{
    float4 position : SV_POSITION;
    float2 uv       : TEXCOORD0;
};

float4 PSCursor(VSOut input) : SV_TARGET
{
    // No luma clamp here on purpose — see the header comment.
    return g_cursorTexture.Sample(g_cursorSampler, input.uv);
}
