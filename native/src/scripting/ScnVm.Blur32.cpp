// C# Scripting/ScnVm.Blur32.cs: Bitch Nee-chan's blur (START 9F5E6, SSE2; Bitch Gakuen's too): a tent
// filter on 32-bit pixels, along the rows (radius rx, from the source into the destination) and then
// down the columns of the destination (radius ry), written out from its x86 code. Each line runs
// three sums over its pixels (edges repeated): S1 the tent-weighted sum the pixel comes from, S2 and
// S3 the plain sums of the window's left half (with the centre) and right half, kept in a ring of
// 2r + 1 pixels; a pixel is (int)(float(S1) * (256f / ((r + 1)^2 * 255 + 1))), saturated to a byte.
// A radius of 0 copies the rows (or leaves the columns).
// The columns run all at once, a row of the picture a step (the sums of every column side by side),
// in place: a row is read into the sums before it is written.
#include "scripting/ScnVm.h"

#include <algorithm>
#include <climits>

namespace openshiina::scripting {

namespace {

/// A sum times the scale to a byte: truncated (cvttps2dq), saturated.
uint8_t BlurByte(float f)
{
    int value = f >= -2147483648.0f && f < 2147483648.0f ? int(f) : INT32_MIN;
    return uint8_t(std::clamp(value, 0, 255));
}

/// One line of n pixels (4 lanes each) through the tent filter of radius r.
void BlurLine(const std::vector<int>& line, int n, int r, uint8_t* output, std::vector<int>& ring)
{
    float scale = 256.0f / float((r + 1) * (r + 1) * 255 + 1);
    int size = 2 * r + 1;
    int s1[4] = {}, s2[4] = {}, s3[4] = {};
    auto at = [](const std::vector<int>& array, int pixel) { return &array[size_t(pixel) * 4]; };
    auto put = [](std::vector<int>& array, int pixel, const int* v) { std::copy(v, v + 4, &array[size_t(pixel) * 4]); };
    // The left half: the first pixel r + 1 times, weights 1 to r + 1
    const int* first = at(line, 0);
    for (int k = 1; k <= r + 1; k++)
    {
        put(ring, k - 1, first);
        for (int l = 0; l < 4; l++)
        {
            s2[l] += first[l];
            s1[l] += first[l] * k;
        }
    }
    // The right half: pixels 1 to r (the last repeated), weights r down to 1
    int last = 0;
    for (int j = 1; j <= r; j++)
    {
        if (j <= n - 1)
            last = j;
        const int* v = at(line, last);
        put(ring, r + j, v);
        for (int l = 0; l < 4; l++)
        {
            s3[l] += v[l];
            s1[l] += v[l] * (r + 1 - j);
        }
    }
    last = std::min(r, n - 1);
    int incoming[4];
    std::copy(at(line, last), at(line, last) + 4, incoming);
    int head = r;
    for (int x = 0; x < n; x++)
    {
        int leave = head - r;
        if (leave < 0)
            leave += size;
        head++;
        if (last < n - 1)
        {
            last++;
            std::copy(at(line, last), at(line, last) + 4, incoming);
        }
        if (head >= size)
            head = 0;
        // truncated, saturated (lanes are 0 to 255 * (r + 1)^2: in range)
        for (int l = 0; l < 4; l++)
            output[x * 4 + l] = BlurByte(float(s1[l]) * scale);
        int* leaving = &ring[size_t(leave) * 4];
        const int* t = &ring[size_t(head) * 4];
        for (int l = 0; l < 4; l++)
        {
            s1[l] -= s2[l];
            s2[l] -= leaving[l];
            leaving[l] = incoming[l];
            s3[l] += incoming[l];
            s1[l] += s3[l];
        }
        for (int l = 0; l < 4; l++)
        {
            s2[l] += t[l];
            s3[l] -= t[l];
        }
    }
}

} // namespace

bool ScnVm::Blur32(int dst, int dstStride, int w, int h, int src, int srcStride, int rx, int ry)
{
    if (w <= 0 || h <= 0 || rx < 0 || ry < 0)
        return false;
    if (rx == 0)
    {
        for (int y = 0; y < h; y++)
            CopyMemory(dst + y * dstStride, src + y * srcStride, w * 4);
    }
    else
    {
        // A row at a time: its pixels read before it is written (rows that share bytes with other
        // rows' destinations come out as one after the other makes them)
        std::vector<int> line(size_t(w) * 4), ring(size_t(2 * rx + 1) * 4);
        openshiina::Bytes output(size_t(w) * 4);
        for (int y = 0; y < h; y++)
        {
            const uint8_t* row = Bytes(src + y * srcStride, w * 4);
            for (size_t k = 0; k < line.size(); k++)
                line[k] = row[k];
            BlurLine(line, w, rx, output.data(), ring);
            std::copy(output.begin(), output.end(), Bytes(dst + y * dstStride, w * 4));
        }
    }
    if (ry != 0)
        BlurColumns(dst, dstStride, 0, w * 4, h, ry);
    return true;
}

void ScnVm::BlurColumns(int dst, int stride, int first, int end, int h, int r)
{
    int lanes = end - first, size = 2 * r + 1;
    dst += first;
    float scale = 256.0f / float((r + 1) * (r + 1) * 255 + 1);
    std::vector<int> s1(static_cast<size_t>(lanes)), s2(static_cast<size_t>(lanes)), s3(static_cast<size_t>(lanes)), incoming(static_cast<size_t>(lanes)), ring(size_t(size) * size_t(lanes));
    auto load = [&](int y) {
        const uint8_t* row = Bytes(dst + y * stride, lanes);
        for (int k = 0; k < lanes; k++)
            incoming[size_t(k)] = row[k];
    };

    // The left half: row 0 r + 1 times, weights 1 to r + 1
    load(0);
    for (int k = 1; k <= r + 1; k++)
    {
        std::copy(incoming.begin(), incoming.end(), ring.begin() + ptrdiff_t(k - 1) * lanes);
        for (int i = 0; i < lanes; i++)
        {
            s2[size_t(i)] += incoming[size_t(i)];
            s1[size_t(i)] += incoming[size_t(i)] * k;
        }
    }
    // The right half: rows 1 to r (the last repeated), weights r down to 1
    int last = 0;
    for (int j = 1; j <= r; j++)
    {
        if (j <= h - 1)
        {
            last = j;
            load(last);
        }
        std::copy(incoming.begin(), incoming.end(), ring.begin() + ptrdiff_t(r + j) * lanes);
        for (int i = 0; i < lanes; i++)
        {
            s3[size_t(i)] += incoming[size_t(i)];
            s1[size_t(i)] += incoming[size_t(i)] * (r + 1 - j);
        }
    }
    last = std::min(r, h - 1);
    load(last);
    int head = r;
    for (int y = 0; y < h; y++)
    {
        int leave = head - r;
        if (leave < 0)
            leave += size;
        head++;
        if (last < h - 1)
        {
            last++;
            load(last);
        }
        if (head >= size)
            head = 0;
        uint8_t* output = Bytes(dst + y * stride, lanes);
        size_t leaveAt = size_t(leave) * size_t(lanes), headAt = size_t(head) * size_t(lanes);
        for (size_t i = 0; i < size_t(lanes); i++)
        {
            output[i] = BlurByte(float(s1[i]) * scale);
            s1[i] -= s2[i];
            s2[i] -= ring[leaveAt + i];
            ring[leaveAt + i] = incoming[i];
            s3[i] += incoming[i];
            s1[i] += s3[i];
            int t = ring[headAt + i];
            s2[i] += t;
            s3[i] -= t;
        }
    }
}

} // namespace openshiina::scripting
