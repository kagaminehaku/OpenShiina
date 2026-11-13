// C# Scripting/ScnVm.Pictures.cs: picture slots (0xBCF2F0[0..256]) - S25 files kept in memory the
// way the executable keeps them: the file as it is, its frame and row offsets turned into pointers
// (FUN_00439DD0), the magic cleared, and the rows of "incremental" frames (flag 0x80000000)
// delta-decoded in place (FUN_00403430). Blank pictures (op_055A, FUN_004106F0) have the same
// shape: one row per line, "0x80000000 | (w * 4 + 6), w" and then the pixels. Drawing code and the
// embedded routines read pixels with FUN_00410520: row pointer [y] + 8 + x * bytes per pixel.
#include "scripting/ScnVm.h"

#include <algorithm>
#include <chrono>

namespace openshiina::scripting {

int ScnVm::LoadedFileSize(int address) const
{
    auto found = m_fileSizes.find(address);
    return found == m_fileSizes.end() ? -1 : found->second;
}

int ScnVm::PicturePixel(int picture, int frame, int x, int y, int bytesPerPixel)
{
    if (uint32_t(Read32(picture + 4)) < uint32_t(frame))
        return 0;
    int f = Read32(picture + 8 + 4 * frame);
    return Read32(f + 0x14 + 4 * y) + 8 + x * bytesPerPixel;
}

void ScnVm::ReleasePicture(int slot)
{
    // GlobalFree of the slot's picture: one an opcode made, or a block put there by 04B2
    bool owned = m_pictureOwned[size_t(slot)];
    m_pictureOwned[size_t(slot)] = false;
    if (owned)
        FreeScriptBlock(Picture(slot));
}

void ScnVm::FreeScriptBlock(int block)
{
    // GlobalFree of a block the scripts were given: its loaded-file size goes, and slots that show
    // it no longer own it (it is not freed twice)
    if (block == 0)
        return;
    m_fileSizes.erase(block);
    for (int s = 0; s < PictureSlots; s++)
        if (m_pictureOwned[size_t(s)] && Picture(s) == block)
            m_pictureOwned[size_t(s)] = false;
    Free(block);
}

void ScnVm::RelocatePicture(int picture)
{
    int frames = Read32(picture + 4);
    for (int i = 0; i < frames; i++)
    {
        int at = picture + 8 + 4 * i;
        int frame = Read32(at);
        if (frame == 0)
            continue;
        frame += picture;
        Write32(at, frame);
        int height = Read32(frame + 4);
        for (int y = 0; y < height; y++)
            Write32(frame + 0x14 + 4 * y, Read32(frame + 0x14 + 4 * y) + picture);
    }
}

void ScnVm::PredecodePicture(int picture)
{
    // In frames with flag 0x80000000 every row's runs of literal pixels (methods 2 and 4) are
    // delta-decoded in place - once per row reference, as the executable does it
    int frames = Read32(picture + 4);
    for (int i = 0; i < frames; i++)
    {
        int frame = Read32(picture + 8 + 4 * i);
        if (frame == 0 || (uint32_t(Read32(frame + 0x10)) & 0x80000000u) == 0)
            continue;
        int width = Read32(frame), height = Read32(frame + 4);
        for (int y = 0; y < height; y++)
        {
            int p = Read32(frame + 0x14 + 4 * y);
            if (p == 0 || width == 0)
                continue;
            p += 2;
            for (int x = width; x > 0;)
            {
                p = (p + 1) & ~1;
                int code = Read16(p);
                int count = code & 0x7FF, method = code >> 13;
                int data = p + ((code >> 11) & 3) + 2;
                if (count == 0)
                {
                    count = Read32(data);
                    data += 4;
                }
                switch (method)
                {
                case 2:
                    for (int k = 3; k < count * 3; k++)
                        WriteByte(data + k, uint8_t(ReadByte(data + k) + ReadByte(data + k - 3)));
                    p = data + std::max(3, count * 3);
                    break;
                case 3:
                    p = data + 3;
                    break;
                case 4:
                    for (int k = 4; k < count * 4; k++)
                        WriteByte(data + k, uint8_t(ReadByte(data + k) + ReadByte(data + k - 4)));
                    p = data + std::max(4, count * 4);
                    break;
                case 5:
                    p = data + 4;
                    break;
                default:
                    p = data;
                    break;
                }
                if (uint32_t(count) > uint32_t(x))
                    break;     // broken data: the executable would run past the row
                x -= count;
            }
        }
    }
}

int ScnVm::CreatePicture(int width, int height, int bytesPerPixel, int frames)
{
    int line = width * bytesPerPixel;
    int frameBytes = (line + 0xC) * height;
    int picture = Allocate((frameBytes + 0x18) * frames + 8);
    Write32(picture + 4, frames);
    uint32_t code = bytesPerPixel == 3 ? 0x40000000u : bytesPerPixel == 4 ? 0x80000000u : uint32_t(height);
    code |= uint32_t(line + 6);
    int frame = picture + frames * 4 + 8;
    for (int i = 0; i < frames; i++)
    {
        Write32(picture + 8 + 4 * i, frame);
        Write32(frame, width);
        Write32(frame + 4, height);
        int row = frame + 0x14 + height * 4;
        for (int y = 0; y < height; y++)
        {
            Write32(frame + 0x14 + 4 * y, row);
            Write32(row, int(code));
            Write32(row + 4, width);
            row += line + 8;
        }
        frame += frameBytes + 0x14;
    }
    return picture;
}

int ScnVm::FrameBytes(int picture, int frame)
{
    // 4 when its first row is ABGR (0x80000000), else 3
    if (uint32_t(frame) > uint32_t(Read32(picture + 4)))
        return 3;
    return Read32(Read32(Read32(picture + 8 + 4 * frame) + 0x14)) < 0 ? 4 : 3;
}

void ScnVm::FillPicture(int picture, int frame, int x, int y, int w, int h, int color)
{
    // 4 bytes a pixel get the colour dword; 3 bytes a pixel get its bytes 2, 3, 1 (as the
    // executable writes them)
    if (uint32_t(frame) > uint32_t(Read32(picture + 4)))
        return;
    int f = Read32(picture + 8 + 4 * frame);
    if (f == 0)
        return;
    int bytes = FrameBytes(picture, frame);
    int dst = Read32(f + 0x14 + 4 * y) + 8 + bytes * x;
    int line = bytes * w, stride = Read32(f) * bytes + 8;
    if (uint32_t(h) == 0)
        return;
    if (bytes == 4)
    {
        openshiina::Bytes row(size_t(uint32_t(line) >> 2) * 4);
        for (size_t k = 0; k < row.size(); k += 4)
            std::memcpy(&row[k], &color, 4);
        for (int r = 0; r < h; r++, dst += stride)
            WriteBytes(dst, row);
        return;
    }
    uint8_t b0 = uint8_t(color >> 16), b1 = uint8_t(color >> 24), b2 = uint8_t(color >> 8);
    for (int r = 0; r < h; r++, dst += stride)
        for (int k = 0; uint32_t(k) < uint32_t(line); k += 3)
        {
            WriteByte(dst + k, b0);
            WriteByte(dst + k + 1, b1);
            WriteByte(dst + k + 2, b2);
        }
}

void ScnVm::RegisterPictures()
{
    // 04B0 slot, file: load an S25 file into a picture slot
    Register(0x04B0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int slot = vm.Value(c, i.Args[0]);
        std::string file = vm.ReadString(vm.Value(c, i.Args[1]));
        if (uint32_t(slot) > uint32_t(vm.MaxPictureSlot()))
            return 2;
        if (vm.m_pictureOwned[size_t(slot)])
            vm.ReleasePicture(slot);
        openshiina::Bytes data;
        if (!vm.ReadScriptFile(file, data) || data.size() < 8 || LittleEndian::ToUInt32(data.data()) != 0x00353253)
        {
            vm.Write32(vm.PictureAddress(slot), 0);
            return 2;
        }
        int picture = vm.AllocateCopy(data);
        vm.EngineGlobals[0x4C4514] = int(data.size());
        vm.Write32(vm.PictureAddress(slot), picture);
        vm.m_pictureOwned[size_t(slot)] = true;
        vm.RelocatePicture(picture);
        vm.Write32(picture, 0);
        vm.PredecodePicture(picture);
        return 0;
    });
    // 055B slot, frame, x, y, w, h, colour: fill a rectangle of a raw picture
    Register(0x055B, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int v[7];
        for (int k = 0; k < 7; k++)
            v[k] = vm.Value(c, i.Args[size_t(k)]);
        int picture = vm.Picture(v[0]);
        if (picture != 0)
            vm.FillPicture(picture, v[1], v[2], v[3], v[4], v[5], v[6]);
        return 0;
    });
    // 055C slot, surface: a one-frame picture copied from a surface (FUN_00410B70)
    Register(0x055C, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int slot = vm.Value(c, i.Args[0]), surface = vm.Value(c, i.Args[1]);
        if (uint32_t(slot) > uint32_t(vm.MaxPictureSlot()) || surface < 0 || surface >= SurfaceCount)
            return 2;
        if (vm.m_pictureOwned[size_t(slot)])
            vm.ReleasePicture(slot);
        int w = vm.SurfaceField(surface, 7), h = vm.SurfaceField(surface, 8), pitch = vm.SurfaceField(surface, 10);
        int bytes = vm.SurfaceField(surface, 9) >> 3, pixels = vm.SurfaceField(surface, 2);
        int picture = vm.CreatePicture(w, h, bytes, 1);
        int dst = vm.Read32(vm.Read32(picture + 8) + 0x14) + 8;
        for (int y = 0; y < h; y++, dst += w * bytes + 8, pixels += pitch)
            vm.CopyMemory(dst, pixels, w * bytes);
        vm.Write32(vm.PictureAddress(slot), picture);
        vm.m_pictureOwned[size_t(slot)] = true;
        return 0;
    });
    // 0FD5 slot, frame, x, y, v: v = the address of pixel (x, y) (FUN_00410520)
    Register(0x0FD5, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int picture = vm.Picture(vm.Value(c, i.Args[0]));
        int frame = vm.Value(c, i.Args[1]), x = vm.Value(c, i.Args[2]), y = vm.Value(c, i.Args[3]);
        int address = picture == 0 ? 0 : vm.PicturePixel(picture, frame, x, y, vm.FrameBytes(picture, frame));
        vm.Store(c, i.Args[4], address);
        return 0;
    });
    // 0FD4 slot, frame, v: bytes of one row of the frame with its 8-byte header (FUN_00410590)
    Register(0x0FD4, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int picture = vm.Picture(vm.Value(c, i.Args[0]));
        int frame = vm.Value(c, i.Args[1]);
        int bytes = 8;
        if (picture != 0 && uint32_t(frame) <= uint32_t(vm.Read32(picture + 4)))
            if (int f = vm.Read32(picture + 8 + 4 * frame); f != 0)
                bytes = vm.Read32(f) * vm.FrameBytes(picture, frame) + 8;
        vm.Store(c, i.Args[2], bytes);
        return 0;
    });
    // 0560 slot, frame, x, y: the frame's offset; 0561 slot, frame, x, y: read it
    Register(0x0560, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int picture = vm.Picture(vm.Value(c, i.Args[0]));
        int frame = vm.Value(c, i.Args[1]), x = vm.Value(c, i.Args[2]), y = vm.Value(c, i.Args[3]);
        if (picture != 0 && uint32_t(frame) <= uint32_t(vm.Read32(picture + 4)))
            if (int f = vm.Read32(picture + 8 + 4 * frame); f != 0)
            {
                vm.Write32(f + 8, x);
                vm.Write32(f + 0xC, y);
            }
        return 0;
    });
    Register(0x0561, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int picture = vm.Picture(vm.Value(c, i.Args[0]));
        int frame = vm.Value(c, i.Args[1]);
        int x = 0, y = 0;
        if (picture != 0 && uint32_t(frame) <= uint32_t(vm.Read32(picture + 4)))
            if (int f = vm.Read32(picture + 8 + 4 * frame); f != 0)
            {
                x = vm.Read32(f + 8);
                y = vm.Read32(f + 0xC);
            }
        vm.Store(c, i.Args[2], x);
        vm.Store(c, i.Args[3], y);
        return 0;
    });
    // 00C8 file, address: copy the file into memory the scripts allocated
    Register(0x00C8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string file = vm.ReadString(vm.Value(c, i.Args[0]));
        openshiina::Bytes data;
        if (!vm.ReadScriptFile(file, data))
            return 2;
        vm.EngineGlobals[0x4C4514] = int(data.size());
        vm.WriteBytes(vm.Value(c, i.Args[1]), data);
        return 0;
    });
    // 04B1 slot: free the slot's picture
    Register(0x04B1, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int slot = vm.Value(c, i.Args[0]);
        if (uint32_t(slot) > uint32_t(vm.MaxPictureSlot()))
            return 2;
        if (vm.Picture(slot) != 0)
        {
            vm.ReleasePicture(slot);
            vm.Write32(vm.PictureAddress(slot), 0);
        }
        return 0;
    });
    // 00C9 file, v: v = the file loaded into memory
    Register(0x00C9, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string file = vm.ReadString(vm.Value(c, i.Args[0]));
        openshiina::Bytes data;
        auto started = std::chrono::steady_clock::now();
        if (!vm.ReadScriptFile(file, data))
            return 2;
        // With RoutineTimes: a file that took long to read, by name ("read: <file>")
        double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - started).count();
        if (vm.RoutineTimes && ms >= 50)
        {
            auto& time = (*vm.RoutineTimes)["read: " + file];
            time.Milliseconds += ms;
            time.Calls++;
        }
        vm.EngineGlobals[0x4C4514] = int(data.size());
        int address = vm.AllocateCopy(data);
        vm.m_fileSizes[address] = int(data.size());
        vm.Store(c, i.Args[1], address);
        return 0;
    });
    // 04B2 slot, address: a picture already in memory (00C9) becomes the slot's
    Register(0x04B2, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int slot = vm.Value(c, i.Args[0]);
        if (uint32_t(slot) > uint32_t(vm.MaxPictureSlot()))
            return 2;
        if (vm.m_pictureOwned[size_t(slot)])
            vm.ReleasePicture(slot);
        int picture = vm.Value(c, i.Args[1]);
        vm.Write32(vm.PictureAddress(slot), picture);
        vm.RelocatePicture(picture);
        // A heap block becomes the slot's: 04B1 frees it. Not one of VirtualAlloc, which the
        // scripts free themselves
        vm.m_pictureOwned[size_t(slot)] = vm.IsBlock(picture) && !vm.m_virtualBlocks.count(picture);
        return 0;
    });
    // 055A slot, width, height, bytes per pixel, frames: a blank picture
    Register(0x055A, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int slot = vm.Value(c, i.Args[0]), width = vm.Value(c, i.Args[1]), height = vm.Value(c, i.Args[2]);
        int bpp = vm.Value(c, i.Args[3]), frames = vm.Value(c, i.Args[4]);
        if (uint32_t(slot) > uint32_t(vm.MaxPictureSlot()))
            return 2;
        if (vm.m_pictureOwned[size_t(slot)])
            vm.ReleasePicture(slot);
        vm.Write32(vm.PictureAddress(slot), vm.CreatePicture(width, height, bpp, frames));
        vm.m_pictureOwned[size_t(slot)] = true;
        return 0;
    });
    // 04CE n: surface n black (the screen's size, 0x487F40); surface 0 is shown again
    Register(0x04CE, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]);
        if (n < 0 || n >= SurfaceCount || vm.SurfaceField(n, 2) == 0)
            return 0;
        vm.FillMemory(vm.SurfaceField(n, 2), vm.ScreenWidth * vm.ScreenHeight * (vm.Bpp >> 3), 0);
        if (n == 0)
            vm.FrameShown = true;
        return 0;
    });
}

} // namespace openshiina::scripting
