using Wagterm.Core;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Wagterm.Pty.Tests;

[CollectionDefinition("Session input UI", DisableParallelization = true)]
public sealed class SessionInputCollection { }

[Collection("Session input UI")]
public class SessionInputTests
{
    [Theory]
    [InlineData(1000, false)] [InlineData(1002, false)] [InlineData(1003, false)]
    [InlineData(1000, true)] [InlineData(1002, true)] [InlineData(1003, true)]
    public void ActualDoubleClickReportsSecondPressToMouseTrackingApp(int mode, bool pixels)
    {
        using var mouse = new MouseHarness(mode, pixels);
        mouse.DoubleClick();
        var view = ((float ox, float oy, float cw, float ch))mouse.Call("ActivePaneView")!;
        string Report(bool press) => MouseReport.EncodePointer(0, 100, 100, 100, 100,
            view.ox, view.oy, view.cw, view.ch, 1f, 80, 24, press, !pixels, pixels);
        Assert.Equal(Report(true) + Report(false) + Report(true) + Report(false), mouse.Session.Input);
        Assert.False(mouse.HasSelection);
    }

    [Theory]
    [InlineData(1000)] [InlineData(1002)] [InlineData(1003)] [InlineData(0)]
    public void ActualDoubleClickSelectsLocallyWithShiftOrWithoutMouseReporting(int mode)
    {
        using var mouse = new MouseHarness(mode);
        mouse.SetShift(mode != 0);
        mouse.DoubleClick(heldMove: true);
        Assert.True(mouse.HasSelection);
        Assert.Equal("gamma", (string)mouse.Call("SelectionText", mouse.Pane, isStatic: true)!);
        Assert.Equal("", mouse.Session.Input);
    }

    [Theory]
    [InlineData(1002)] [InlineData(1003)]
    public void ActualForwardedLeftDragReportsPressMotionAndRelease(int mode)
    {
        using var mouse = new MouseHarness(mode);
        mouse.Dispatch(MouseHarness.WM_LBUTTONDOWN);
        mouse.Dispatch(MouseHarness.WM_MOUSEMOVE, x: 108, buttons: 1);
        mouse.Dispatch(MouseHarness.WM_LBUTTONUP, x: 108);
        var view = ((float ox, float oy, float cw, float ch))mouse.Call("ActivePaneView")!;
        string Report(int button, int x, bool press) => MouseReport.EncodePointer(button, x, 100, x, 100,
            view.ox, view.oy, view.cw, view.ch, 1f, 80, 24, press, true, false);
        Assert.Equal(Report(0, 100, true) + Report(32, 108, true) + Report(0, 108, false), mouse.Session.Input);
        Assert.False(mouse.HasSelection);
    }

    [Theory]
    [InlineData("_setOpen")] [InlineData("_helpOpen")] [InlineData("_palette")]
    public void ActualDoubleClickDoesNotReachContentUnderModalUi(string field)
    {
        using var mouse = new MouseHarness(1002);
        mouse.Set("_leftPressInContent", true);
        mouse.Set(field, field == "_palette"
            ? Enum.ToObject(mouse.App.GetType().GetField(field, MouseHarness.Flags)!.FieldType, 1)
            : true);
        mouse.Dispatch(MouseHarness.WM_LBUTTONDBLCLK);
        Assert.Equal("", mouse.Session.Input);
        Assert.False(mouse.HasSelection);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualDoubleClickAfterConsumedChromeOrLinkPressDoesNotReachContent(bool link)
    {
        using var mouse = new MouseHarness(1002);
        mouse.DoubleClick();
        Assert.NotEmpty(mouse.Session.Input);
        // A later press loses its release. The following consumed DOWN must reset both old flags.
        mouse.Dispatch(MouseHarness.WM_LBUTTONDOWN);
        mouse.Session.Input = "";
        if (link) { mouse.Set("_linkUrl", "ftp://example.com"); mouse.SetControl(); }
        else mouse.Set("_helpOpen", true); // The help card has not been laid out (empty rect), so DOWN closes it.
        mouse.DoubleClick(heldMove: true);
        Assert.Equal("", mouse.Session.Input);
        Assert.False(mouse.HasSelection);
        string state = link ? "_toastText" : "_helpOpen";
        object? value = mouse.App.GetType().GetField(state, MouseHarness.Flags)!.GetValue(mouse.App);
        if (link) Assert.Equal("blocked non-http(s) link", value);
        else Assert.Equal(false, value);
    }

    [Fact]
    public void ActualForwardedLeftPressReleasesOutsideContent()
    {
        using var mouse = new MouseHarness(1002);
        mouse.Dispatch(MouseHarness.WM_LBUTTONDOWN);
        mouse.Dispatch(MouseHarness.WM_LBUTTONUP, y: 10);
        var reports = System.Text.RegularExpressions.Regex.Matches(mouse.Session.Input, @"\x1b\[<0;(\d+);(\d+)([Mm])");
        Assert.Equal(2, reports.Count);
        Assert.Equal("M", reports[0].Groups[3].Value);
        Assert.Equal("m", reports[1].Groups[3].Value);
        Assert.Equal("1", reports[1].Groups[2].Value);
        Assert.False(mouse.HasSelection);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void ActualSendBroadcastDoesNotLetActiveProtectionBlockSibling(bool closed, bool command)
    {
        using var active = new InputSession { InputClosed = closed };
        using var live = new InputSession();
        var program = LoadProgram();
        // No Program constructor: no HWND, host, timers, or shared state is created.
        var app = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(program);
        object Make(string nested) => Activator.CreateInstance(program.GetNestedType(nested, BindingFlags.NonPublic)!, true)!;
        static void Set(object o, string name, object value) => o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, value);
        static System.Collections.IList List(object o, string name) => (System.Collections.IList)o.GetType().GetField(name)!.GetValue(o)!;
        var ws = Make("Workspace"); var ses = Make("Ses");
        var pane = Make("Pane"); var sibling = Make("Pane");
        Set(pane, "S", active); Set(pane, "ReadOnly", !closed); Set(pane, "HasSel", true); Set(pane, "ScrollOffset", 5);
        Set(sibling, "S", live); Set(ses, "Ws", ws);
        List(ses, "Panes").Add(pane); List(ses, "Panes").Add(sibling); List(ws, "Sessions").Add(ses);
        Set(app, "_active", ses); Set(app, "_session", active); Set(app, "_broadcast", true);
        Set(app, "_workspaces", Activator.CreateInstance(program.GetField("_workspaces", BindingFlags.NonPublic | BindingFlags.Instance)!.FieldType)!);
        var method = program.GetMethod(command ? "RunCommandText" : "Send", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = method.Invoke(app, command ? ["payload", "send"] : ["payload", true]);
        if (command) Assert.Contains("do not retry blindly", Assert.IsType<string>(result));
        else Assert.False(Assert.IsType<bool>(result));
        Assert.Equal(0, active.Attempts); Assert.Equal(command ? "payload\r" : "payload", live.Input);
        Assert.True((bool)pane.GetType().GetField("HasSel")!.GetValue(pane)!);
        Assert.Equal(5, pane.GetType().GetField("ScrollOffset")!.GetValue(pane));
    }

    [Fact]
    public void BroadcastContinuesPastClosedProtectedAndFailingPanes()
    {
        using var closed = new InputSession { InputClosed = true };
        using var failing = new InputSession { FailWrite = true };
        using var protectedPane = new InputSession(); using var live = new InputSession();
        Assert.False(SessionInput.Broadcast([(closed, false), (failing, false), (protectedPane, true), (live, false)], "payload"u8));
        Assert.Equal(0, closed.Attempts); Assert.Equal(1, failing.Attempts); Assert.Equal(0, protectedPane.Attempts);
        Assert.Equal("payload", live.Input); Assert.Equal(1, live.Attempts);
    }

    [Theory]
    [InlineData(false, 0, false)] [InlineData(true, 0, false)]
    [InlineData(false, 2, false)] [InlineData(true, 2, false)]
    [InlineData(false, 0, true)] [InlineData(true, 0, true)]
    [InlineData(false, 1, true)] [InlineData(true, 1, true)]
    [InlineData(false, 2, true)] [InlineData(true, 2, true)]
    public void ActualSendKeepsCoversTargetedWhileBroadcastIsOn(bool paneOverlay, int state, bool command)
    {
        using var shell = new InputSession(); using var cover = new InputSession { InputClosed = state == 2 };
        var program = LoadProgram();
        var app = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(program);
        object Make(string name) => Activator.CreateInstance(program.GetNestedType(name, BindingFlags.NonPublic)!, true)!;
        static void Set(object o, string name, object value) => o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, value);
        var ses = Make("Ses"); var pane = Make("Pane"); var overlay = Make("Pane");
        Set(pane, "S", shell); Set(overlay, "S", cover); Set(overlay, "ReadOnly", state == 1);
        ((System.Collections.IList)ses.GetType().GetField("Panes")!.GetValue(ses)!).Add(pane);
        if (paneOverlay) Set(pane.GetType().GetField("Overlay")!.GetValue(pane)!, "Term", overlay);
        else Set(app, "_cover", overlay);
        Set(app, "_active", ses); Set(app, "_session", cover); Set(app, "_broadcast", true);
        Assert.False((bool)program.GetProperty("BroadcastActive", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(app)!);
        var result = program.GetMethod(command ? "RunCommandText" : "Send", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(app, command ? ["payload", "send"] : ["payload", true]);
        Assert.Equal(0, shell.Attempts);
        Assert.Equal(state == 0 ? (command ? "payload\r" : "payload") : "", cover.Input);
        if (command) Assert.Equal(state != 0, Assert.IsType<string>(result).StartsWith(ISessionHost.RefusePrefix, StringComparison.Ordinal));
        else Assert.Equal(state == 0, Assert.IsType<bool>(result));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualPaneHostQueryReplyCannotAbortFollowingOutput(bool closed)
    {
        using var session = new InputSession { InputClosed = closed, FailWrite = true };
        var type = LoadProgram().GetNestedType("PaneHost", BindingFlags.NonPublic)!;
        session.Emulator.Host = (IHostActions)Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [null, null, session], null)!;
        // Kitty flags query requests a real host reply; the marker follows in the SAME Feed call.
        session.Emulator.Feed("\x1b[?uFINAL-after-query"u8);
        Assert.Contains("FINAL-after-query", session.Emulator.DumpRow(0));
        Assert.Equal(closed ? 0 : 1, session.Attempts);
    }

    private sealed class MouseHarness : IDisposable
    {
        public const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        public const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_MOUSEMOVE = 0x0200;
        public readonly InputSession Session = new();
        public readonly object App, Pane;
        private readonly IntPtr _hwnd;
        private readonly byte[] _keyboard = new byte[256];
        private readonly System.Collections.IDictionary _metrics;
        private readonly FieldInfo _config;
        private readonly object _previousConfig;
        private readonly IDisposable _uia;
        public bool HasSelection => (bool)Pane.GetType().GetField("HasSel")!.GetValue(Pane)!;

        public MouseHarness(int mode, bool pixels = false)
        {
            var program = LoadProgram();
            App = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(program);
            Pane = Activator.CreateInstance(program.GetNestedType("Pane", BindingFlags.NonPublic)!, true)!;
            _metrics = (System.Collections.IDictionary)program.GetField("_metrics", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            _config = program.GetField("_config", BindingFlags.NonPublic | BindingFlags.Static)!;
            _previousConfig = _config.GetValue(null)!;
            _uia = (IDisposable)Activator.CreateInstance(program.GetField("_uia", Flags)!.FieldType, true)!;
            Assert.True(GetKeyboardState(_keyboard));
            _hwnd = CreateWindowExW(0, "STATIC", "WagTerm input test", 0x80000000,
                0, 0, 800, 600, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, _hwnd);
            try
            {
                SetKeys();
                Pane.GetType().GetField("S")!.SetValue(Pane, Session);
                Pane.GetType().GetField("FontSize")!.SetValue(Pane, 987f);
                Set("_cover", Pane); Set("_session", Session); Set("_hwnd", _hwnd);
                Set("_dpi", 96f); Set("_menuBarFocus", -1);
                Set("_uia", _uia);
                var names = program.GetField("_sidebarNames", Flags)!;
                names.SetValue(App, Activator.CreateInstance(names.FieldType));
                _config.SetValue(null, new TerminalConfig { CopyOnSelect = false });
                // Avoid DirectWrite while retaining a real client area and a selectable text row.
                _metrics.Add(987f, Activator.CreateInstance(_metrics.GetType().GetGenericArguments()[1], [null, 8f, 16f]));
                Session.Inject(System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n",
                    Enumerable.Repeat("alpha beta gamma delta epsilon zeta eta theta", 24))));
                if (mode != 0) Session.Inject(System.Text.Encoding.UTF8.GetBytes($"\x1b[?{mode}h\x1b[?{(pixels ? 1016 : 1006)}h"));
                // Local selection must be reachable, otherwise HasSelection assertions are vacuous.
                Assert.NotNull(Call("PaneAt", 100, 100));
            }
            catch { Dispose(); throw; }
        }

        public void Set(string field, object value) => App.GetType().GetField(field, Flags)!.SetValue(App, value);
        public object? Call(string name, object? arg1 = null, object? arg2 = null, bool isStatic = false)
            => App.GetType().GetMethod(name, isStatic ? BindingFlags.Static | BindingFlags.NonPublic : Flags)!
                .Invoke(isStatic ? null : App, arg1 is null ? [] : arg2 is null ? [arg1] : [arg1, arg2]);
        private void SetKeys(bool shift = false, bool control = false)
        {
            var keys = (byte[])_keyboard.Clone();
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5 }) keys[key] = 0;
            if (shift) keys[0x10] = keys[0xa0] = 0x80;
            if (control) keys[0x11] = keys[0xa2] = 0x80;
            Assert.True(SetKeyboardState(keys));
        }
        public void SetShift(bool shift) => SetKeys(shift: shift);
        public void SetControl() => SetKeys(control: true);
        public void Dispatch(uint message, int x = 100, int y = 100, int buttons = 0) => App.GetType().GetMethod("WindowProcCore", Flags)!
            .Invoke(App, [_hwnd, message, (IntPtr)buttons, (IntPtr)((y << 16) | x)]);
        public void DoubleClick(bool heldMove = false)
        {
            foreach (uint message in new[] { WM_LBUTTONDOWN, WM_LBUTTONUP, WM_LBUTTONDBLCLK }) Dispatch(message);
            if (heldMove) Dispatch(WM_MOUSEMOVE, x: 108, buttons: 1);
            Dispatch(WM_LBUTTONUP);
        }
        public void Dispose()
        {
            _metrics.Remove(987f);
            _config.SetValue(null, _previousConfig);
            DestroyWindow(_hwnd);
            SetKeyboardState(_keyboard);
            _uia.Dispose();
            Session.Dispose();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool GetKeyboardState([Out] byte[] keys);
        [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] keys);
    }

    private static Type LoadProgram()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "Wagterm.Win32"))) root = Path.GetDirectoryName(root);
        var path = Directory.GetFiles(Path.Combine(root!, "src", "Wagterm.Win32", "bin"), "wagterm.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
        return Assembly.LoadFrom(path).GetType("Wagterm.Win32.Program", true)!;
    }

    private sealed class InputSession : ISession
    {
        public bool InputClosed { get; init; }
        public bool FailWrite; public int Attempts; public string Input = "";
        public ITerminalCore Emulator { get; } = new TerminalEmulator(80, 24);
        public int Cols => 80; public int Rows => 24; public int? ChildProcessId => null;
        public object SyncRoot { get; } = new();
        public AgentStatus Status => AgentStatus.Idle; public bool Blink => false; public bool AutoReset => false;
        public long StatusChangedAt => 0; public bool HasExited => false; public int? ExitCode => null;
        public event Action? OutputReceived { add { } remove { } }
        public event Action? StatusChanged { add { } remove { } }
        public event Action<string?>? SoundRequested { add { } remove { } }
        public event Action<int>? Exited { add { } remove { } }
        public void NotifyActivity() { }
        public void SetStatus(AgentStatus status, bool blink = false, bool autoReset = false, bool sound = false, string? soundName = null) { }
        public void Write(ReadOnlySpan<byte> bytes) { Attempts++; if (FailWrite) throw new IOException("closed during write"); Input += System.Text.Encoding.UTF8.GetString(bytes); }
        public Task StartAsync(string app, string[] commandLine, bool verbatimCommandLine = false, IReadOnlyDictionary<string, string>? extraEnv = null, string? cwd = null, bool deElevate = false, bool freshEnv = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> RunAsync(string app, string[] commandLine, bool verbatimCommandLine = false, CancellationToken ct = default) => throw new NotSupportedException();
        public void Attach(Microsoft.Win32.SafeHandles.SafeFileHandle conOut, Microsoft.Win32.SafeHandles.SafeFileHandle conIn, Microsoft.Win32.SafeHandles.SafeFileHandle signal, IntPtr clientProcess, int pid) => throw new NotSupportedException();
        public void Inject(ReadOnlySpan<byte> bytes) => Emulator.Feed(bytes);
        public void MutateLocked(Action<ITerminalCore> mutate) => mutate(Emulator);
        public void Resize(int cols, int rows) => Emulator.Resize(cols, rows);
        public string SnapshotRow(int row) => Emulator.DumpRow(row);
        public (int Row, int Col) SnapshotCursor() => (Emulator.CursorRow, Emulator.CursorCol);
        public void Detach() { } public void Dispose() { }
    }
}
