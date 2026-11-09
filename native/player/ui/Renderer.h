// The Switch player's drawing, on deko3d (the homebrew low-level API of the Tegra X1; no OpenGL, no
// mesa). One 1280 x 720 swapchain of two framebuffers; textures are pitch-linear images in memory
// the CPU writes straight into (the game's picture each frame, the glyphs, the logo), read by the
// GPU as they are. The screens are drawn as quads in one vertex format: textured, a glyph's
// coverage, or a rounded rectangle worked out in the fragment shader; the game's picture has a
// shader of its own for the scalings of the C# player (Game/PictureScaling.cs): Sharp, whole
// numbers, Smooth, Nearest, and FSR 1 in two passes (EASU into a picture of the screen size, then
// RCAS to the screen). The shaders are compiled when the player is built (uam), into the romfs.
#pragma once

#include <deko3d.hpp>

#include <array>
#include <cstdint>
#include <string>
#include <vector>

namespace openshiina::player {

/// The C# player's setting "Scaling" (Game/PictureScaling.cs), without Anime4K yet.
enum class Scaling
{
    /// Whole game pixels as far as they fit, the rest bilinear: even and sharp (the default).
    Sharp,
    /// x2, x3... only (x1 when not even that fits): exact.
    WholeNumbers,
    /// Filtered (bicubic): soft.
    Smooth,
    /// Nearest-neighbour to the screen's size: uneven at scales that are not whole numbers.
    Nearest,
    /// AMD FidelityFX Super Resolution 1: edge-adaptive upscaling (EASU), then sharpening (RCAS).
    Fsr,
};

/// An image the CPU writes and the GPU reads: rows Stride bytes apart at Pixels, 4 bytes a pixel
/// (RGBA) or 1 (coverage).
struct Texture
{
    int Slot = -1;
    int Width = 0, Height = 0;
    uint32_t Stride = 0;
    uint8_t* Pixels = nullptr;
    /// For the caller: which version of its content the texture holds (the game's pictures).
    uint64_t Version = 0;
    bool Valid() const { return Slot >= 0; }
};

/// A rectangle on the screen, in pixels.
struct Rect
{
    float X = 0, Y = 0, W = 0, H = 0;
    bool Contains(float x, float y) const { return x >= X && y >= Y && x < X + W && y < Y + H; }
};

class Renderer
{
public:
    static constexpr int Width = 1280, Height = 720;

    Renderer();
    ~Renderer();
    Renderer(const Renderer&) = delete;
    Renderer& operator=(const Renderer&) = delete;

    /// A texture of width x height, RGBA (or one byte a pixel: a glyph's coverage); zeroed.
    Texture CreateTexture(int width, int height, bool coverage = false);
    /// A texture from the player's raw images (romfs ui/*.rgba: width, height, then RGBA rows).
    Texture LoadImage(const std::string& path);

    /// A frame begins: waits for its buffers, clears the screen.
    void Begin(uint32_t clear = 0xFF000000);
    /// The frame drawn so far goes to the screen (at the next vertical blank).
    void End();

    /// The texture of the game's picture for this frame (one of two, made the first time with the
    /// game's size): written by the caller between Begin and Picture.
    Texture& PictureTexture(int width, int height);
    /// The game's picture into dst with a scaling; fsrSharpness: FSR's sharpening, 1 (mild) to 10
    /// (AMD's strongest), 0 none (the C# player's setting "FSR sharpness").
    void Picture(const Texture& picture, const Rect& dst, Scaling scaling, int fsrSharpness = 8);
    /// Where a picture of width x height goes on the screen with a scaling (C# PictureScaling.Placement).
    static Rect Placement(Scaling scaling, int width, int height);

    /// A texture (or part of one, in its pixels) into dst, its colours times color (0xAABBGGRR).
    void Image(const Texture& texture, const Rect& dst, uint32_t color = 0xFFFFFFFF);
    void Image(const Texture& texture, const Rect& dst, const Rect& src, uint32_t color = 0xFFFFFFFF);
    /// A glyph from a coverage texture, in color.
    void Glyph(const Texture& atlas, const Rect& dst, const Rect& src, uint32_t color);
    /// A rectangle in color, its corners rounded by radius pixels.
    void Fill(const Rect& dst, uint32_t color, float radius = 0);

private:
    struct Vertex
    {
        float X, Y, U, V;
        uint32_t Color;
        float LocalX, LocalY, HalfW, HalfH, Radius, Kind;
    };
    enum Kind { Textured = 0, Coverage = 1, Shape = 2 };
    static constexpr int Frames = 2, MaxTextures = 256;
    static constexpr uint32_t VertexBytes = 2u << 20, CommandBytes = 512u << 10;

    void CreateSwapchain();
    /// FSR's picture of width x height (EASU draws into it, RCAS reads it), made again when the size changes.
    void FsrTarget(int width, int height);
    /// The vertex shader's screen: pixels of a target of width x height to -1..1.
    void ScreenSize(int width, int height);
    void LoadShader(dk::Shader& shader, const char* path);
    void Quad(int textureSlot, const Rect& dst, float u0, float v0, float u1, float v1, uint32_t color, Kind kind, float radius);
    void Flush();
    uint32_t Allocate(uint32_t size, uint32_t alignment);

    dk::UniqueDevice m_device;
    dk::UniqueQueue m_queue;
    dk::UniqueMemBlock m_framebufferMem, m_textureMem, m_dataMem, m_codeMem;
    dk::UniqueCmdBuf m_cmd;
    dk::UniqueSwapchain m_swapchain;
    std::array<dk::Image, Frames> m_framebuffers;
    std::array<dk::Fence, Frames> m_fences;
    dk::Shader m_vertexShader, m_uiShader, m_pictureShader, m_easuShader, m_rcasShader;
    // FSR's picture: its memory, the image, its size (its descriptor is the last slot)
    dk::UniqueMemBlock m_fsrMem;
    dk::Image m_fsrImage;
    int m_fsrWidth = 0, m_fsrHeight = 0;

    // m_dataMem: image descriptors, sampler descriptors, the uniforms, then a part a frame of
    // vertices and of command memory
    uint32_t m_imageDescriptors = 0, m_samplerDescriptors = 0, m_uniforms = 0;
    std::array<uint32_t, Frames> m_vertexBase{}, m_commandBase{};
    uint32_t m_textureUsed = 0, m_codeUsed = 0;
    int m_nextSlot = 0;
    std::vector<dk::Image> m_images = std::vector<dk::Image>(MaxTextures);

    // the frame being drawn
    int m_frame = 0, m_framebuffer = -1;
    uint32_t m_vertexUsed = 0;
    std::vector<Vertex> m_batch;
    int m_batchTexture = -1;
    std::array<Texture, Frames> m_pictures;
};

} // namespace openshiina::player
