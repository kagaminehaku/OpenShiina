# Compares the window pictures ScnBoot (scnboot <game> <frames> <folder> <every>) and scnrun
# (SCNRUN_PICTURES=<folder>) saved for the same frames, pixel for pixel.
#   python screencompare.py <ScnBoot folder> <scnrun folder>
import os
import struct
import sys
import zlib


def read_png(path):
    """The pixels of an 8-bit RGB or RGBA PNG as (width, height, channels, rows of bytes)."""
    with open(path, "rb") as f:
        data = f.read()
    pos, chunks, width = 8, b"", 0
    while pos < len(data):
        length, kind = struct.unpack(">I4s", data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            width, height, depth, colour = struct.unpack(">IIBB", body[:10])
            channels = {2: 3, 6: 4}[colour]
        elif kind == b"IDAT":
            chunks += body
        pos += 12 + length
    raw = zlib.decompress(chunks)
    stride = width * channels
    rows, previous = [], bytearray(stride)
    for y in range(height):
        start = y * (stride + 1)
        kind, row = raw[start], bytearray(raw[start + 1:start + 1 + stride])
        for i in range(stride):
            left = row[i - channels] if i >= channels else 0
            up = previous[i]
            corner = previous[i - channels] if i >= channels else 0
            if kind == 1:
                row[i] = (row[i] + left) & 255
            elif kind == 2:
                row[i] = (row[i] + up) & 255
            elif kind == 3:
                row[i] = (row[i] + (left + up) // 2) & 255
            elif kind == 4:
                p = left + up - corner
                pa, pb, pc = abs(p - left), abs(p - up), abs(p - corner)
                row[i] = (row[i] + (left if pa <= pb and pa <= pc else up if pb <= pc else corner)) & 255
        rows.append(bytes(row))
        previous = row
    return width, height, channels, rows


def main():
    expected_folder, actual_folder = sys.argv[1], sys.argv[2]
    names = sorted(n for n in os.listdir(expected_folder) if n.startswith("frame_") and n.endswith(".png") and "_s" not in n)
    same = differ = missing = 0
    for name in names:
        other = os.path.join(actual_folder, name)
        if not os.path.exists(other):
            missing += 1
            print(f"{name}: not saved by scnrun")
            continue
        w1, h1, c1, a = read_png(os.path.join(expected_folder, name))
        w2, h2, c2, b = read_png(other)
        if (w1, h1) != (w2, h2):
            differ += 1
            print(f"{name}: {w1}x{h1} against {w2}x{h2}")
            continue
        pixels, first = 0, None
        for y in range(h1):
            ra, rb = a[y], b[y]
            if ra == rb and c1 == c2:
                continue
            for x in range(w1):
                if ra[x * c1:x * c1 + 3] != rb[x * c2:x * c2 + 3]:
                    pixels += 1
                    first = first or (x, y)
        if pixels:
            differ += 1
            print(f"{name}: {pixels} pixels differ, the first at {first}")
        else:
            same += 1
    print(f"{same} frames the same, {differ} differ, {missing} missing")
    return 0 if differ == 0 and missing == 0 and same > 0 else 1


if __name__ == "__main__":
    sys.exit(main())
