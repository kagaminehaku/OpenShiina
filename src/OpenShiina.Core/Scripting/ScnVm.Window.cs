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
        // 07A8 hwnd, text: the window title
        Register(0x07A8, (vm, c, i) =>
        {
            vm.Value(c, i.Args[0]);
            vm.m_host.SetTitle(vm.ReadString(vm.Value(c, i.Args[1])));
            return 0;
        });
        // 0778 on: full screen on / off; 0776: full screen off
        Register(0x0778, (vm, c, i) => { vm.m_host.SetFullScreen(vm.Value(c, i.Args[0]) != 0); vm.InvalidateWindow(); return 0; });
        Register(0x0776, (vm, c, i) => { vm.m_host.SetFullScreen(false); vm.InvalidateWindow(); return 0; });
        // 079E: the window gets a maximise box
        Register(0x079E, (vm, c, i) => 0);
        // Engine settings kept in globals
        Register(0x076C, (vm, c, i) => vm.SetGlobal(0x488090, c, i));
        Register(0x076D, (vm, c, i) => vm.SetGlobal(0x488094, c, i));
        Register(0x078A, (vm, c, i) => vm.SetGlobal(0x488098, c, i));
        Register(0x0794, (vm, c, i) => vm.SetGlobal(0x48808C, c, i));
        Register(0x07E4, (vm, c, i) => vm.SetGlobal(0x4880A8, c, i));
        Register(0x0136, (vm, c, i) => vm.SetGlobal(0x4880D0, c, i));
    }

    private readonly Dictionary<int, int> m_classLongs = new();

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
