// C# Platform/DibShapes.cs: IScnShapes without Windows, GDI's Ellipse as Windows draws it (see
// DibShapes.cpp). The Switch's shapes; on Windows the tools draw with GDI itself.
#pragma once

#include "scripting/ScnTypes.h"

namespace openshiina::platform {

class DibShapes : public scripting::IScnShapes
{
public:
    void Ellipse(uint8_t* pixels, int width, int height, int stride, int bytesPerPixel, int l, int t, int r, int b, std::optional<int> brush,
                 std::optional<scripting::ScnPen> pen) override;
};

} // namespace openshiina::platform
