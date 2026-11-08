// The player's quads (ui/Renderer): positions in screen pixels, the rest passed on
#version 460

layout (location = 0) in vec2 inPos;
layout (location = 1) in vec2 inUv;
layout (location = 2) in vec4 inColor;
layout (location = 3) in vec4 inShape;   // the point from the quad's centre (x, y), its half size (w, h)
layout (location = 4) in vec2 inExtra;   // the corners' radius, the kind (0 textured, 1 coverage, 2 shape)

layout (location = 0) out vec2 vUv;
layout (location = 1) out vec4 vColor;
layout (location = 2) out vec4 vShape;
layout (location = 3) out vec2 vExtra;

layout (std140, binding = 0) uniform Screen
{
    vec4 scale;    // 2 / width, 2 / height
} screen;

void main()
{
    // (y down the screen: the top of the screen is +1)
    vec2 at = inPos * screen.scale.xy - 1.0;
    gl_Position = vec4(at.x, -at.y, 0.0, 1.0);
    vUv = inUv;
    vColor = inColor;
    vShape = inShape;
    vExtra = inExtra;
}
