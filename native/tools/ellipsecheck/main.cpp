// ellipsecheck: the C# tools/GdiEllipseCheck's fixed cases for the C++ DibShapes (the Switch's
// ellipses) against GDI itself (Windows): every size up to 96 x 96 and circles 97 to 1100 across with
// a brush and a 1-pixel pen of one grey (EFCLIB's circle wipe), and circles cut by the picture. It
// prints, per kind, how many cases differ from GDI and by how many pixels: the same counts as the C#
// tool's run with no random cases ("GdiEllipseCheck 0") when the port draws as the C# DibShapes.
#include <cstdio>
#include <map>
#include <string>
#include <vector>

#include "../scnrun/GdiShapes.h"
#include "platform/DibShapes.h"

using namespace openshiina;

namespace {

constexpr int Margin = 4, Background = 0x5A, Grey = 0x808080;

struct Totals
{
    int Cases = 0, Differ = 0;
    long long Pixels = 0;
};

std::vector<uint8_t> Draw(scripting::IScnShapes& shapes, int width, int height, int l, int t, int r, int b)
{
    std::vector<uint8_t> pixels(size_t(width) * size_t(height) * 4, uint8_t(Background));
    shapes.Ellipse(pixels.data(), width, height, width * 4, 4, l, t, r, b, Grey, scripting::ScnPen{0, 1, Grey});
    return pixels;
}

} // namespace

int main()
{
    platform::DibShapes dib;
    tools::GdiShapes gdi;
    std::map<std::string, Totals> totals;
    auto run = [&](const std::string& kind, int width, int height, int l, int t, int r, int b) {
        auto a = Draw(gdi, width, height, l, t, r, b), c = Draw(dib, width, height, l, t, r, b);
        int differ = 0;
        for (size_t p = 0; p < a.size(); p += 4)
            if (a[p] != c[p] || a[p + 1] != c[p + 1] || a[p + 2] != c[p + 2] || a[p + 3] != c[p + 3])
                differ++;
        auto& total = totals[kind];
        total.Cases++;
        total.Differ += differ > 0 ? 1 : 0;
        total.Pixels += differ;
    };
    for (int w = 1; w <= 96; w++)
        for (int h = 1; h <= 96; h++)
            run("one colour, every size to 96", w + 2 * Margin, h + 2 * Margin, Margin, Margin, Margin + w, Margin + h);
    for (int d = 97; d <= 1100; d++)
        run("one colour, circles 97 to 1100", d + 2 * Margin, d + 2 * Margin, Margin, Margin, Margin + d, Margin + d);
    // A circle cut by the picture, as the wipe's last ones are
    for (int d = 400; d <= 1100; d += 7)
        run("one colour, circles cut by the picture", 800, 600, 400 - d / 2, 300 - d / 2, 400 - d / 2 + d, 300 - d / 2 + d);
    std::printf("Cases that differ from GDI, and their pixels:\n");
    for (const auto& [kind, t] : totals)
        std::printf("  %s: %d of %d (%lld pixels)\n", kind.c_str(), t.Differ, t.Cases, t.Pixels);
    return 0;
}
