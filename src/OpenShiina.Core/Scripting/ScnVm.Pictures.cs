// Picture slots (0xBCF2F0[0..256]): S25 files kept in memory the way the executable keeps them -
// the file as it is, its frame and row offsets turned into pointers (FUN_00439DD0), the magic
// cleared, and the rows of "incremental" frames (flag 0x80000000) delta-decoded in place
// (FUN_00403430). Blank pictures (op_055A, FUN_004106F0) have the same shape: one row per line,
// "0x80000000 | (w * 4 + 6), w" and then the pixels. Drawing code and the embedded routines read
// pixels with FUN_00410520: row pointer [y] + 8 + x * bytes per pixel.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>Room for the picture slots of every version: 0-0x100 up to v2.49, 0-0x400 in v2.50.</summary>
    public const int PictureSlots = 0x401;

    /// <summary>The last picture slot the engine takes (0x100, v2.50's 0x400: REMPLUS 0x42B29A).</summary>
    private int MaxPictureSlot => EngineVersion >= 250 ? 0x400 : 0x100;
    private const int PictureRegion = 0x09300000;
    // 0xFB3BBC: the slot owns its memory (loaded by 04B0 / made by 055A), so it is freed when replaced
    private readonly bool[] m_pictureOwned = new bool[PictureSlots];

    // Files loaded into memory by op_00C9: address -> size (GlobalSize of the block)
    private readonly Dictionary<int, int> m_fileSizes = new();

    /// <summary>The size of a file op_00C9 loaded at <paramref name="address"/>, or -1.</summary>
    public int LoadedFileSize(int address) => m_fileSizes.GetValueOrDefault(address, -1);

    public int PictureAddress(int slot) => PictureRegion + 4 * slot;

    /// <summary>The picture in a slot (0 when empty).</summary>
    public int Picture(int slot) => slot is >= 0 and < PictureSlots ? Read32(PictureAddress(slot)) : 0;

    /// <summary>FUN_00410520: the address of pixel (x, y) of a picture's frame.</summary>
    public int PicturePixel(int picture, int frame, int x, int y, int bytesPerPixel)
    {
        if ((uint)Read32(picture + 4) < (uint)frame)
            return 0;
        int f = Read32(picture + 8 + 4 * frame);
        return Read32(f + 0x14 + 4 * y) + 8 + x * bytesPerPixel;
    }

    private void ReleasePicture(int slot)
    {
        // GlobalFree of the slot's picture: one an opcode made, or a block put there by 04B2
        // (START loads pictures with its cached loader, 04B2s them and lets 04B1 free them)
        bool owned = m_pictureOwned[slot];
        m_pictureOwned[slot] = false;
        if (owned)
            FreeScriptBlock(Picture(slot));
    }

    /// <summary>
    /// GlobalFree of a block the scripts were given: its loaded-file size goes, and slots that
    /// show it no longer own it (it is not freed twice).
    /// </summary>
    private void FreeScriptBlock(int block)
    {
        if (block == 0)
            return;
        m_fileSizes.Remove(block);
        for (int s = 0; s < PictureSlots; s++)
            if (m_pictureOwned[s] && Picture(s) == block)
                m_pictureOwned[s] = false;
        Free(block);
    }

    /// <summary>FUN_00439DD0: frame offsets and row offsets of a picture in memory become pointers.</summary>
    private void RelocatePicture(int picture)
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

    /// <summary>
    /// FUN_00403430: in frames with flag 0x80000000 every row's runs of literal pixels (methods 2
    /// and 4) are delta-decoded in place - once per row reference, as the executable does it.
    /// </summary>
    private void PredecodePicture(int picture)
    {
        int frames = Read32(picture + 4);
        for (int i = 0; i < frames; i++)
        {
            int frame = Read32(picture + 8 + 4 * i);
            if (frame == 0 || (Read32(frame + 0x10) & 0x80000000) == 0)
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
                    p = p + 1 & ~1;
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
                                WriteByte(data + k, (byte)(ReadByte(data + k) + ReadByte(data + k - 3)));
                            p = data + Math.Max(3, count * 3);
                            break;
                        case 3:
                            p = data + 3;
                            break;
                        case 4:
                            for (int k = 4; k < count * 4; k++)
                                WriteByte(data + k, (byte)(ReadByte(data + k) + ReadByte(data + k - 4)));
                            p = data + Math.Max(4, count * 4);
                            break;
                        case 5:
                            p = data + 4;
                            break;
                        default:
                            p = data;
                            break;
                    }
                    if ((uint)count > (uint)x)
                        break;     // broken data: the executable would run past the row
                    x -= count;
                }
            }
        }
    }

    /// <summary>FUN_004106F0: a blank picture of frames frames, width x height, 3 or 4 bytes a pixel.</summary>
    public int CreatePicture(int width, int height, int bytesPerPixel, int frames)
    {
        int line = width * bytesPerPixel;
        int frameBytes = (line + 0xC) * height;
        int picture = Allocate((frameBytes + 0x18) * frames + 8);
        Write32(picture + 4, frames);
        uint code = bytesPerPixel switch { 3 => 0x40000000u, 4 => 0x80000000u, _ => (uint)height };
        code |= (uint)(line + 6);
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
                Write32(row, (int)code);
                Write32(row + 4, width);
                row += line + 8;
            }
            frame += frameBytes + 0x14;
        }
        return picture;
    }

    /// <summary>Bytes per pixel of a picture's frame: 4 when its first row is ABGR (0x80000000), else 3.</summary>
    private int FrameBytes(int picture, int frame) =>
        (uint)frame > (uint)Read32(picture + 4) ? 3 : (Read32(Read32(Read32(picture + 8 + 4 * frame) + 0x14)) < 0 ? 4 : 3);

    /// <summary>
    /// FUN_004105F0: fills a rectangle of a raw picture's frame. 4 bytes a pixel get the colour
    /// dword; 3 bytes a pixel get its bytes 2, 3, 1 (as the executable writes them).
    /// </summary>
    public void FillPicture(int picture, int frame, int x, int y, int w, int h, int color)
    {
        if ((uint)frame > (uint)Read32(picture + 4))
            return;
        int f = Read32(picture + 8 + 4 * frame);
        if (f == 0)
            return;
        int bytes = FrameBytes(picture, frame);
        int dst = Read32(f + 0x14 + 4 * y) + 8 + bytes * x;
        int line = bytes * w, stride = Read32(f) * bytes + 8;
        if ((uint)h == 0)
            return;
        if (bytes == 4)
        {
            var row = new byte[(line >>> 2) * 4];
            for (int k = 0; k < row.Length; k += 4)
                BitConverter.TryWriteBytes(row.AsSpan(k), color);
            for (int r = 0; r < h; r++, dst += stride)
                WriteBytes(dst, row);
            return;
        }
        byte b0 = (byte)(color >> 16), b1 = (byte)(color >> 24), b2 = (byte)(color >> 8);
        for (int r = 0; r < h; r++, dst += stride)
            for (int k = 0; (uint)k < (uint)line; k += 3)
            {
                WriteByte(dst + k, b0);
                WriteByte(dst + k + 1, b1);
                WriteByte(dst + k + 2, b2);
            }
    }

    private void RegisterPictures()
    {
        // 04B0 slot, file: load an S25 file into a picture slot
        Register(0x04B0, (vm, c, i) =>
        {
            int slot = vm.Value(c, i.Args[0]);
            string file = vm.ReadString(vm.Value(c, i.Args[1]));
            if ((uint)slot > (uint)vm.MaxPictureSlot)
                return 2;
            if (vm.m_pictureOwned[slot])
                vm.ReleasePicture(slot);
            byte[]? data = vm.ReadScriptFile(file);
            if (data == null || data.Length < 8 || BitConverter.ToUInt32(data, 0) != 0x00353253)
            {
                vm.Write32(vm.PictureAddress(slot), 0);
                return 2;
            }
            int picture = vm.AllocateCopy(data);
            vm.EngineGlobals[0x4C4514] = data.Length;
            vm.Write32(vm.PictureAddress(slot), picture);
            vm.m_pictureOwned[slot] = true;
            vm.RelocatePicture(picture);
            vm.Write32(picture, 0);
            vm.PredecodePicture(picture);
            return 0;
        });
        // 055B slot, frame, x, y, w, h, colour: fill a rectangle of a raw picture
        Register(0x055B, (vm, c, i) =>
        {
            var v = new int[7];
            for (int k = 0; k < 7; k++)
                v[k] = vm.Value(c, i.Args[k]);
            int picture = vm.Picture(v[0]);
            if (picture != 0)
                vm.FillPicture(picture, v[1], v[2], v[3], v[4], v[5], v[6]);
            return 0;
        });
        // 055C slot, surface: a one-frame picture copied from a surface (FUN_00410B70)
        Register(0x055C, (vm, c, i) =>
        {
            int slot = vm.Value(c, i.Args[0]), surface = vm.Value(c, i.Args[1]);
            if ((uint)slot > (uint)vm.MaxPictureSlot || surface is < 0 or >= SurfaceCount)
                return 2;
            if (vm.m_pictureOwned[slot])
                vm.ReleasePicture(slot);
            int w = vm.SurfaceField(surface, 7), h = vm.SurfaceField(surface, 8), pitch = vm.SurfaceField(surface, 10);
            int bytes = vm.SurfaceField(surface, 9) >> 3, pixels = vm.SurfaceField(surface, 2);
            int picture = vm.CreatePicture(w, h, bytes, 1);
            int dst = vm.Read32(vm.Read32(picture + 8) + 0x14) + 8;
            for (int y = 0; y < h; y++, dst += w * bytes + 8, pixels += pitch)
                vm.CopyMemory(dst, pixels, w * bytes);
            vm.Write32(vm.PictureAddress(slot), picture);
            vm.m_pictureOwned[slot] = true;
            return 0;
        });
        // 0FD5 slot, frame, x, y, v: v = the address of pixel (x, y) (FUN_00410520)
        Register(0x0FD5, (vm, c, i) =>
        {
            int picture = vm.Picture(vm.Value(c, i.Args[0]));
            int frame = vm.Value(c, i.Args[1]), x = vm.Value(c, i.Args[2]), y = vm.Value(c, i.Args[3]);
            int address = picture == 0 ? 0 : vm.PicturePixel(picture, frame, x, y, vm.FrameBytes(picture, frame));
            vm.Store(c, i.Args[4], address);
            return 0;
        });
        // 0FD4 slot, frame, v: bytes of one row of the frame with its 8-byte header (FUN_00410590)
        Register(0x0FD4, (vm, c, i) =>
        {
            int picture = vm.Picture(vm.Value(c, i.Args[0]));
            int frame = vm.Value(c, i.Args[1]);
            int bytes = 8;
            if (picture != 0 && (uint)frame <= (uint)vm.Read32(picture + 4) && vm.Read32(picture + 8 + 4 * frame) is var f and not 0)
                bytes = vm.Read32(f) * vm.FrameBytes(picture, frame) + 8;
            vm.Store(c, i.Args[2], bytes);
            return 0;
        });
        // 0560 slot, frame, x, y: the frame's offset; 0561 slot, frame, x, y: read it
        Register(0x0560, (vm, c, i) =>
        {
            int picture = vm.Picture(vm.Value(c, i.Args[0]));
            int frame = vm.Value(c, i.Args[1]), x = vm.Value(c, i.Args[2]), y = vm.Value(c, i.Args[3]);
            if (picture != 0 && (uint)frame <= (uint)vm.Read32(picture + 4) && vm.Read32(picture + 8 + 4 * frame) is var f and not 0)
            {
                vm.Write32(f + 8, x);
                vm.Write32(f + 0xC, y);
            }
            return 0;
        });
        Register(0x0561, (vm, c, i) =>
        {
            int picture = vm.Picture(vm.Value(c, i.Args[0]));
            int frame = vm.Value(c, i.Args[1]);
            int x = 0, y = 0;
            if (picture != 0 && (uint)frame <= (uint)vm.Read32(picture + 4) && vm.Read32(picture + 8 + 4 * frame) is var f and not 0)
            {
                x = vm.Read32(f + 8);
                y = vm.Read32(f + 0xC);
            }
            vm.Store(c, i.Args[2], x);
            vm.Store(c, i.Args[3], y);
            return 0;
        });
        // 00C8 file, address: copy the file into memory the scripts allocated
        Register(0x00C8, (vm, c, i) =>
        {
            string file = vm.ReadString(vm.Value(c, i.Args[0]));
            if (vm.ReadScriptFile(file) is not { } data)
                return 2;
            vm.EngineGlobals[0x4C4514] = data.Length;
            vm.WriteBytes(vm.Value(c, i.Args[1]), data);
            return 0;
        });
        // 04B1 slot: free the slot's picture
        Register(0x04B1, (vm, c, i) =>
        {
            int slot = vm.Value(c, i.Args[0]);
            if ((uint)slot > (uint)vm.MaxPictureSlot)
                return 2;
            if (vm.Picture(slot) != 0)
            {
                vm.ReleasePicture(slot);
                vm.Write32(vm.PictureAddress(slot), 0);
            }
            return 0;
        });
        // 00C9 file, v: v = the file loaded into memory
        Register(0x00C9, (vm, c, i) =>
        {
            string file = vm.ReadString(vm.Value(c, i.Args[0]));
            if (vm.ReadScriptFile(file) is not { } data)
                return 2;
            vm.EngineGlobals[0x4C4514] = data.Length;
            int address = vm.AllocateCopy(data);
            vm.m_fileSizes[address] = data.Length;
            vm.Store(c, i.Args[1], address);
            return 0;
        });
        // 04B2 slot, address: a picture already in memory (00C9) becomes the slot's
        Register(0x04B2, (vm, c, i) =>
        {
            int slot = vm.Value(c, i.Args[0]);
            if ((uint)slot > (uint)vm.MaxPictureSlot)
                return 2;
            if (vm.m_pictureOwned[slot])
                vm.ReleasePicture(slot);
            int picture = vm.Value(c, i.Args[1]);
            vm.Write32(vm.PictureAddress(slot), picture);
            vm.RelocatePicture(picture);
            // A heap block becomes the slot's: 04B1 frees it
            vm.m_pictureOwned[slot] = vm.IsBlock(picture);
            return 0;
        });
        // 055A slot, width, height, bytes per pixel, frames: a blank picture
        Register(0x055A, (vm, c, i) =>
        {
            int slot = vm.Value(c, i.Args[0]), width = vm.Value(c, i.Args[1]), height = vm.Value(c, i.Args[2]);
            int bpp = vm.Value(c, i.Args[3]), frames = vm.Value(c, i.Args[4]);
            if ((uint)slot > (uint)vm.MaxPictureSlot)
                return 2;
            if (vm.m_pictureOwned[slot])
                vm.ReleasePicture(slot);
            vm.Write32(vm.PictureAddress(slot), vm.CreatePicture(width, height, bpp, frames));
            vm.m_pictureOwned[slot] = true;
            return 0;
        });
        // 04CE n: surface n black (the screen's size, 0x487F40); surface 0 is shown again
        Register(0x04CE, (vm, c, i) =>
        {
            int n = vm.Value(c, i.Args[0]);
            if (n is < 0 or >= SurfaceCount || vm.SurfaceField(n, 2) == 0)
                return 0;
            vm.FillMemory(vm.SurfaceField(n, 2), vm.ScreenWidth * vm.ScreenHeight * (vm.Bpp >> 3), 0);
            if (n == 0)
            {
                vm.FrameShown = true;
            }
            return 0;
        });
    }
}
