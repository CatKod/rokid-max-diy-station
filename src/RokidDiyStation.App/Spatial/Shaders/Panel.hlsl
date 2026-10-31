// SPDX-License-Identifier: MIT
//
// The vertex and pixel shaders for the pinned panel.
//
// Compiled at build time by fxc (see build-shaders.ps1) into DXBC and
// embedded as a resource, so the app never needs a runtime HLSL
// compiler.
//
// Why a real 3D draw rather than a 2D affine warp of the captured
// bitmap
// -----------------------------------------------------------
// The obvious alternative is to project the panel's four corners onto
// the screen and then draw the captured bitmap as a screen-space
// quad, warping it with four corner offsets.  That is wrong for a
// perspective projection, and it is wrong in a way that is visible:
//
//  * A perspective projection of a rectangle is a general quadrilateral
//    whose edges are straight lines in 3D but whose interior is NOT a
//    bilinear image of the source rectangle.  Bilinear warping cannot
//    represent it; the classic fix is a projective (homography) warp,
//    which needs a fragment shader anyway.
//  * The panel is a plane in the world.  Rendering it as a plane in
//    the world is one matrix multiply.  Rendering it as a screen-space
//    warp is a matrix multiply plus a homography plus a per-pixel
//    division.
//
// So we do the honest thing: the vertex shader takes the panel's
// corners in world space, and the pipeline does the perspective
// division for us.
//
// The panel's corners are supplied as four world-space positions plus
// their UVs, so the quad's shape can change every frame (it rotates
// with the panel, and its perspective foreshortening changes as the
// head moves) without any CPU-side projection.
//
// The near-black clamp
// -------------------
// The Rokid Max's Micro-OLED panels are see-through: a black pixel
// emits no light and is therefore optically transparent.  That is what
// makes the surrounding room visible through the display, and it is
// why the background is cleared to pure black.
//
// The catch is the Lontium DP bridge's limited-range quirk in 120 Hz
// mode: it interprets the video range as 16-235 rather than 0-255.  A
// nominally-black pixel then lands at 16, which lights the panel up as
// a faint grey rectangle - a glowing box floating in the room, which
// destroys the illusion completely.
//
// So we clamp anything darker than the limited-range black point to
// true black in the shader.  The threshold is 16/255, which is where
// the bridge's 16 would land.

#define LIMITED_RANGE_BLACK (16.0 / 255.0)

// Constant buffer layout (see PanelConstants in C#).
//   0: float4x4 mvp        (64 bytes)
//   1: float4   uvTransform (16 bytes)  xy = scale, zw = offset
// Total 80 bytes, which fits in a 16-byte-aligned constant buffer.

cbuffer Constants : register(b0)
{
    float4x4 mvp;
    float4 uvTransform;
};

Texture2D g_panelTexture : register(t0);
SamplerState g_panelSampler : register(s0);

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

VSOut VSMain(VSIn input)
{
    VSOut output;
    output.position = mul(float4(input.position, 1.0f), mvp);
    output.uv = input.uv * uvTransform.xy + uvTransform.zw;
    return output;
}

float4 PSMain(VSOut input) : SV_TARGET
{
    float4 color = g_panelTexture.Sample(g_panelSampler, input.uv);

    // Anything at or below the bridge's limited-range black point
    // becomes true black, so the panel's own background is optically
    // transparent rather than a faint grey wash.
    float luma = dot(color.rgb, float3(0.299, 0.587, 0.114));
    if (luma <= LIMITED_RANGE_BLACK)
    {
        return float4(0.0f, 0.0f, 0.0f, 1.0f);
    }

    return color;
}
