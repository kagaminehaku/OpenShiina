// The engine's SCN interpreter as the executable runs it (docs/engine-notes.md, sections 4 and 10).
//
// 1,000 slots, each both a module and a task: "loadmod" puts a file's code in a slot and starts
// it, "op_000C id, label" makes a slot of a label of the current module (the engine's script
// functions), "gosub" / "callmod" run a slot's code inside the calling task. Every frame the
// scheduler runs each running task until it yields ("op_0034", the per-frame instruction budget,
// or an opcode that returns to the scheduler).
//
// Scripts use real pointers: variables, strings and code live in one flat 32-bit memory. Layout:
// g / b / a arrays, s flags, f (1,000 per slot), the stacks (1,000 dwords per slot, "l" variables
// and salloc live on it), named variables, a heap for loaded files, and the module code.
//
// Opcodes outside the core (files, pictures, text, sound, input...) are added with Register.

namespace OpenShiina.Scripting;

/// <summary>What the interpreter needs from the engine around it.</summary>
public interface IScnHost
{
    /// <summary>A file by the engine's rules (archives of the game, then its folder); null when missing.</summary>
    byte[]? ReadFile(string name);

    /// <summary>The length ReadFile gives for a file of the archives, without reading it; null when it is not in them.</summary>
    long? ArchiveFileSize(string name) => ReadFile(name)?.Length;

    /// <summary>A file of the game folder itself (RIO.INI, movies), or null.</summary>
    byte[]? ReadLooseFile(string name);

    /// <summary>Milliseconds since some fixed point (timeGetTime).</summary>
    uint Milliseconds { get; }

    /// <summary>The window title (SetWindowText).</summary>
    void SetTitle(string title);

    void SetFullScreen(bool fullScreen);

    /// <summary>Where this game's save data goes (an absolute folder path; the scripts' DataPath).</summary>
    string SaveFolder { get; }

    byte[]? ReadSaveFile(string name);

    void WriteSaveFile(string name, byte[] data);

    void DeleteSaveFile(string name);

    /// <summary>A message box; returns the button as Windows numbers it (1 OK, 2 cancel, 6 yes, 7 no).</summary>
    int MessageBox(string text, string caption, int type);

    /// <summary>The font engine text is drawn with, or null to measure and draw nothing.</summary>
    IScnFonts? Fonts => null;

    /// <summary>The shapes GDI calls of the scripts draw (op_01A4), or null for close approximations.</summary>
    IScnShapes? Shapes => null;

    /// <summary>The sound buffers, or null to keep sounds silent.</summary>
    IScnSound? Sound => null;

    /// <summary>The music streams, or null to keep music silent.</summary>
    IScnMusic? Music => null;

    /// <summary>GetAsyncKeyState: a virtual key is held down.</summary>
    bool KeyDown(int virtualKey) => false;

    /// <summary>Mouse buttons held in the window: 1 left, 2 right, 4 middle.</summary>
    int MouseButtons => 0;

    /// <summary>The game window is in front (GetForegroundWindow).</summary>
    bool Active => true;

    /// <summary>Where the mouse is, in pixels of the game's picture (GetCursorPos + ScreenToClient).</summary>
    (int X, int Y) MousePosition => (0, 0);

    /// <summary>Moves the mouse to a point of the game's picture (SetCursorPos).</summary>
    void SetMousePosition(int x, int y) { }

    /// <summary>Joystick 0 as joyGetPosEx gives it, or null when there is none.</summary>
    ScnJoystick? Joystick => null;
}

/// <summary>A joystick's position (X and Y from 0 to 65535, 32767 in the middle) and buttons (bit 0 = button 1).</summary>
public readonly record struct ScnJoystick(int X, int Y, int Buttons);

/// <summary>An opcode the interpreter does not run yet, or a script error.</summary>
public sealed class ScnException(string message, int slot, int address, int op) : Exception(message)
{
    public int Slot { get; } = slot;
    public int Address { get; } = address;
    public int Op { get; } = op;
}

/// <summary>One slot: its module, and the task running in it.</summary>
public sealed class ScnContext
{
    public int Slot { get; init; }

    /// <summary>Base of the code being run now (changes with gosub / callmod), for relative addresses.</summary>
    public int Base;
    /// <summary>The slot's own module: code start and size.</summary>
    public int CodeBase, CodeSize;
    public int Entry;
    public int Pc;
    /// <summary>Address of the instruction being run.</summary>
    public int Current;
    /// <summary>1 = running.</summary>
    public int Flags;
    public bool Loaded;
    /// <summary>Slot that registered this one with op_000C, or -1.</summary>
    public int Parent = -1;
    /// <summary>Stack index: 1000 = empty, a push goes below.</summary>
    public int Sp = ScnVm.StackSize;
    /// <summary>Arguments of callmod frames (result address, arguments, count), and its top.</summary>
    public readonly int[] Frames = new int[0x3F0];
    public int FrameTop;
    /// <summary>Sequential data reader (op_02E4 / 02E5).</summary>
    public int DataPointer;
    /// <summary>Value of the "end" that stopped the task.</summary>
    public int ExitCode;
    /// <summary>op_0083: the surface the task's text goes to (ctx+0x28).</summary>
    public int TextSurface;
    /// <summary>op_0096: the task's text record (ctx+0xFF8).</summary>
    public int TextRecord;
}

public readonly record struct ScnOperand(byte Kind, bool Relative, bool AddressOf, int Value, string? Text);

public sealed record ScnInstruction(int Op, int Address, int Length, ScnOperand[] Args, int[] Raw, int[] Targets, int[] CaseValues)
{
    /// <summary>The opcode's handler, looked up once when the instruction is decoded.</summary>
    internal ScnVm.OpHandler? Handler;
}

/// <summary>How often each opcode ran: a counter per opcode number, read like a dictionary of the ones that ran.</summary>
public sealed class ScnOpCounts : IReadOnlyDictionary<int, long>
{
    private readonly long[] m_counts = new long[0x10000];

    internal void Add(int op) => m_counts[op & 0xFFFF]++;

    public void Clear() => Array.Clear(m_counts);

    public long this[int op] => TryGetValue(op, out long n) ? n : throw new KeyNotFoundException($"Opcode {op:X4} did not run");

    public bool TryGetValue(int op, out long count)
    {
        count = (uint)op < (uint)m_counts.Length ? m_counts[op] : 0;
        return count != 0;
    }

    public bool ContainsKey(int op) => TryGetValue(op, out _);

    public IEnumerable<int> Keys => this.Select(p => p.Key);

    public IEnumerable<long> Values => this.Select(p => p.Value);

    public int Count => m_counts.Count(n => n != 0);

    public IEnumerator<KeyValuePair<int, long>> GetEnumerator()
    {
        for (int op = 0; op < m_counts.Length; op++)
            if (m_counts[op] != 0)
                yield return new(op, m_counts[op]);
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed partial class ScnVm
{
    public const int Slots = 1000, StackSize = 1000;

    // Memory regions
    private const int GRegion = 0x01000000, BRegion = 0x02000000, ARegion = 0x03000000, SRegion = 0x04000000;
    private const int FRegion = 0x05000000, StackRegion = 0x06000000, NamedRegion = 0x07000000;
    private const int HeapRegion = 0x10000000, CodeRegion = 0x40000000;

    private const int PageBits = 16, PageSize = 1 << PageBits;
    // Pages of the 32-bit address space (64 KB), made on first use (a direct table)
    private readonly byte[]?[] m_pages = new byte[]?[1 << (32 - PageBits)];
    private int m_heapTop = HeapRegion, m_codeTop = CodeRegion, m_namedTop = NamedRegion;

    private readonly ScnOpcodes m_opcodes;
    private readonly IScnHost m_host;
    private readonly ScnContext[] m_slots = new ScnContext[Slots];
    private readonly Dictionary<int, ScnInstruction> m_decoded = new();

    public delegate int OpHandler(ScnVm vm, ScnContext context, ScnInstruction instruction);
    private readonly Dictionary<int, OpHandler> m_handlers = new();

    /// <summary>Instructions a task may run in one frame before it has to yield (DAT_004880C8).</summary>
    public int Budget { get; set; } = 100_000;

    /// <summary>op_0033 / op_0032: yield after every instruction.</summary>
    public bool YieldEveryInstruction { get; set; }

    /// <summary>The task asked to quit the game ("end" with a value, or a window close).</summary>
    public bool QuitRequested { get; private set; }

    /// <summary>How often each opcode ran, for the boot report.</summary>
    public ScnOpCounts OpCounts { get; } = new();

    /// <summary>When set, the time each opcode took in all (Stopwatch ticks), for profiling.</summary>
    public Dictionary<int, long>? OpTimes { get; set; }

    public ScnVm(ScnOpcodes opcodes, IScnHost host)
    {
        m_opcodes = opcodes;
        m_host = host;
        for (int i = 0; i < Slots; i++)
            m_slots[i] = new ScnContext { Slot = i };
        RegisterCore();
        RegisterNativeCall();
        RegisterWindow();
        RegisterFiles();
        RegisterSystem();
        RegisterSurfaces();
        RegisterEvents();
        RegisterDraw();
        RegisterText();
        RegisterPrintf();
        RegisterPictures();
        RegisterSound();
        RegisterSprites();
        RegisterInput();
        RegisterMusic();
        RegisterMovie();
        RegisterGraphMovie();
        RegisterMisc();
        RegisterLayers();
        RegisterNativeKernels();
        RegisterEffects();
        RegisterGdi();
        RegisterMenus();
        RegisterEngine250();
    }

    public ScnContext Slot(int index) => m_slots[index];

    /// <summary>Adds (or replaces) an opcode outside the core.</summary>
    public void Register(int op, OpHandler handler) => m_handlers[op] = handler;

    #region Memory

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private byte[] Page(int address) => m_pages[address >>> PageBits] ?? NewPage(address);

    private byte[] NewPage(int address) => m_pages[address >>> PageBits] = new byte[PageSize];

    /// <summary>
    /// The page array and offset that hold [address, address + length) when it does not cross
    /// a page, for code that works on the bytes in place.
    /// </summary>
    public bool TryDirect(int address, int length, out byte[] page, out int offset)
    {
        offset = address & (PageSize - 1);
        page = Page(address);
        return offset + length <= PageSize;
    }

    public byte ReadByte(int address) => Page(address)[address & (PageSize - 1)];

    public void WriteByte(int address, byte value) => Page(address)[address & (PageSize - 1)] = value;

    public ushort Read16(int address) => (ushort)(ReadByte(address) | ReadByte(address + 1) << 8);

    public void Write16(int address, int value)
    {
        WriteByte(address, (byte)value);
        WriteByte(address + 1, (byte)(value >> 8));
    }

    public int Read32(int address)
    {
        int offset = address & (PageSize - 1);
        if (offset <= PageSize - 4)
            return BitConverter.ToInt32(Page(address), offset);
        return ReadByte(address) | ReadByte(address + 1) << 8 | ReadByte(address + 2) << 16 | ReadByte(address + 3) << 24;
    }

    public void Write32(int address, int value)
    {
        int offset = address & (PageSize - 1);
        if (offset <= PageSize - 4)
        {
            BitConverter.TryWriteBytes(Page(address).AsSpan(offset), value);
            return;
        }
        for (int i = 0; i < 4; i++)
            WriteByte(address + i, (byte)(value >> (8 * i)));
    }

    public void WriteBytes(int address, ReadOnlySpan<byte> bytes)
    {
        while (bytes.Length > 0)
        {
            int offset = address & (PageSize - 1), n = Math.Min(bytes.Length, PageSize - offset);
            bytes[..n].CopyTo(Page(address).AsSpan(offset));
            bytes = bytes[n..];
            address += n;
        }
    }

    /// <summary>Copies memory into <paramref name="bytes"/>, a page at a time.</summary>
    public void ReadBytes(int address, Span<byte> bytes)
    {
        while (bytes.Length > 0)
        {
            int offset = address & (PageSize - 1), n = Math.Min(bytes.Length, PageSize - offset);
            Page(address).AsSpan(offset, n).CopyTo(bytes);
            bytes = bytes[n..];
            address += n;
        }
    }

    public byte[] ReadBytes(int address, int count)
    {
        var bytes = new byte[count];
        ReadBytes(address, bytes);
        return bytes;
    }

    /// <summary>memset, a page at a time.</summary>
    public void FillMemory(int dst, int count, byte value)
    {
        while (count > 0)
        {
            int offset = dst & (PageSize - 1), n = Math.Min(count, PageSize - offset);
            Page(dst).AsSpan(offset, n).Fill(value);
            count -= n;
            dst += n;
        }
    }

    /// <summary>memmove (rep movsd / movsb on blocks that do not overlap).</summary>
    public void CopyMemory(int dst, int src, int count)
    {
        if (count <= 0)
            return;
        var buffer = count <= 1 << 20 ? new byte[count] : null;
        if (buffer != null)
        {
            ReadBytes(src, buffer);
            WriteBytes(dst, buffer);
            return;
        }
        const int Chunk = 1 << 16;
        for (int done = 0; done < count; done += Chunk)
        {
            int n = Math.Min(Chunk, count - done);
            WriteBytes(dst + done, ReadBytes(src + done, n));
        }
    }

    /// <summary>A zero-terminated Shift-JIS string.</summary>
    public string ReadString(int address)
    {
        var bytes = new List<byte>();
        for (byte c; (c = ReadByte(address)) != 0 && bytes.Count < 0x10000; address++)
            bytes.Add(c);
        return Encodings.cp932.GetString(bytes.ToArray());
    }

    public int StringLength(int address)
    {
        int n = 0;
        while (ReadByte(address + n) != 0 && n < 0x1000000)
            n++;
        return n;
    }

    public void WriteString(int address, string text)
    {
        var bytes = Encodings.cp932.GetBytes(text);
        WriteBytes(address, bytes);
        WriteByte(address + bytes.Length, 0);
    }

    // The heap (GlobalAlloc / VirtualAlloc of the engine): blocks in use by address with their
    // size, and the free ranges below the top, merged with their neighbours. Free memory is kept
    // zero - pages wholly inside a free range are dropped, the rest cleared - so a block is zero
    // when it is handed out, from a free range or from the top.
    private readonly Dictionary<int, int> m_blocks = new();
    private readonly SortedList<int, int> m_freeRanges = new();

    /// <summary>Bytes of heap in use (for reports).</summary>
    public long HeapInUse { get; private set; }

    /// <summary>Zeroed memory (GlobalAlloc / VirtualAlloc); <see cref="Free"/> gives it back.</summary>
    public int Allocate(int bytes)
    {
        int size = (Math.Max(bytes, 4) + 15) & ~15;
        // The smallest free range it fits in
        int best = -1, bestSize = int.MaxValue;
        for (int i = 0; i < m_freeRanges.Count; i++)
        {
            int length = m_freeRanges.Values[i];
            if (length >= size && length < bestSize)
            {
                best = m_freeRanges.Keys[i];
                bestSize = length;
                if (length == size)
                    break;
            }
        }
        int at;
        if (best >= 0)
        {
            m_freeRanges.Remove(best);
            if (bestSize > size)
                m_freeRanges.Add(best + size, bestSize - size);
            at = best;
        }
        else
        {
            // The heap must not grow into the code of the modules
            if ((long)m_heapTop + size > CodeRegion)
                throw new ScnException($"Out of script memory: {HeapInUse >> 20} MB in use, {size} bytes asked for", -1, 0, 0);
            at = m_heapTop;
            m_heapTop += size;
        }
        m_blocks[at] = size;
        HeapInUse += size;
        return at;
    }

    /// <summary>The address is the start of a block in use.</summary>
    public bool IsBlock(int address) => m_blocks.ContainsKey(address);

    /// <summary>Gives back a block <see cref="Allocate"/> made (GlobalFree); other addresses are ignored.</summary>
    public void Free(int address)
    {
        if (!m_blocks.Remove(address, out int size))
            return;
        HeapInUse -= size;
        int start = address, end = address + size;
        ClearRange(start, end);
        // Merge with the free ranges before and after
        int index = LowerFreeRange(start);
        if (index >= 0 && m_freeRanges.Keys[index] + m_freeRanges.Values[index] == start)
        {
            start = m_freeRanges.Keys[index];
            m_freeRanges.RemoveAt(index);
        }
        if (m_freeRanges.TryGetValue(end, out int after))
        {
            m_freeRanges.Remove(end);
            end += after;
        }
        if (end == m_heapTop)
            m_heapTop = start;      // the top comes down; it stays zero
        else
            m_freeRanges.Add(start, end - start);
    }

    /// <summary>The index of the last free range starting before <paramref name="address"/>, or -1.</summary>
    private int LowerFreeRange(int address)
    {
        var keys = m_freeRanges.Keys;
        int lo = 0, hi = keys.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (keys[mid] < address)
            {
                found = mid;
                lo = mid + 1;
            }
            else
                hi = mid - 1;
        }
        return found;
    }

    /// <summary>Zeroes [start, end): pages wholly inside are dropped, the parts of others cleared.</summary>
    private void ClearRange(int start, int end)
    {
        while (start < end)
        {
            int page = start >>> PageBits, offset = start & (PageSize - 1);
            int n = Math.Min(end - start, PageSize - offset);
            if (m_pages[page] is { } bytes)
            {
                if (n == PageSize)
                    m_pages[page] = null;
                else
                    bytes.AsSpan(offset, n).Clear();
            }
            start += n;
        }
    }

    public int AllocateCopy(ReadOnlySpan<byte> data)
    {
        int at = Allocate(data.Length + 1);
        WriteBytes(at, data);
        return at;
    }

    public int GAddress(int index) => GRegion + 4 * index;
    public int BAddress(int index) => BRegion + 4 * index;
    public int AAddress(int index) => ARegion + 4 * index;
    public int SAddress(int index) => SRegion + index;
    public int FAddress(int slot, int index) => FRegion + slot * StackSize * 4 + 4 * index;
    public int StackAddress(int slot, int index) => StackRegion + slot * StackSize * 4 + 4 * index;

    public int GetA(int index) => Read32(AAddress(index));
    public void SetA(int index, int value) => Write32(AAddress(index), value);
    public int GetB(int index) => Read32(BAddress(index));
    public void SetB(int index, int value) => Write32(BAddress(index), value);

    #endregion

    #region Stack

    public void Push(ScnContext c, int value)
    {
        if (c.Sp <= 0)
            throw Error(c, "Not Enough Stack Area");
        c.Sp--;
        Write32(StackAddress(c.Slot, c.Sp), value);
    }

    public int Pop(ScnContext c)
    {
        if (c.Sp >= StackSize)
            throw Error(c, "Not Stack Data");
        return Read32(StackAddress(c.Slot, c.Sp++));
    }

    #endregion

    #region Modules and tasks

    /// <summary>Loads a module file into a slot (op_0001 / 0002); <paramref name="start"/> also runs it as a task.</summary>
    public bool LoadModule(int slot, string file, bool start)
    {
        var code = m_host.ReadFile(file);
        if (code == null)
            return false;
        int at = m_codeTop;
        m_codeTop += (code.Length + 0xFFF + 16) & ~0xFFF;
        WriteBytes(at, code);
        RememberModule(at, file, code);
        var c = m_slots[slot];
        c.CodeBase = c.Base = c.Entry = c.Pc = at;
        c.CodeSize = code.Length;
        c.Loaded = true;
        c.Parent = -1;
        c.Sp = StackSize;
        c.FrameTop = 0;
        c.Flags = start ? 1 : 0;
        TasksChanged();
        // Decoded code of an older module at this address no longer applies
        foreach (var key in m_decoded.Keys.Where(k => k >= at && k < at + code.Length + 16).ToList())
            m_decoded.Remove(key);
        ForgetRoutines();
        return true;
    }

    /// <summary>
    /// A drawing opcode showed a picture: the frame is done. The engine's main loop is not tied
    /// to frames - it pumps window messages and runs every task a little, over and over - so a
    /// frame of the host is as many of those rounds as it takes the scripts to show a picture.
    /// </summary>
    public bool FrameShown { get; set; }

    /// <summary>
    /// Runs rounds of the engine's main loop (every running task until it yields) until the
    /// scripts show a picture or <paramref name="maxRounds"/> have run. False once the game quits.
    /// A task's 0083 text goes on a character a round; text that does not wait for time is
    /// finished before the frame ends - the engine's rounds take no time, so the window never
    /// shows it half drawn (the backlog draws its lines again each frame).
    /// </summary>
    public bool RunFrame(int maxRounds = 20000)
    {
        m_frameNumber++;
        MakePages();
        PumpGraphMovies();
        // The window is shown at start: all of it waits for its first WM_PAINT
        if (m_window == null)
            InvalidateWindow();
        PumpMessages();
        FrameShown = false;
        FrameRounds = 0;
        for (int round = 0; round < maxRounds && !QuitRequested; round++)
        {
            FrameRounds = round + 1;
            m_textWentOn = false;
            RunRound();
            FireTimers();
            PumpMessages();
            if (FrameShown && !m_textWentOn)
                break;
        }
        return !QuitRequested;
    }

    /// <summary>For diagnostics: rounds of the main loop run in the current frame so far.</summary>
    public int FrameRounds { get; private set; }

    /// <summary>For diagnostics: the task being run now.</summary>
    public ScnContext? CurrentTask { get; private set; }

    /// <summary>
    /// For diagnostics (set from another thread while a frame is slow): instructions run per
    /// slot, code base, offset and opcode.
    /// </summary>
    public volatile Dictionary<(int Slot, int Base, int Offset, int Op), int>? HotSpots;

    /// <summary>One round of the main loop: every running task until it yields.</summary>
    public void RunRound()
    {
        m_rounds++;
        int active = ActiveCount();
        for (int i = 0; i < active && !QuitRequested; i++)
        {
            var c = m_slots[i];
            if ((c.Flags & 2) != 0)
                continue;
            if ((c.Flags & 8) != 0)
            {
                // the task's 0083 text: one step a round, the task goes on when it is done
                StepTaskText(c);
                continue;
            }
            if ((c.Flags & 1) == 0)
                continue;
            Run(c);
        }
    }

    // Slots up to the last running one (DAT_00488084), kept until a task starts or stops
    private int m_activeCount = -1;

    /// <summary>A task started or stopped: the scheduler counts the running slots again.</summary>
    public void TasksChanged() => m_activeCount = -1;

    private int ActiveCount()
    {
        if (m_activeCount >= 0)
            return m_activeCount;
        int last = -1;
        for (int i = 0; i < Slots; i++)
            if ((m_slots[i].Flags & 1) != 0)
                last = i;
        return m_activeCount = last + 1;
    }

    /// <summary>Runs a task until it yields; returns the code it yielded with (0 frame end, 1 quit).</summary>
    private int Run(ScnContext c)
    {
        CurrentTask = c;
        for (int count = 1; ; count++)
        {
            int at = c.Pc;
            var ins = Decode(c, at);
            c.Current = at;
            c.Pc = at + ins.Length;
            if (HotSpots is { } hot)
            {
                lock (hot)
                    hot[(c.Slot, c.Base, at - c.Base, ins.Op)] = hot.GetValueOrDefault((c.Slot, c.Base, at - c.Base, ins.Op)) + 1;
            }
            OpCounts.Add(ins.Op);
            long started = OpTimes != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            int result = Execute(c, ins);
            if (OpTimes != null)
                OpTimes[ins.Op] = OpTimes.GetValueOrDefault(ins.Op) + System.Diagnostics.Stopwatch.GetTimestamp() - started;
            if (result == 2)
                throw Error(c, $"Script error in opcode {ins.Op:X4}");
            if (result != 0)
            {
                if (result == 1)
                    QuitRequested = true;
                return result;
            }
            if ((c.Flags & 1) == 0)
                return 0;
            // 0083 text: the task waits until the main loop has drawn it, a step a round
            if ((c.Flags & 8) != 0)
                return 0;
            if (ins.Op == 0x0034 || YieldEveryInstruction || count >= Budget)
                return 0;
        }
    }

    public ScnException Error(ScnContext c, string message) =>
        new($"{message} (slot {c.Slot}, {c.Current - c.CodeBase:X5} in its module)", c.Slot, c.Current, Read16(c.Current));

    #endregion

    #region Decoding

    public ScnInstruction Decode(ScnContext c, int at)
    {
        if (m_decoded.TryGetValue(at, out var cached))
            return cached;
        int p = at;
        int op = Read16(p);
        p += 2;
        if (!m_opcodes.TryGetLayout(op, out var layout))
            throw new ScnException($"Unknown opcode {op:X4} (slot {c.Slot}, {at - c.Base:X5})", c.Slot, at, op);
        var args = new List<ScnOperand>();
        var raw = new List<int>();
        var targets = new List<int>();
        var caseValues = new List<int>();
        foreach (var item in layout)
        {
            switch (item)
            {
                case "V":
                    args.Add(ReadOperand(ref p));
                    break;
                case "N2V":
                {
                    int count = Read16(p);
                    p += 2;
                    raw.Add(count);
                    for (int i = 0; i < count; i++)
                        args.Add(ReadOperand(ref p));
                    break;
                }
                case "SW":
                {
                    // u32 end of the table (relative), index, targets up to the end
                    int end = Read32(p);
                    p += 4;
                    raw.Add(end);
                    args.Add(ReadOperand(ref p));
                    while (p - c.Base < end && targets.Count < 0x10000)
                        targets.Add(ReadOperand(ref p).Value);
                    break;
                }
                case "CASE":
                {
                    // u32 address of the index operand, u32 target, values, FF, u32 next case
                    int target = Read32(p + 4);
                    p += 8;
                    while (ReadByte(p) != 0xFF)
                        caseValues.Add(ReadOperand(ref p).Value);
                    p++;
                    raw.Add(target);
                    raw.Add(Read32(p));
                    p += 4;
                    break;
                }
                case "VARGS":
                    while (ReadByte(p) != 0xFF)
                        args.Add(ReadOperand(ref p));
                    p++;
                    break;
                case "M1V":
                {
                    // Operands up to the first -1 (the handler reads values until it gets -1;
                    // the scripts end the list with a constant)
                    while (true)
                    {
                        var o = ReadOperand(ref p);
                        args.Add(o);
                        if (o.Kind == 4 && !o.Relative && !o.AddressOf && o.Value == -1 || args.Count >= 0x100)
                            break;
                    }
                    break;
                }
                default:
                {
                    int n = int.Parse(item.AsSpan(1));
                    raw.Add(n switch { 1 => ReadByte(p), 2 => Read16(p), 4 => Read32(p), _ => 0 });
                    p += n;
                    break;
                }
            }
        }
        var ins = new ScnInstruction(op, at, p - at, args.ToArray(), raw.ToArray(), targets.ToArray(), caseValues.ToArray())
        {
            Handler = m_handlers.GetValueOrDefault(op),
        };
        m_decoded[at] = ins;
        return ins;
    }

    private ScnOperand ReadOperand(ref int p)
    {
        int at = p;
        byte type = ReadByte(p++);
        bool addressOf = (type & 0x40) != 0;
        bool relative = (type & 0x80) != 0;
        byte kind = (byte)(addressOf ? type & 0x3F : type & 0x7F);
        switch (kind)
        {
            case >= 2 and <= 15 when kind is not 4 and not 5:
            {
                int index = Read16(p);
                p += 2;
                return new ScnOperand(kind, relative, addressOf, index, null);
            }
            case 4 or 5:
            {
                int value = Read32(p);
                p += 4;
                return new ScnOperand(kind, relative, addressOf, value, null);
            }
            case 0x10:
            {
                // The value of a string operand is its address
                int start = p;
                while (ReadByte(p) != 0)
                    p++;
                p++;
                return new ScnOperand(kind, relative, addressOf, start, null);
            }
            case 0x11:
            {
                bool integer = ReadByte(p) == 0x2E;
                if (integer)
                    p++;
                int start = p;
                while (ReadByte(p) != 0)
                    p++;
                string text = Encodings.cp932.GetString(ReadBytes(start, p - start));
                p++;
                // Value 1 marks the "." form, whose result is the expression's value
                return new ScnOperand(kind, relative, addressOf, integer ? 1 : 0, text);
            }
            case 0x12 or 0x13:
            {
                int start = p;
                while (ReadByte(p) != 0 && ReadByte(p) != (byte)'}')
                    p++;
                string name = Encodings.cp932.GetString(ReadBytes(start, p - start));
                p++;
                return new ScnOperand(kind, relative, addressOf, 0, name);
            }
            default:
                throw new ScnException($"Operand type {type:X2} at {at:X} is not supported", -1, at, 0);
        }
    }

    #endregion

    #region Operands (GETV / SETV / GETADR of the executable)

    /// <summary>Address of a variable operand (FUN_00414CA0).</summary>
    public int AddressOf(ScnContext c, in ScnOperand o)
    {
        return (o.Kind & 0x3E) switch
        {
            2 => GAddress(o.Value),
            4 => c.Base + o.Value,
            6 => SAddress(o.Value),
            8 => StackAddress(c.Slot, c.Sp + o.Value),
            10 => AAddress(o.Value),
            12 => FAddress(c.Slot, o.Value),
            14 => BAddress(o.Value),
            0x12 => NamedAddress(c, o.Text!),
            _ => throw Error(c, $"Script Error (GetVarAdr): operand type {o.Kind:X2}"),
        };
    }

    /// <summary>Value of an operand (FUN_00414E30).</summary>
    public int Value(ScnContext c, in ScnOperand o)
    {
        if (o.AddressOf)
            return AddressOf(c, o);
        int rel = o.Relative ? c.Base : 0;
        switch (o.Kind)
        {
            case 4: return o.Value + rel;
            case 5: return Read32(o.Value + rel);
            case 6: return ReadByte(SAddress(o.Value)) + rel;
            case 7: return ReadByte(ReadByte(SAddress(o.Value)) + rel);
            case 0x10: return o.Value;
            case 0x11: return Calculate(c, o.Text!, o.Value == 1);
            case 0x12: return Read32(NamedAddress(c, o.Text!)) + rel;
            case 0x13: return Read32(Read32(NamedAddress(c, o.Text!)) + rel);
        }
        // AddressOf looks at the kind without its dereference bit
        int value = Read32(AddressOf(c, o));
        return (o.Kind & 1) == 0 ? value + rel : Read32(value + rel);
    }

    /// <summary>Stores a value into an operand (FUN_004148E0).</summary>
    public void Store(ScnContext c, in ScnOperand o, int value)
    {
        int rel = o.Relative ? c.Base : 0;
        switch (o.Kind)
        {
            case 4 or 0x10 or 0x11:
                return;     // constants take nothing
            case 5:
                Write32(o.Value + rel, value);
                return;
            case 6:
                WriteByte(SAddress(o.Value), (byte)(value & 1));
                return;
            case 7:
                WriteByte(ReadByte(SAddress(o.Value)) + rel, (byte)(value & 1));
                return;
            case 0x12:
                Write32(NamedAddress(c, o.Text!), value);
                return;
            case 0x13:
                Write32(Read32(NamedAddress(c, o.Text!)) + rel, value);
                return;
        }
        int address = AddressOf(c, o);
        if ((o.Kind & 1) == 0)
            Write32(address, value);
        else
            Write32(Read32(address) + rel, value);
    }

    #endregion
}
