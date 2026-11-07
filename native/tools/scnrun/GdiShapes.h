// C# Platform/GdiShapes.cs: IScnShapes with Windows GDI, as ScnBoot draws (for scnrun, so that its
// pictures can be compared with ScnBoot's, and for ellipsecheck).
#pragma once

#include "scripting/ScnTypes.h"

namespace openshiina::tools {

class GdiShapes : public scripting::IScnShapes
{
public:
    void Ellipse(uint8_t* pixels, int width, int height, int stride, int bytesPerPixel, int l, int t, int r, int b, std::optional<int> brush,
                 std::optional<scripting::ScnPen> pen) override;
};

} // namespace openshiina::tools
