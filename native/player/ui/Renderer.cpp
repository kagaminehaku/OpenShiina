// The Switch player's drawing on deko3d (see Renderer.h)
#include "ui/Renderer.h"

#include <switch.h>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <stdexcept>

namespace openshiina::player {

namespace {

constexpr uint32_t Align(uint32_t v, uint32_t a) { return (v + a - 1) & ~(a - 1); }

constexpr std::array kAttributes = {
    DkVtxAttribState{0, 0, 0, DkVtxAttribSize_2x32, DkVtxAttribType_Float, 0},    // x, y
    DkVtxAttribState{0, 0, 8, DkVtxAttribSize_2x32, DkVtxAttribType_Float, 0},    // u, v
    DkVtxAttribState{0, 0, 16, DkVtxAttribSize_4x8, DkVtxAttribType_Unorm, 0},    // colour
    DkVtxAttribState{0, 0, 20, DkVtxAttribSize_4x32, DkVtxAttribType_Float, 0},   // the shape: local x, y; half w, h
    DkVtxAttribState{0, 0, 36, DkVtxAttribSize_2x32, DkVtxAttribType_Float, 0},   // radius, kind
};

// the samplers: 0 nearest, 1 linear
constexpr int SamplerNearest = 0, SamplerLinear = 1, Samplers = 2;
// (the game's two picture textures take 7.4 MB at 1280 x 720, the glyph atlas 1 MB, the logos 0.5 MB)
constexpr uint32_t TextureBytes = 24u << 20, CodeBytes = 64u << 10;

} // namespace

Renderer::Renderer()
{
    m_device = dk::DeviceMaker{}.create();
    m_queue = dk::QueueMaker{m_device}.setFlags(DkQueueFlags_Graphics).create();

    // The framebuffers and the swapchain
    dk::ImageLayout layout;
    dk::ImageLayoutMaker{m_device}
        .setFlags(DkImageFlags_UsageRender | DkImageFlags_UsagePresent | DkImageFlags_HwCompression)
        .setFormat(DkImageFormat_RGBA8_Unorm)
        .setDimensions(Width, Height)
        .initialize(layout);
    uint32_t size = Align(uint32_t(layout.getSize()), layout.getAlignment());
    m_framebufferMem = dk::MemBlockMaker{m_device, Align(size * Frames, DK_MEMBLOCK_ALIGNMENT)}
                           .setFlags(DkMemBlockFlags_GpuCached | DkMemBlockFlags_Image)
                           .create();
    std::array<DkImage const*, Frames> images{};
    for (int i = 0; i < Frames; i++)
    {
        m_framebuffers[size_t(i)].initialize(layout, m_framebufferMem, uint32_t(i) * size);
        images[size_t(i)] = &m_framebuffers[size_t(i)];
    }
    m_swapchain = dk::SwapchainMaker{m_device, nwindowGetDefault(), images}.create();

    // The textures' memory, written by the CPU and read as it is by the GPU
    m_textureMem = dk::MemBlockMaker{m_device, TextureBytes}
                       .setFlags(DkMemBlockFlags_CpuUncached | DkMemBlockFlags_GpuCached | DkMemBlockFlags_Image)
                       .create();

    // The rest: descriptors, uniforms, and a part a frame of vertices and of commands
    uint32_t at = 0;
    m_imageDescriptors = at;
    at = Align(at + MaxTextures * sizeof(DkImageDescriptor), DK_SAMPLER_DESCRIPTOR_ALIGNMENT);
    m_samplerDescriptors = at;
    at = Align(at + Samplers * sizeof(DkSamplerDescriptor), DK_UNIFORM_BUF_ALIGNMENT);
    m_uniforms = at;
    at += 2 * DK_UNIFORM_BUF_ALIGNMENT;
    for (int i = 0; i < Frames; i++)
    {
        m_vertexBase[size_t(i)] = at;
        at += VertexBytes;
        m_commandBase[size_t(i)] = at;
        at += CommandBytes;
    }
    m_dataMem = dk::MemBlockMaker{m_device, Align(at, DK_MEMBLOCK_ALIGNMENT)}
                    .setFlags(DkMemBlockFlags_CpuUncached | DkMemBlockFlags_GpuCached)
                    .create();
    auto* data = static_cast<uint8_t*>(m_dataMem.getCpuAddr());
    for (int s = 0; s < Samplers; s++)
    {
        dk::Sampler sampler;
        DkFilter filter = s == SamplerNearest ? DkFilter_Nearest : DkFilter_Linear;
        sampler.setFilter(filter, filter);
        sampler.setWrapMode(DkWrapMode_ClampToEdge, DkWrapMode_ClampToEdge, DkWrapMode_ClampToEdge);
        dk::SamplerDescriptor descriptor;
        descriptor.initialize(sampler);
        std::memcpy(data + m_samplerDescriptors + size_t(s) * sizeof(DkSamplerDescriptor), &descriptor, sizeof(DkSamplerDescriptor));
    }

    // The shaders
    m_codeMem = dk::MemBlockMaker{m_device, Align(CodeBytes + DK_SHADER_CODE_UNUSABLE_SIZE, DK_MEMBLOCK_ALIGNMENT)}
                    .setFlags(DkMemBlockFlags_CpuUncached | DkMemBlockFlags_GpuCached | DkMemBlockFlags_Code)
                    .create();
    LoadShader(m_vertexShader, "romfs:/shaders/ui_vsh.dksh");
    LoadShader(m_uiShader, "romfs:/shaders/ui_fsh.dksh");
    LoadShader(m_pictureShader, "romfs:/shaders/picture_fsh.dksh");

    m_cmd = dk::CmdBufMaker{m_device}.create();

    // Slot 0: one white pixel, for shapes (and what is drawn with no texture)
    Texture white = CreateTexture(1, 1);
    std::memset(white.Pixels, 0xFF, 4);
}

Renderer::~Renderer()
{
    m_queue.waitIdle();
}

void Renderer::LoadShader(dk::Shader& shader, const char* path)
{
    std::FILE* f = std::fopen(path, "rb");
    if (!f)
        throw std::runtime_error(std::string("No shader ") + path);
    std::fseek(f, 0, SEEK_END);
    long length = std::ftell(f);
    std::fseek(f, 0, SEEK_SET);
    uint32_t at = Align(m_codeUsed, DK_SHADER_CODE_ALIGNMENT);
    if (length <= 0 || at + uint32_t(length) > CodeBytes)
    {
        std::fclose(f);
        throw std::runtime_error(std::string("Shader too large: ") + path);
    }
    size_t read = std::fread(static_cast<uint8_t*>(m_codeMem.getCpuAddr()) + at, 1, size_t(length), f);
    std::fclose(f);
    if (read != size_t(length))
        throw std::runtime_error(std::string("Cannot read ") + path);
    dk::ShaderMaker{m_codeMem, at}.initialize(shader);
    m_codeUsed = at + uint32_t(length);
}

uint32_t Renderer::Allocate(uint32_t size, uint32_t alignment)
{
    uint32_t at = Align(m_textureUsed, alignment);
    if (at + size > TextureBytes)
        throw std::runtime_error("Out of texture memory");
    m_textureUsed = at + size;
    return at;
}

Texture Renderer::CreateTexture(int width, int height, bool coverage)
{
    if (m_nextSlot >= MaxTextures)
        throw std::runtime_error("Too many textures");
    uint32_t bytes = coverage ? 1 : 4;
    uint32_t stride = Align(uint32_t(width) * bytes, DK_IMAGE_LINEAR_STRIDE_ALIGNMENT);
    dk::ImageLayout layout;
    dk::ImageLayoutMaker{m_device}
        .setFlags(DkImageFlags_PitchLinear)
        .setFormat(coverage ? DkImageFormat_R8_Unorm : DkImageFormat_RGBA8_Unorm)
        .setDimensions(uint32_t(width), uint32_t(height))
        .setPitchStride(stride)
        .initialize(layout);
    uint32_t at = Allocate(uint32_t(layout.getSize()), layout.getAlignment());
    int slot = m_nextSlot++;
    dk::Image& image = m_images[size_t(slot)];
    image.initialize(layout, m_textureMem, at);
    dk::ImageView view{image};
    dk::ImageDescriptor descriptor;
    descriptor.initialize(view);
    auto* data = static_cast<uint8_t*>(m_dataMem.getCpuAddr());
    std::memcpy(data + m_imageDescriptors + size_t(slot) * sizeof(DkImageDescriptor), &descriptor, sizeof(DkImageDescriptor));
    Texture t;
    t.Slot = slot;
    t.Width = width;
    t.Height = height;
    t.Stride = stride;
    t.Pixels = static_cast<uint8_t*>(m_textureMem.getCpuAddr()) + at;
    std::memset(t.Pixels, 0, size_t(stride) * size_t(height));
    return t;
}

Texture Renderer::LoadImage(const std::string& path)
{
    std::FILE* f = std::fopen(path.c_str(), "rb");
    if (!f)
        return {};
    uint32_t size[2] = {};
    Texture t;
    if (std::fread(size, 4, 2, f) == 2 && size[0] > 0 && size[1] > 0 && size[0] <= 4096 && size[1] <= 4096)
    {
        t = CreateTexture(int(size[0]), int(size[1]));
        for (uint32_t y = 0; y < size[1]; y++)
            if (std::fread(t.Pixels + size_t(y) * t.Stride, 4, size[0], f) != size[0])
                break;
    }
    std::fclose(f);
    return t;
}

Texture& Renderer::PictureTexture(int width, int height)
{
    Texture& t = m_pictures[size_t(m_frame % Frames)];
    if (!t.Valid() || t.Width != width || t.Height != height)
        t = CreateTexture(width, height);
    return t;
}

void Renderer::Begin(uint32_t clear)
{
    int f = m_frame % Frames;
    m_fences[size_t(f)].wait();
    m_cmd.clear();
    m_cmd.addMemory(m_dataMem, m_commandBase[size_t(f)], CommandBytes);
    m_vertexUsed = 0;
    m_batch.clear();
    m_batchTexture = -1;

    m_framebuffer = m_queue.acquireImage(m_swapchain);
    dk::ImageView target{m_framebuffers[size_t(m_framebuffer)]};
    m_cmd.bindRenderTargets(&target);
    m_cmd.setViewports(0, {{0.0f, 0.0f, float(Width), float(Height), 0.0f, 1.0f}});
    m_cmd.setScissors(0, {{0, 0, uint32_t(Width), uint32_t(Height)}});
    m_cmd.clearColor(0, DkColorMask_RGBA, float(clear & 0xFF) / 255.0f, float((clear >> 8) & 0xFF) / 255.0f, float((clear >> 16) & 0xFF) / 255.0f,
                     float(clear >> 24) / 255.0f);
    // what the CPU wrote since (textures, descriptors) seen by the GPU
    m_cmd.barrier(DkBarrier_None, DkInvalidateFlags_Image | DkInvalidateFlags_Descriptors | DkInvalidateFlags_L2Cache);

    DkGpuAddr base = m_dataMem.getGpuAddr();
    m_cmd.bindImageDescriptorSet(base + m_imageDescriptors, MaxTextures);
    m_cmd.bindSamplerDescriptorSet(base + m_samplerDescriptors, Samplers);
    m_cmd.bindRasterizerState(dk::RasterizerState{}.setCullMode(DkFace_None));
    m_cmd.bindColorState(dk::ColorState{}.setBlendEnable(0, true));
    m_cmd.bindColorWriteState(dk::ColorWriteState{});
    m_cmd.bindBlendStates(0, dk::BlendState{}.setFactors(DkBlendFactor_SrcAlpha, DkBlendFactor_InvSrcAlpha, DkBlendFactor_One, DkBlendFactor_InvSrcAlpha));
    m_cmd.bindDepthStencilState(dk::DepthStencilState{}.setDepthTestEnable(false).setDepthWriteEnable(false));
    m_cmd.bindVtxAttribState(kAttributes);
    m_cmd.bindVtxBufferState({DkVtxBufferState{sizeof(Vertex), 0}});

    // the screen's scale (pixels to -1..1), for the vertex shader
    float screen[4] = {2.0f / Width, 2.0f / Height, 0, 0};
    m_cmd.bindUniformBuffer(DkStage_Vertex, 0, base + m_uniforms, DK_UNIFORM_BUF_ALIGNMENT);
    m_cmd.pushConstants(base + m_uniforms, DK_UNIFORM_BUF_ALIGNMENT, 0, sizeof screen, screen);
    m_cmd.bindUniformBuffer(DkStage_Fragment, 0, base + m_uniforms + DK_UNIFORM_BUF_ALIGNMENT, DK_UNIFORM_BUF_ALIGNMENT);
    m_cmd.bindShaders(DkStageFlag_GraphicsMask, {&m_vertexShader, &m_uiShader});
}

void Renderer::End()
{
    Flush();
    int f = m_frame % Frames;
    m_queue.submitCommands(m_cmd.finishList());
    m_queue.signalFence(m_fences[size_t(f)]);
    m_queue.presentImage(m_swapchain, m_framebuffer);
    m_frame++;
}

void Renderer::Flush()
{
    if (m_batch.empty())
        return;
    uint32_t bytes = uint32_t(m_batch.size() * sizeof(Vertex));
    if (m_vertexUsed + bytes <= VertexBytes)
    {
        uint32_t at = m_vertexBase[size_t(m_frame % Frames)] + m_vertexUsed;
        std::memcpy(static_cast<uint8_t*>(m_dataMem.getCpuAddr()) + at, m_batch.data(), bytes);
        m_cmd.bindVtxBuffer(0, m_dataMem.getGpuAddr() + at, bytes);
        m_cmd.bindTextures(DkStage_Fragment, 0, dkMakeTextureHandle(uint32_t(std::max(m_batchTexture, 0)), SamplerLinear));
        m_cmd.draw(DkPrimitive_Triangles, uint32_t(m_batch.size()), 1, 0, 0);
        m_vertexUsed += bytes;
    }
    m_batch.clear();
}

void Renderer::Quad(int textureSlot, const Rect& d, float u0, float v0, float u1, float v1, uint32_t color, Kind kind, float radius)
{
    // shapes take any texture: they join the batch as it is
    if (kind != Shape && textureSlot != m_batchTexture)
    {
        Flush();
        m_batchTexture = textureSlot;
    }
    float hw = d.W / 2, hh = d.H / 2, k = float(kind);
    Vertex a{d.X, d.Y, u0, v0, color, -hw, -hh, hw, hh, radius, k};
    Vertex b{d.X + d.W, d.Y, u1, v0, color, hw, -hh, hw, hh, radius, k};
    Vertex c{d.X + d.W, d.Y + d.H, u1, v1, color, hw, hh, hw, hh, radius, k};
    Vertex e{d.X, d.Y + d.H, u0, v1, color, -hw, hh, hw, hh, radius, k};
    m_batch.insert(m_batch.end(), {a, b, c, a, c, e});
}

void Renderer::Image(const Texture& texture, const Rect& dst, uint32_t color)
{
    Image(texture, dst, Rect{0, 0, float(texture.Width), float(texture.Height)}, color);
}

void Renderer::Image(const Texture& texture, const Rect& dst, const Rect& src, uint32_t color)
{
    if (!texture.Valid())
        return;
    float w = float(texture.Width), h = float(texture.Height);
    Quad(texture.Slot, dst, src.X / w, src.Y / h, (src.X + src.W) / w, (src.Y + src.H) / h, color, Textured, 0);
}

void Renderer::Glyph(const Texture& atlas, const Rect& dst, const Rect& src, uint32_t color)
{
    float w = float(atlas.Width), h = float(atlas.Height);
    Quad(atlas.Slot, dst, src.X / w, src.Y / h, (src.X + src.W) / w, (src.Y + src.H) / h, color, Coverage, 0);
}

void Renderer::Fill(const Rect& dst, uint32_t color, float radius)
{
    Quad(m_batchTexture, dst, 0, 0, 1, 1, color, Shape, std::min(radius, std::min(dst.W, dst.H) / 2));
}

Rect Renderer::Placement(Scaling scaling, int width, int height)
{
    // C# PictureScaling.Placement: whole numbers take the largest whole scale that fits (the fitting
    // one when not even x1 fits); the others fill the screen, proportions kept, centred
    if (width <= 0 || height <= 0)
        return {};
    float fit = std::min(float(Width) / float(width), float(Height) / float(height));
    float scale = fit;
    if (scaling == Scaling::WholeNumbers && fit >= 1)
        scale = std::floor(fit);
    float w = float(width) * scale, h = float(height) * scale;
    return {std::round((float(Width) - w) / 2), std::round((float(Height) - h) / 2), w, h};
}

void Renderer::Picture(const Texture& picture, const Rect& dst, Scaling scaling)
{
    if (!picture.Valid())
        return;
    Flush();
    // the fragment shader's uniforms: the picture's size, the size on the screen, the mode
    // (0 nearest, 1 bicubic, 2 sharp bilinear)
    float mode = scaling == Scaling::Smooth ? 1 : scaling == Scaling::Sharp ? 2 : 0;
    float uniforms[8] = {float(picture.Width), float(picture.Height), 1.0f / float(picture.Width), 1.0f / float(picture.Height), dst.W, dst.H, mode, 0};
    DkGpuAddr fragment = m_dataMem.getGpuAddr() + m_uniforms + DK_UNIFORM_BUF_ALIGNMENT;
    m_cmd.pushConstants(fragment, DK_UNIFORM_BUF_ALIGNMENT, 0, sizeof uniforms, uniforms);
    m_cmd.bindShaders(DkStageFlag_GraphicsMask, {&m_vertexShader, &m_pictureShader});
    m_batchTexture = picture.Slot;
    Quad(picture.Slot, dst, 0, 0, 1, 1, 0xFFFFFFFF, Textured, 0);
    Flush();
    m_cmd.bindShaders(DkStageFlag_GraphicsMask, {&m_vertexShader, &m_uiShader});
}

} // namespace openshiina::player
