// Window and system opcodes (07xx). On Windows the engine calls user32 directly; here the title
// and full screen go to the host, and the calls that only make sense for a Win32 window (find a
// window, class styles, posted messages, repaints) answer the way a single running copy of the
// game would see them.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>
    /// Engine globals set by simple opcodes, by their address in the executable (0x488098 ...),
    /// for the opcodes that read them back.
    /// </summary>
    public Dictionary<int, int> EngineGlobals { get; } = new();

    public int EngineGlobal(int address) => EngineGlobals.GetValueOrDefault(address);

    /// <summary>A stand-in for the game window's handle: never 0.</summary>
    public const int WindowHandle = 0x00010001;

    private void RegisterWindow()
    {
        // 07EE hwnd, index, v: GetClassLong (0 = the game window); 07EF hwnd, index, value: SetClassLong
        Register(0x07EE, (vm, c, i) =>
        {
            vm.Value(c, i.Args[0]);
            int index = vm.Value(c, i.Args[1]);
            vm.Store(c, i.Args[2], vm.m_classLongs.GetValueOrDefault(index, index == -26 ? 0x0B : 0));
            return 0;
        });
        Register(0x07EF, (vm, c, i) =>
        {
            vm.Value(c, i.Args[0]);
            int index = vm.Value(c, i.Args[1]);
            vm.m_classLongs[index] = vm.Value(c, i.Args[2]);
            return 0;
        });
        // 07B2 class, title, v: FindWindow - no other copy of the game is running
        Register(0x07B2, (vm, c, i) =>
        {
            vm.Value(c, i.Args[0]);
            vm.Value(c, i.Args[1]);
            vm.Store(c, i.Args[2], 0);
            return 0;
        });
        // 07B3 v: the game window's handle
        Register(0x07B3, (vm, c, i) => { vm.Store(c, i.Args[0], WindowHandle); return 0; });
        // 07BC / 07BD hwnd, message, wparam, lparam: PostMessage / SendMessage
        Register(0x07BC, (vm, c, i) => vm.WindowMessage(c, i));
        Register(0x07BD, (vm, c, i) => vm.WindowMessage(c, i));
        // 07D1 UpdateWindow, 07D2 l, t, r, b: ValidateRect - nothing to do
        // 07D1: UpdateWindow - WM_PAINT now if part of the window is invalid
        Register(0x07D1, (vm, c, i) => { vm.Paint(); return 0; });
        Register(0x07D2, (vm, c, i) =>
        {
            foreach (var a in i.Args)
                vm.Value(c, a);
            return 0;
        });
        // 07A8 hwnd, text: SetWindowTextA (FUN_0041F5D0 in v2.47; 0 is the game window): the title,
        // or a control's text
        Register(0x07A8, (vm, c, i) =>
        {
            int hwnd = vm.Value(c, i.Args[0]);
            string text = vm.ReadString(vm.Value(c, i.Args[1]));
            if (vm.m_controls.ContainsKey(hwnd))
                vm.m_controls[hwnd] = text;
            else if (hwnd is 0 or WindowHandle)
            {
                vm.m_windowTitle = text;
                vm.m_host.SetTitle(text);
            }
            return 0;
        });
        // 07A9 hwnd, buffer, size: GetWindowTextA (FUN_0041F600): at most size - 1 bytes and a 0
        Register(0x07A9, (vm, c, i) =>
        {
            int hwnd = vm.Value(c, i.Args[0]), buffer = vm.Value(c, i.Args[1]), size = vm.Value(c, i.Args[2]);
            string text = vm.m_controls.TryGetValue(hwnd, out var t) ? t : hwnd is 0 or WindowHandle ? vm.m_windowTitle : "";
            if (size > 0)
            {
                byte[] bytes = OpenShiina.IO.Encodings.cp932.GetBytes(text);
                int n = Math.Min(bytes.Length, size - 1);
                vm.WriteBytes(buffer, bytes.AsSpan(0, n));
                vm.WriteByte(buffer + n, 0);
            }
            return 0;
        });
        // 0960 text, x, y, w, h, v: an edit box on the window (FUN_00416F20: CreateWindowExA "EDIT",
        // child, visible, bordered, at most 256 controls), v its handle; 097E hwnd: it goes
        // (FUN_00417160: DestroyWindow). Iyashinbo The Motion asks for its saves' comments so. The
        // players show no box yet: its text is what the scripts set (07A8), the box stays empty.
        Register(0x0960, (vm, c, i) =>
        {
            string text = vm.ReadString(vm.Value(c, i.Args[0]));
            for (int k = 1; k <= 4; k++)
                vm.Value(c, i.Args[k]);
            if (vm.m_controls.Count >= MaxControls)
                return 2;
            int hwnd = ControlHandle;
            while (vm.m_controls.ContainsKey(hwnd))
                hwnd++;
            vm.m_controls[hwnd] = text;
            vm.Store(c, i.Args[5], hwnd);
            return 0;
        });
        Register(0x097E, (vm, c, i) =>
        {
            vm.m_controls.Remove(vm.Value(c, i.Args[0]));
            return 0;
        });
        // 0778 on: full screen on / off; 0776: full screen off
        Register(0x0778, (vm, c, i) => { vm.m_host.SetFullScreen(vm.Value(c, i.Args[0]) != 0); vm.InvalidateWindow(); return 0; });
        Register(0x0776, (vm, c, i) => { vm.m_host.SetFullScreen(false); vm.InvalidateWindow(); return 0; });
        // 079E: the window gets a maximise box
        Register(0x079E, (vm, c, i) => 0);
        // 079F: the window loses it (FUN_004201C0: style 0x487F74 without WS_MAXIMIZEBOX, set
        // when not full screen); Ero-On! does this at start
        Register(0x079F, (vm, c, i) => 0);
        // Engine settings kept in globals
        Register(0x076C, (vm, c, i) => vm.SetGlobal(0x488090, c, i));
        Register(0x076D, (vm, c, i) => vm.SetGlobal(0x488094, c, i));
        Register(0x078A, (vm, c, i) => vm.SetGlobal(0x488098, c, i));
        Register(0x0794, (vm, c, i) => vm.SetGlobal(0x48808C, c, i));
        Register(0x07E4, (vm, c, i) =>
        {
            int result = vm.SetGlobal(0x4880A8, c, i);
            if (vm.Trace != null)
                vm.TraceLine(c, $"07E4 message slot {vm.EngineGlobals.GetValueOrDefault(0x4880A8, -1)}");
            return result;
        });
        Register(0x0136, (vm, c, i) => vm.SetGlobal(0x4880D0, c, i));
    }

    private readonly Dictionary<int, int> m_classLongs = new();

    // The controls 0960 made (stand-in handles from ControlHandle on) and their text; the title
    // the scripts gave the window
    private readonly Dictionary<int, string> m_controls = new();
    private const int MaxControls = 0x100, ControlHandle = 0x00090001;
    private string m_windowTitle = "";

    private int SetGlobal(int address, ScnContext c, ScnInstruction i)
    {
        EngineGlobals[address] = Value(c, i.Args[0]);
        return 0;
    }

    private int WindowMessage(ScnContext c, ScnInstruction i)
    {
        Value(c, i.Args[0]);
        int message = Value(c, i.Args[1]);
        Value(c, i.Args[2]);
        Value(c, i.Args[3]);
        // WM_CLOSE / WM_DESTROY to the game window end the game
        if (message is 0x10 or 0x02)
            QuitRequested = true;
        return 0;
    }
}
