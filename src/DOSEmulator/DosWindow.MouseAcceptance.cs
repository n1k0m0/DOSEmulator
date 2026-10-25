using System.Runtime.InteropServices;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Runtime;
using OpenTK.Windowing.Common;

namespace DOSEmulator;

public sealed partial class DosWindow
{
    // Opt-in integration harness only. It sends real Windows events exclusively
    // to this process's verified foreground windows. Normal execution never calls
    // SendInput/SetCursorPos or changes another application's focus.
    private readonly bool _nativeMouseAcceptance;
    private int _nativeCapturedFocusLosses, _nativeMouseStage;
    private nint _nativeMouseWindow;
    private long _nativeMouseNext;
    private Form? _nativeFocusTarget;
    private NativeRect _nativeOriginalClip;
    private NativePoint _nativeOriginalPointer;
    private bool _nativePointerSaved;
    private bool _nativePointerMoved;
    private readonly List<object> _nativeMouseEvidence = [];
    private readonly List<RelativeMouseInput.Input> _nativeGuestMouseEvents = [];
    private readonly HashSet<ushort> _nativePressedKeys = [];
    private int _nativePressedButtons;

    private void AdvanceNativeMouseAcceptance()
    {
        try
        {
            Application.DoEvents();
            if (_elapsed.ElapsedMilliseconds > 45000) throw new TimeoutException($"Native mouse stage {_nativeMouseStage} timed out.");
            if (_elapsed.ElapsedMilliseconds < _nativeMouseNext || _captureIndex >= 0) return;
            string text = Volatile.Read(ref _status)?.DebugOutput ?? "";
            bool Marker(string value) => text.Contains(value, StringComparison.Ordinal);
            void Later() => _nativeMouseNext = _elapsed.ElapsedMilliseconds + 300;
            void Evidence(string phase)
            {
                if (!GetClipCursor(out NativeRect clip)) throw new InvalidOperationException("Cannot inspect Windows cursor clip.");
                _nativeMouseEvidence.Add(new { phase, logicalCapture = _mouseInput.Captured,
                    nativeCursorState = CursorState.ToString(), focused = IsFocused,
                    clip = new { clip.Left, clip.Top, clip.Right, clip.Bottom }, capturedFocusLosses = _nativeCapturedFocusLosses });
            }
            switch (_nativeMouseStage)
            {
                case 0:
                    if (!Marker("NATIVE-MOUSE-READY")) return;
                    unsafe { _nativeMouseWindow = OpenTK.Windowing.GraphicsLibraryFramework.GLFW.GetWin32Window(WindowPtr); }
                    if (_nativeMouseWindow == 0) throw new InvalidOperationException("No own mouse-test window.");
                    if (!GetClipCursor(out _nativeOriginalClip) || !GetCursorPos(out _nativeOriginalPointer)) throw new InvalidOperationException("Cannot save initial pointer state.");
                    _nativePointerSaved = true;
                    Focus(); _nativeMouseStage++; Later(); break;
                case 1:
                    File.WriteAllText(Path.Combine(_smokeDirectory!,"mouse-native-preflight.json"),JsonSerializer.Serialize(new {
                        nativeFocused = IsFocused, foregroundMatchesOwnWindow = GetForegroundWindow() == _nativeMouseWindow,
                        foregroundAvailable = GetForegroundWindow() != 0, userInteractive = Environment.UserInteractive,
                        stage = _nativeMouseStage },IpcProtocol.JsonOptions));
                    if (!IsFocused || GetForegroundWindow() != _nativeMouseWindow)
                    {
                        // A just-created window may not have completed Windows' show/
                        // activation messages yet. Retry only our own GLFW window.
                        Focus(); Later(); return;
                    }
                    ClickOwnGuest(); _nativeMouseStage++; Later(); break;
                case 2:
                    RequireNativeCapture(); Evidence("captured-with-real-windows-left-down");
                    OwnInput(new NativeInput { MouseFlags = 1, X = 40, Y = -25 },
                        new NativeInput { MouseFlags = 4 }, new NativeInput { MouseFlags = 8 });
                    _captureIndex = 0; _captureDelay = 2; _nativeMouseStage++; Later(); break;
                case 3:
                    if (!Marker("NATIVE-MOUSE-MOVED") || !Marker("NATIVE-MOUSE-LEFT") || !Marker("NATIVE-MOUSE-RIGHT")) return;
                    RequireNativeCapture();
                    _nativeFocusTarget = new Form { Text = "DOSEmulator native focus acceptance", Width = 260, Height = 100,
                        ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen };
                    _nativeFocusTarget.Show(); _nativeFocusTarget.Activate();
                    _nativeMouseStage++; Later(); break;
                case 4:
                    if (IsFocused || GetForegroundWindow() != _nativeFocusTarget!.Handle) return;
                    if (_mouseInput.Captured || CursorState != CursorState.Normal || _nativeCapturedFocusLosses < 1)
                        throw new InvalidOperationException("Focus loss did not release the native pointer.");
                    if (!GetClipCursor(out NativeRect released) || released != _nativeOriginalClip)
                        throw new InvalidOperationException("Focus loss left a Windows cursor clip behind.");
                    Evidence("real-os-focus-loss-restored-cursor-clip");
                    OwnInput(new NativeInput { MouseFlags = 16 });
                    Focus(); _nativeFocusTarget.Close(); _nativeFocusTarget.Dispose(); _nativeFocusTarget = null;
                    _nativeMouseStage++; Later(); break;
                case 5:
                    if (!IsFocused || GetForegroundWindow() != _nativeMouseWindow) return;
                    ClickOwnGuest(); _nativeMouseStage++; Later(); break;
                case 6:
                    RequireNativeCapture(); Evidence("recaptured-after-focus-return");
                    OwnInput(new NativeInput { MouseFlags = 4 }, KeyInput(0x11), KeyInput(0x12), KeyInput(0x12,true), KeyInput(0x11,true));
                    _nativeMouseStage++; Later(); break;
                case 7:
                    if (_mouseInput.Captured || CursorState != CursorState.Normal) throw new InvalidOperationException("Real Ctrl+Alt events did not release capture.");
                    if (!Marker("NATIVE-MOUSE-RELEASED")) return;
                    Evidence("real-windows-control-alt-released-pointer-and-guest-buttons");
                    _captureIndex = 1; _captureDelay = 2;
                    OwnInput(KeyInput(0x1b),KeyInput(0x1b,true)); _nativeMouseStage++; Later(); break;
                case 8:
                    if (!Marker("NATIVE-MOUSE-DOS-RETURN-OK") || !Marker("NATIVE-MOUSE-RUN-END")) return;
                    if (!text.Contains("\r\nNATIVE-MOUSE-RUN-END\r\n",StringComparison.Ordinal) || !text.EndsWith("\r\nC:\\>",StringComparison.Ordinal)) return;
                    if (Volatile.Read(ref _status)?.Fault is not null) throw new InvalidOperationException("Native mouse guest faulted.");
                    _captureIndex = 2; _captureDelay = 2; _nativeMouseStage++; Later(); break;
                case 9:
                    if (_smokeCaptures.Count != 3) return;
                    long packets; lock (_gate) packets = _session.Machine.Mouse!.PacketCount;
                    File.WriteAllText(Path.Combine(_smokeDirectory!,"mouse-native-result.json"),JsonSerializer.Serialize(new {
                        passed = true, inputSource = "Windows SendInput to verified own foreground windows; no in-process mouse injection",
                        originalHardwareMouseMeasured = false, osCaptureTested = true, osFocusLossTested = true,
                        controlAltTested = true, recaptureTested = true, guestInt33Tested = true, packets,
                        evidence = _nativeMouseEvidence, guestMouseEvents = _nativeGuestMouseEvents, captures = _smokeCaptures, status = Volatile.Read(ref _status),
                        scenario = new { _scenario!.Complete, _scenario.InputBytesSent, _scenario.InputBytesTotal }
                    },IpcProtocol.JsonOptions));
                    Close(); break;
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(_smokeDirectory!,"mouse-native-result.json"),JsonSerializer.Serialize(new {
                passed = false, stage = _nativeMouseStage, error = ex.Message, evidence = _nativeMouseEvidence,
                status = Volatile.Read(ref _status) },IpcProtocol.JsonOptions));
            Close();
        }
    }
    private void ClickOwnGuest()
    {
        if (GetForegroundWindow() != _nativeMouseWindow) throw new InvalidOperationException("Mouse target is not our own foreground window.");
        if (!GetClientRect(_nativeMouseWindow,out NativeRect client)) throw new InvalidOperationException("Cannot inspect our client rectangle.");
        var center = new NativePoint { X = (client.Right-client.Left)/2, Y = (client.Bottom-client.Top)/2 };
        if (!ClientToScreen(_nativeMouseWindow,ref center) || !SetCursorPos(center.X,center.Y)) throw new InvalidOperationException("Cannot position test pointer inside own guest.");
        _nativePointerMoved = true;
        OwnInput(new NativeInput { MouseFlags = 2 });
    }
    private void RequireNativeCapture()
    {
        if (!_mouseInput.Captured || CursorState != CursorState.Grabbed || !IsFocused)
            throw new InvalidOperationException("Windows mouse click did not grab the native cursor.");
        if (!GetClipCursor(out NativeRect clip) || clip == _nativeOriginalClip)
            throw new InvalidOperationException("GLFW did not establish a Windows cursor clip.");
    }
    private static NativeInput KeyInput(ushort key,bool up = false) => new() { Type = 1, VirtualKey = key, KeyFlags = up ? 2u : 0 };
    private void OwnInput(params NativeInput[] input)
    {
        nint foreground = GetForegroundWindow();
        if (foreground != _nativeMouseWindow && foreground != _nativeFocusTarget?.Handle)
            throw new InvalidOperationException("Refusing test input outside our own windows.");
        uint sent = SendInput((uint)input.Length,input,Marshal.SizeOf<NativeInput>());
        for (int i = 0; i < sent; i++)
        {
            NativeInput e = input[i];
            if (e.Type == 1) { if ((e.KeyFlags & 2) != 0) _nativePressedKeys.Remove(e.VirtualKey); else _nativePressedKeys.Add(e.VirtualKey); }
            else
            {
                if ((e.MouseFlags & 2) != 0) _nativePressedButtons |= 1;
                if ((e.MouseFlags & 4) != 0) _nativePressedButtons &= ~1;
                if ((e.MouseFlags & 8) != 0) _nativePressedButtons |= 2;
                if ((e.MouseFlags & 16) != 0) _nativePressedButtons &= ~2;
            }
        }
        if (sent != input.Length)
            throw new InvalidOperationException("Windows rejected native test input.");
    }
    private void FinishNativeMouseInput()
    {
        if (!_nativeMouseAcceptance) return;
        _nativeFocusTarget?.Close(); _nativeFocusTarget?.Dispose(); _nativeFocusTarget = null;
        // Balance only keys/buttons this opt-in harness may have left pressed.
        if (_nativePointerSaved)
        {
            var up = _nativePressedKeys.Select(key => KeyInput(key,true)).ToList();
            uint buttons = (uint)(((_nativePressedButtons & 1) != 0 ? 4 : 0) | ((_nativePressedButtons & 2) != 0 ? 16 : 0));
            if (buttons != 0) up.Add(new() { MouseFlags = buttons });
            if (up.Count != 0) SendInput((uint)up.Count,up.ToArray(),Marshal.SizeOf<NativeInput>());
            if (_nativePointerMoved) SetCursorPos(_nativeOriginalPointer.X,_nativeOriginalPointer.Y);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private record struct NativeRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X,Y; }
    [StructLayout(LayoutKind.Explicit,Size=40)] private struct NativeInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public int X;
        [FieldOffset(12)] public int Y;
        [FieldOffset(20)] public uint MouseFlags;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(12)] public uint KeyFlags;
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetClipCursor(out NativeRect rect);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window,out NativeRect rect);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window,ref NativePoint point);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint count,[In] NativeInput[] input,int size);
}
