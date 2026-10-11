// WarcPacker: packs files into a WARC 1.7 archive, the reverse of WarcOpener (from
// GrandCrossExtractor). The files of a folder named as entries of an archive are packed (YPK,
// zlib) and encrypted with the game's scheme; the other entries are copied as they are, so the
// game reads the new archive as the old one with those files changed. Older WARC versions keep
// their index range-coded, which there is no encoder for.

using System.IO.Compression;
using System.Text;

namespace OpenShiina.Archives;

/// <summary>
/// Packs files into a WARC 1.7 archive: the reverse of <see cref="WarcOpener"/>.
/// </summary>
public static class WarcPacker
{
    /// <summary>
    /// Repacks a WARC archive.  For each entry whose name matches a file in
    /// <paramref name="sourceDir"/>, the new content is packed and encrypted.
    /// Entries without a matching file are copied byte-for-byte from the
    /// original archive (no decrypt → re-encrypt round-trip, so they are
    /// reproduced perfectly).
    /// </summary>
    /// <param name="originalArchive">The currently open archive (provides
    ///     entry metadata, the encryption scheme, and raw data for unchanged
    ///     entries).</param>
    /// <param name="sourceDir">Folder with the modified files.  File names
    ///     must match the archive entry names (case-insensitive).  Files that
    ///     don't match any entry are silently ignored.</param>
    /// <param name="outputPath">Destination path for the new <c>.WAR</c> file.
    ///     Must not be the same as the original archive.</param>
    /// <param name="progress">Optional progress callback
    ///     (<c>current</c> 1-based, <c>total</c>, <c>name</c>).</param>
    /// <returns>The number of entries that were replaced from the source
    ///     folder.</returns>
    public static int Repack(
        WarcArchive originalArchive,
        string sourceDir,
        string outputPath,
        IProgress<(int current, int total, string name)>? progress = null)
    {
        var decoder = originalArchive.Decoder;
        if (decoder.WarcVersion != 170)
            throw new NotSupportedException($"Only WARC 1.7 archives can be packed (this one is WARC {decoder.WarcVersion / 100}.{decoder.WarcVersion % 100 / 10}).");
        var entries = originalArchive.Entries;
        int replaced = 0;

        // ── Build a case-insensitive lookup of the source folder ───────────
        var sourceFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(sourceDir))
        {
            foreach (var f in Directory.GetFiles(sourceDir))
                sourceFiles[Path.GetFileName(f)] = f;
        }

        // ── Write the archive ──────────────────────────────────────────────
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite);

        // Header: "WARC 1.7" (8 bytes) + XOR'd index_offset (4 bytes)
        output.Write(Encoding.ASCII.GetBytes("WARC 1.7"), 0, 8);
        output.Write(new byte[4], 0, 4); // placeholder — patched in Phase 4

        var newEntries = new List<WarcEntry>(entries.Count);

        for (int i = 0; i < entries.Count; i++)
        {
            var origEntry = (WarcEntry)entries[i];
            progress?.Report((i + 1, entries.Count, origEntry.Name));

            long entryOffset = output.Position;
            string safeName = MediaConverter.SafeFileName(origEntry.Name);

            if (sourceFiles.TryGetValue(safeName, out var srcPath))
            {
                // ── Modified entry: pack & encrypt new content ─────────
                byte[] content = File.ReadAllBytes(srcPath);
                byte[] packed = PackAndEncryptEntry(decoder, origEntry, content);
                output.Write(packed, 0, packed.Length);

                newEntries.Add(new WarcEntry
                {
                    Name    = origEntry.Name,
                    Type    = origEntry.Type,
                    Offset  = entryOffset,
                    Size    = (uint)packed.Length,
                    UnpackedSize = origEntry.IsPacked
                                       ? (uint)content.Length
                                       : (uint)packed.Length,
                    IsPacked = origEntry.IsPacked,
                    FileTime = origEntry.FileTime,
                    Flags    = origEntry.Flags,
                });
                replaced++;
            }
            else
            {
                // ── Unchanged entry: copy raw encrypted bytes ──────────
                byte[] raw;
                lock (originalArchive)
                {
                    raw = originalArchive.File.View.ReadBytes(
                              origEntry.Offset, origEntry.Size);
                }
                output.Write(raw, 0, raw.Length);

                newEntries.Add(new WarcEntry
                {
                    Name         = origEntry.Name,
                    Type         = origEntry.Type,
                    Offset       = entryOffset,
                    Size         = origEntry.Size,
                    UnpackedSize = origEntry.UnpackedSize,
                    IsPacked     = origEntry.IsPacked,
                    FileTime     = origEntry.FileTime,
                    Flags        = origEntry.Flags,
                });
            }
        }

        // ── Encrypted index ────────────────────────────────────────────────
        uint indexOffset = (uint)output.Position;
        byte[] encIndex = BuildAndEncryptIndex(decoder, newEntries, indexOffset);
        output.Write(encIndex, 0, encIndex.Length);

        // ── Patch header with real index offset ────────────────────────────
        output.Position = 8;
        byte[] ioBuf = new byte[4];
        LittleEndian.Pack((int)(indexOffset ^ 0xF182AD82u), ioBuf, 0);
        output.Write(ioBuf, 0, 4);

        return replaced;
    }

    /// <summary>
    /// Reads every entry of the archive at <paramref name="packedPath"/> that has a file in
    /// <paramref name="sourceDir"/> (opened with <paramref name="originalArchive"/>'s scheme)
    /// and compares it with the file; the entries that differ or do not read, empty when all do.
    /// </summary>
    public static List<string> Verify(string packedPath, WarcArchive originalArchive, string sourceDir)
    {
        var wrong = new List<string>();
        var sourceFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(sourceDir))
            sourceFiles[Path.GetFileName(f)] = f;
        var scheme = FormatManager.Instance.GetScheme(originalArchive.SchemeName)
                     ?? throw new InvalidOperationException($"No scheme {originalArchive.SchemeName}.");
        using var view = new ArcView(packedPath);
        using var packed = WarcOpener.TryOpen(view, scheme)
                           ?? throw new InvalidDataException("The new archive does not open.");
        foreach (var entry in packed.Entries)
        {
            if (!sourceFiles.TryGetValue(MediaConverter.SafeFileName(entry.Name), out var file))
                continue;
            try
            {
                if (!WarcOpener.OpenEntry(packed, entry).AsSpan().SequenceEqual(File.ReadAllBytes(file)))
                    wrong.Add($"{entry.Name}: reads back different");
            }
            catch (Exception ex)
            {
                wrong.Add($"{entry.Name}: {ex.Message}");
            }
        }
        return wrong;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Entry packing & encryption  (reverse of WarcOpener.OpenEntry)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Packs and encrypts a single entry's content, producing the raw bytes
    /// to store in the archive.  The operations are the exact reverse of
    /// <see cref="WarcOpener.OpenEntry"/>:
    /// <list type="number">
    ///   <item>Post-unpack encryption (reverse of extraction steps 4–5)</item>
    ///   <item>YPK compression</item>
    ///   <item>Main encryption (reverse of extraction steps 1–3)</item>
    /// </list>
    /// </summary>
    private static byte[] PackAndEncryptEntry(
        Decoder decoder, WarcEntry origEntry, byte[] content)
    {
        byte[] data;

        if (origEntry.IsPacked)
        {
            // ── Reverse of the post-unpack decryption ──────────────────
            //   Extraction did:  Decrypt2(unpacked)  then  ExtraCrypt.Decrypt(…, 0x204)
            //   We do the reverse: ExtraCrypt.Encrypt(…, 0x104)  then  Decrypt2 (self-inverse)
            byte[] toCompress = new byte[content.Length];
            Buffer.BlockCopy(content, 0, toCompress, 0, content.Length);

            if (decoder.WarcVersion > 110)
            {
                if (decoder.ExtraCrypt != null)
                    decoder.ExtraCrypt.Encrypt(
                        toCompress, 0, (uint)toCompress.Length, 0x104);

                if (0 != (origEntry.Flags & 0x40000000))
                    decoder.Decrypt2(toCompress, 0, (uint)toCompress.Length);
            }

            // ── Compress with YPK (zlib) ───────────────────────────────
            // Every GRAND†CROSS game accepts all three pack formats
            // (YH1 / YPK / YLZ); YPK is the simplest to produce.
            data = CompressYPK(toCompress, (uint)content.Length);
        }
        else
        {
            // Not packed: the content IS the raw entry data.
            data = new byte[content.Length];
            Buffer.BlockCopy(content, 0, data, 0, content.Length);
        }

        // ── Reverse of the main decryption ─────────────────────────────
        //   Extraction did:  Decrypt  →  ExtraCrypt(0x202)  →  Decrypt2
        //   We do the reverse: Decrypt2  →  ExtraCrypt(0x102)  →  Encrypt
        if (data.Length > 8 && decoder.WarcVersion > 110)
        {
            uint size = (uint)data.Length;

            // Decrypt2 is self-inverse (XOR-based)
            if (0 != (origEntry.Flags & 0x20000000u))
                decoder.Decrypt2(data, 8, size - 8);

            if (decoder.ExtraCrypt != null)
                decoder.ExtraCrypt.Encrypt(data, 8, size - 8, 0x102);

            if (0 != (origEntry.Flags & 0x80000000u))
                decoder.Encrypt(data, 8, size - 8);
        }

        return data;
    }

    /// <summary>
    /// Compresses data into the YPK format used by WARC archives:
    /// 4-byte XOR'd signature + 4-byte unpacked size + zlib stream.
    /// </summary>
    private static byte[] CompressYPK(byte[] input, uint unpackedSize)
    {
        // Zlib compress
        var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(input, 0, input.Length);
        byte[] compressed = ms.ToArray();

        // YPK packet = sig(4) + unpackedSize(4) + compressed_bytes
        //
        // The plain signature is 'YPK' with 01 in its top byte, as every YPK entry of the
        // GRAND†CROSS archives has it.  The archive stores the lower 3 bytes XOR'd:
        // sig ^= (unpackedSize ^ 0x82AD82) & 0xFFFFFF.  A non-zero top byte means the
        // packet from byte 8 on is XOR'd with ~0x4B4D4B4D (whole dwords, then the
        // remaining bytes with its low byte), which the reader undoes (UnpackYPK).
        byte[] result = new byte[8 + compressed.Length];
        uint storedSig = 0x014B5059u ^ ((unpackedSize ^ 0x82AD82u) & 0xFFFFFF);
        LittleEndian.Pack((int)storedSig, result, 0);
        LittleEndian.Pack((int)unpackedSize, result, 4);
        Buffer.BlockCopy(compressed, 0, result, 8, compressed.Length);
        uint key = ~0x4B4D4B4Du;
        int i;
        for (i = 2; i < result.Length / 4; ++i)
            LittleEndian.Pack((int)(LittleEndian.ToUInt32(result, i * 4) ^ key), result, i * 4);
        for (i *= 4; i < result.Length; ++i)
            result[i] ^= (byte)key;

        return result;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Index building
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Serialises the entry records, zlib-compresses them, and encrypts the
    /// result as a <see cref="Decoder.MaxIndexLength"/>-byte buffer that
    /// the reader expects; the part up to the end of the zlib stream is returned.
    /// </summary>
    private static byte[] BuildAndEncryptIndex(
        Decoder decoder, List<WarcEntry> entries, uint indexOffset)
    {
        // ── Serialise entry records ────────────────────────────────────
        using var records = new MemoryStream();
        using (var w = new BinaryWriter(records, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var e in entries)
            {
                // Name: EntryNameSize bytes, null-terminated & zero-padded
                byte[] nameBuf = new byte[decoder.EntryNameSize];
                byte[] nameBytes = Encodings.cp932.GetBytes(e.Name);
                int n = Math.Min(nameBytes.Length, nameBuf.Length - 1);
                Buffer.BlockCopy(nameBytes, 0, nameBuf, 0, n);
                w.Write(nameBuf);

                w.Write((uint)e.Offset);     // 4 bytes
                w.Write(e.Size);             // 4 bytes
                w.Write(e.UnpackedSize);     // 4 bytes
                w.Write(e.FileTime);         // 8 bytes
                w.Write(e.Flags);            // 4 bytes
            }
        }

        // ── Zlib compress ──────────────────────────────────────────────
        var comp = new MemoryStream();
        using (var zlib = new ZLibStream(comp, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] raw = records.ToArray();
            zlib.Write(raw, 0, raw.Length);
        }
        byte[] zdata = comp.ToArray();

        // ── Assemble the full index buffer ─────────────────────────────
        // The reader always allocates MaxIndexLength bytes and decrypts
        // that many, so the packer must produce exactly that size to keep
        // the PRNG in sync.
        uint maxLen = decoder.MaxIndexLength;
        if (8u + (uint)zdata.Length > maxLen)
            throw new InvalidOperationException(
                $"The compressed index ({8 + zdata.Length} bytes) exceeds " +
                $"the maximum index size ({maxLen} bytes) for this WARC version.");

        byte[] index = new byte[maxLen];
        // bytes 0–7: reserved (the reader skips them)
        // bytes 8+ : zlib data (byte 8 must be 0x78 = zlib header)
        Buffer.BlockCopy(zdata, 0, index, 8, zdata.Length);

        // ── Encrypt (XorIndex + Encrypt) ───────────────────────────────
        decoder.EncryptIndex(indexOffset, index);
        // Only the zlib stream is written, as the original archives do: each byte is
        // encrypted from the bytes before it, and the reader pads the rest of the buffer.
        return index.AsSpan(0, 8 + zdata.Length).ToArray();
    }
}
