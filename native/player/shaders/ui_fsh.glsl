// The player's quads (ui/Renderer): a texture times the colour, a glyph's coverage in the colour, or
// a rectangle with rounded corners (its distance to the edge, antialiased over a pixel)
#version 460

layout (location = 0) in vec2 vUv;
layout (location = 1) in vec4 vColor;
layout (location = 2) in vec4 vShape;
layout (location = 3) in vec2 vExtra;

layout (location = 0) out vec4 outColor;

layout (binding = 0) uniform sampler2D tex;

void main()
{
    float kind = vExtra.y;
    if (kind < 0.5)
        outColor = texture(tex, vUv) * vColor;
    else if (kind < 1.5)
        outColor = vec4(vColor.rgb, vColor.a * texture(tex, vUv).r);
    else
    {
        float radius = vExtra.x;
        vec2 q = abs(vShape.xy) - vShape.zw + radius;
        float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
        outColor = vec4(vColor.rgb, vColor.a * clamp(0.5 - d, 0.0, 1.0));
    }
}
