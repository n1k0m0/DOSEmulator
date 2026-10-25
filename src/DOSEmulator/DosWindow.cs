using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Input;
using OpenTK.Windowing.Common;
using SharpPixels;
using Key = OpenTK.Input.Key;
using KeyEventArgs = SharpPixels.KeyEventArgs;
using MouseState = OpenTK.Input.MouseState;

namespace DOSEmulator;

public sealed partial class DosWindow : SharpPixelsWindow
{
    private readonly MachineSession _session;
    private readonly IpcServer _ipc;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly string _settingsPath;
    private readonly string? _smokeDirectory;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private Task? _worker;
    private HostSettings _settings;
    private VideoFrame? _frame;
    private MachineStatus? _status;
    private string[] _items = [];
    private string _page = "";
    private int _selected;
    private int _smokeStage;
    private int _captureIndex = -1;
    private int _captureDelay;
    private long _nextSmokeTime = 500;
    private string _notice = "";
    private readonly List<string> _smokeCaptures = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _notices = new();
    private readonly HashSet<Key> _guestKeys = [];
    private readonly RelativeMouseInput _mouseInput = new();
    private int _releaseMouseRequested;
    private readonly bool _freeDosAcceptance;
    private readonly string? _captureAfter;
    private readonly byte[] _automaticKeys;
    private int _automaticKeyIndex;
    private readonly string? _audioOutPath;
    private readonly bool _audioAcceptance;
    private HostAudioOutput? _audio;
    private readonly List<object> _audioMeasurements = [];
    private readonly GuestTestScenario? _scenario;

    public DosWindow(PcMachine machine, HostSettings settings, string settingsPath, string? smokeDirectory, bool freeDosAcceptance = false, string? automaticText = null, string? captureAfter = null, string? audioOutPath = null, bool audioAcceptance = false, GuestTestScenario? scenario = null, bool resourceAcceptance = false, bool nativeMouseAcceptance = false)
        : base(960, 720, "DOSEmulator - 486 Development")
    {
        _session = new(machine); _settings = settings with { MasterVolume = Math.Clamp(settings.MasterVolume, 0, 100),
            SpeakerVolume = Math.Clamp(settings.SpeakerVolume, 0, 100), DigitalVolume = Math.Clamp(settings.DigitalVolume, 0, 100),
            FmVolume = Math.Clamp(settings.FmVolume, 0, 100) }; _settingsPath = settingsPath;
        _smokeDirectory = smokeDirectory;
        _freeDosAcceptance = freeDosAcceptance;
        _captureAfter = captureAfter;
        _audioOutPath = audioOutPath; _audioAcceptance = audioAcceptance;
        _automaticKeys = automaticText is null ? [] : GuestKeyboard.Encode(automaticText);
        _scenario = scenario;
        _resourceAcceptance = resourceAcceptance;
        _nativeMouseAcceptance = nativeMouseAcceptance;
        if (nativeMouseAcceptance && (smokeDirectory is null || machine.Mouse is null || resourceAcceptance || audioAcceptance))
            throw new ArgumentException("Native mouse acceptance requires a PS/2 profile and its own acceptance directory.");
        if (scenario is not null && automaticText is not null) throw new ArgumentException("Choose scenario or automatic text.");
        if (smokeDirectory is not null) Directory.CreateDirectory(smokeDirectory);
        _ipc = new(_session, instanceDirectory: Path.Combine(Path.GetDirectoryName(settingsPath)!, "ipc", "instances"));
    }

    public override void OnUserInitialize()
    {
        var audio = _audio = new HostAudioOutput(_audioOutPath);
        ApplyAudioLevels();
        if (audio.Fault is { } audioFault) _notices.Enqueue("AUDIO UNAVAILABLE: " + audioFault);
        PresentationVSyncEnabled = _settings.VSync;
        CursorState = CursorState.Normal;
        FocusedChanged += e => { if (!e.IsFocused) { if (_mouseInput.Captured) _nativeCapturedFocusLosses++; ReleaseMouse(); ReleaseGuestKeys(); } };
        MouseMove += e => SendMouse(_mouseInput.Move(e.DeltaX, e.DeltaY));
        MouseDown += e => NativeMouseButton(e.Button, true);
        MouseUp += e => NativeMouseButton(e.Button, false);
        _session.Machine.Running = true;
        _worker = Task.Run(() =>
        {
            var hostClock = Stopwatch.StartNew();
            // A small audio lead absorbs callback/batch jitter; it does not change guest rates.
            var pacing = new RealtimePacer(audio.Available ? 40_000_000UL : 0);
            ulong HostNanoseconds() => (ulong)((UInt128)(ulong)hostClock.ElapsedTicks * 1_000_000_000 / (ulong)Stopwatch.Frequency);
            PcMachine? previousMachine = null;
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    int delay = 16;
                    lock (_gate)
                    {
                        _session.PumpCommands();
                        var machine = _session.Machine;
                        _scenario?.Pump(machine);
                        if (!ReferenceEquals(previousMachine, machine))
                        {
                            if (previousMachine is not null) { previousMachine.Audio.AudioReady -= audio.Write; Interlocked.Exchange(ref _releaseMouseRequested, 1); }
                            audio.Clear(); machine.Audio.AudioReady += audio.Write;
                            pacing.Reset(machine.Scheduler.Now, HostNanoseconds()); previousMachine = machine;
                        }
                        var levels = Volatile.Read(ref _settings);
                        machine.Speaker.Volume = levels.SpeakerVolume / 100.0;
                        if (machine.SoundBlaster is { } sb) sb.Volume = levels.DigitalVolume / 100.0;
                        if (machine.Opl is { } opl) opl.Volume = levels.FmVolume / 100.0;
                        if (_automaticKeyIndex < _automaticKeys.Length && machine.Board.DebugText.Contains(machine.Profile.BootDevice == "floppy-a" ? "A:\\>" : "C:\\>", StringComparison.Ordinal)
                            && GuestKeyboard.TryInjectForBios(machine, _automaticKeys[_automaticKeyIndex])) _automaticKeyIndex++;
                        if (machine.Running && machine.Fault is null)
                        {
                            ulong target = pacing.Target(machine.Scheduler.Now, HostNanoseconds());
                            machine.RunSteps(200000, target);
                            delay = pacing.DelayMilliseconds(machine.Scheduler.Now, HostNanoseconds());
                        }
                        else { audio.Clear(); pacing.Reset(machine.Scheduler.Now, HostNanoseconds()); }
                        Volatile.Write(ref _status, machine.GetStatus());
                        Volatile.Write(ref _frame, machine.CaptureFrame());
                    }
                    _stop.Token.WaitHandle.WaitOne(delay);
                }
            }
            catch (Exception ex)
            {
                lock (_gate) Volatile.Write(ref _status, _session.Machine.GetStatus() with { State = MachineRunState.Faulted, Fault = ex.Message });
            }
        });
    }

    public override void OnUserUpdate(double time)
    {
        if (Interlocked.Exchange(ref _releaseMouseRequested, 0) != 0) ReleaseMouse();
        while (_notices.TryDequeue(out string? notice)) _notice = notice;
        if (_ipc.Fault is not null) _notice = "LOCAL IPC UNAVAILABLE: " + _ipc.Fault;
        if (_smokeDirectory is not null) AdvanceSmokeTest();
        var frame = Volatile.Read(ref _frame);
        var status = Volatile.Read(ref _status);
        if (frame is not null)
        {
            double aspect = _settings.CorrectAspect ? 4.0 / 3 : (double)frame.Width / frame.Height;
            int width = Math.Min(PixelsWidth, (int)((PixelsHeight - 40) * aspect));
            int height = (int)(width / aspect);
            _ = Enum.TryParse(_settings.Filter, out SharpPixelsGpuFilterMode filter);
            _ = Enum.TryParse(_settings.Upscale, out SharpPixelsGpuUpscaleMode upscale);
            int factor = upscale switch { SharpPixelsGpuUpscaleMode.Scale2x => 2, SharpPixelsGpuUpscaleMode.Scale3x => 3, _ => 1 };
            DrawArgbPixelsGpuPresented(frame.ArgbPixels, frame.Width, frame.Height, 0, 0, frame.Width, frame.Height,
                (PixelsWidth - width) / 2, (PixelsHeight - 40 - height) / 2, width, height, filter, upscale, factor, 8, 10, 21);
        }
        else ClearScreen();
        DrawFilledRectangle(0, PixelsHeight - 38, PixelsWidth, 38, 12, 15, 29);
        Text(15, PixelsHeight - 31, $"486 DEV | {status?.State.ToString().ToUpperInvariant() ?? "STARTING"} | VGA {frame?.Width}X{frame?.Height} | {status?.Position ?? "RESET"} | {FPS} FPS", 1, Color.LightSteelBlue);
        Text(15, PixelsHeight - 16, _mouseInput.Captured ? "MOUSE CAPTURED / CTRL+ALT RELEASE   CTRL+ALT+F10 MENU   CTRL+ALT+F11 FULLSCREEN" :
            status?.Configuration?.Mouse == "ps2-two-button" ? "CTRL+ALT+F10 MENU   CTRL+ALT+F11 FULLSCREEN   CLICK GUEST FOR PS/2 MOUSE" :
            "CTRL+ALT+F10 MENU   CTRL+ALT+F11 FULLSCREEN   EXPERIMENTAL REAL-MODE DOS", 1, Color.Gray);
        if (status?.Fault is not null && _page.Length == 0)
        {
            DrawFilledRectangleWithAlpha(20, 18, PixelsWidth - 40, 80, 30, 10, 20, 235);
            Text(32, 28, "CPU STOPPED: UNIMPLEMENTED OPERATION", 2, Color.Salmon);
            Text(32, 57, status.Fault.Length > 112 ? status.Fault[..112] : status.Fault, 1, Color.White);
        }
        if (_page.Length > 0) { RebuildItems(); DrawMenu(status); }
        else if (_notice.Length > 0)
        {
            DrawFilledRectangleWithAlpha(0, PixelsHeight - 61, PixelsWidth, 23, 35, 15, 20, 240);
            Text(15, PixelsHeight - 54, _notice.Length > 140 ? _notice[..140] : _notice, 1, Color.Salmon);
        }
        Title = $"DOSEmulator - 486 Development | {status?.State} | {FPS} FPS";
    }

    private void DrawMenu(MachineStatus? status)
    {
        const int x = 126, y = 116, width = 708, height = 472;
        DrawFilledRectangleWithAlpha(x + 8, y + 8, width, height, 0, 0, 0, 140);
        DrawFilledRectangleWithAlpha(x, y, width, height, 9, 12, 29, 246);
        DrawLine(x, y, x + width, y, Color.SlateBlue); DrawLine(x, y + height, x + width, y + height, Color.Cyan);
        DrawLine(x, y, x, y + height, Color.SlateBlue); DrawLine(x + width, y, x + width, y + height, Color.Cyan);
        Text(x + 22, y + 24, _page, 3, Color.White);
        Text(x + 22, y + 58, "ENTER CHANGE   LEFT/RIGHT ADJUST   ESC BACK", 1, Color.LightSteelBlue);
        for (int i = 0; i < _items.Length; i++)
        {
            int rowY = y + 96 + i * 33;
            if (i == _selected) DrawFilledRectangleWithAlpha(x + 12, rowY - 6, width - 24, 29, 53, 51, 85, 220);
            Text(x + 22, rowY, (i == _selected ? "> " : "  ") + _items[i], 2, i == _selected ? Color.LightGoldenrodYellow : Color.LightSteelBlue);
        }
        Text(x + 22, y + height - 61, _notice.Length > 100 ? _notice[..100] : _notice, 1, Color.Khaki);
        Text(x + 22, y + height - 38, $"IDE: {status?.MediaName ?? "NONE"}", 1, Color.LightSteelBlue);
        Text(x + 22, y + height - 21, $"STATE: {status?.State}   PROFILE: VIRTUAL486 DEV   WINDOWS 95 PENDING", 1, Color.Gray);
    }

    private void Text(int x, int y, string value, int scale, Color color)
    {
        foreach (char c in value)
        {
            for (int row = 0; row < 7; row++)
            {
                byte bits = BitmapFont.Row(c, row);
                for (int column = 0; column < 5; column++)
                    if ((bits & (1 << (4 - column))) != 0) DrawFilledRectangle(x + column * scale, y + row * scale, scale, scale, color);
            }
            x += 6 * scale;
        }
    }

    private void OpenPage(string page)
    {
        bool wasClosed = _page.Length == 0;
        _page = page; _selected = 0; _notice = ""; RebuildItems();
        if (wasClosed) { ReleaseMouse(); ReleaseGuestKeys(); Command("Pause"); _audio?.Clear(); }
    }
    private void RebuildItems()
    {
        var status = Volatile.Read(ref _status);
        var frame = Volatile.Read(ref _frame);
        var profile = status?.Configuration ?? new MachineProfile();
        _items = _page switch
        {
        "MAIN MENU" => ["CONTINUE", "VIDEO SETTINGS", "MACHINE / HARDWARE", "MEDIA", "REMOTE PLAY / NETWORK", "AUDIO SETTINGS", "RESET / POWER", "EXIT"],
        "RESET / POWER" => ["SEND CTRL+ALT+DEL TO GUEST", "HARDWARE RESET / CPU, RAM RETAINED", "POWER CYCLE / RAM CLEARED", "BACK"],
        "VIDEO SETTINGS" => [$"FILTER          {_settings.Filter.ToUpperInvariant()}", $"UPSCALE         {_settings.Upscale.ToUpperInvariant()}",
            $"ASPECT          {(_settings.CorrectAspect ? "4:3" : "SOURCE PIXELS")}", $"VSYNC           {(_settings.VSync ? "ON" : "OFF")}", "BACK"],
        "MACHINE / HARDWARE" => ["CPU: 486 REAL-MODE DEVELOPMENT SUBSET", $"RAM: {profile.RamMiB} MIB (CHANGE RESTARTS)",
            $"FLOPPY A: {profile.FloppyDriveA.ToUpperInvariant()} (CHANGE RESTARTS)", $"FLOPPY B: {profile.FloppyDriveB.ToUpperInvariant()} (CHANGE RESTARTS)",
            $"BOOT: {profile.BootDevice.ToUpperInvariant()} (CHANGE RESTARTS)",
            profile.SoundCard switch { "adlib-opl2-development" => "SOUND: ADLIB OPL2 MELODIC DEV (RESTART)",
                "sb2-opl2-development" => $"SOUND: SB2+OPL2 A{profile.SoundBlasterBasePort:X3} I{profile.SoundBlasterIrq} D1 (RESTART)",
                "sb2-dsp-development" => $"SOUND: SB2 DSP A{profile.SoundBlasterBasePort:X3} I{profile.SoundBlasterIrq} D1 (RESTART)", _ => "SOUND: SPEAKER (CHANGE RESTARTS)" },
            "SOUND CARD RESOURCES...",
            $"MOUSE: {profile.Mouse.ToUpperInvariant()} (CHANGE RESTARTS)",
            $"VGA: {frame?.Width}X{frame?.Height} / IDE: LBA28 PIO", "BACK"],
        "SOUND CARD RESOURCES" => profile.SoundCard is "sb2-dsp-development" or "sb2-opl2-development"
            ? [$"SB2 PORT: {profile.SoundBlasterBasePort:X3}H (CHANGE RESTARTS)",
                $"SB2 IRQ: {profile.SoundBlasterIrq} (CHANGE RESTARTS)", "SB2 DMA: 1 (FIXED ON THIS CARD)",
                $"DOS: SET BLASTER={profile.GetBlasterEnvironment()}", "BACK"]
            : ["SELECT AN SB2 CARD IN HARDWARE FIRST", "BACK"],
        "MEDIA" => [status?.DiskWritable == true ? "IDE: WRITES TO PRIVATE OVERLAY" : "IDE: READ-ONLY", "OPEN RAW IDE IMAGE... (READ-ONLY)", "OPEN IDE + PRIVATE OVERLAY...",
            $"FLOPPY A: {ShortName(status?.FloppyAMedia)}", $"FLOPPY B: {ShortName(status?.FloppyBMedia)}", "BOOT BUILT-IN TEST SECTOR", "BACK"],
        "FLOPPY A" or "FLOPPY B" => [$"DRIVE TYPE: {(_page == "FLOPPY A" ? profile.FloppyDriveA : profile.FloppyDriveB).ToUpperInvariant()}",
            "INSERT READ-ONLY IMAGE...", "INSERT + PRIVATE OVERLAY...", "EJECT MEDIUM", "BACK"],
        "AUDIO SETTINGS" => [_audio?.Available == true ? "OUTPUT: WINDOWS DEFAULT / 48 KHZ STEREO" : "OUTPUT: UNAVAILABLE",
            $"MUTE            {(_settings.AudioMuted ? "ON" : "OFF")}", $"MASTER          {_settings.MasterVolume}%", $"PC SPEAKER      {_settings.SpeakerVolume}%",
            $"SB2 DIGITAL     {_settings.DigitalVolume}%", $"OPL2 FM         {_settings.FmVolume}% (MELODIC DEV)", "BACK"],
        _ => ["GUEST NIC: NOT IMPLEMENTED", "REMOTE PLAY: NOT IMPLEMENTED", "BACK"]
        };
    }
    private static string ShortName(string? name) => name is null ? "EMPTY" : name.Length > 38 ? name[..35] + "..." : name.ToUpperInvariant();
    private void CloseMenu() { _page = ""; Command("Resume"); }
    private void Command(string command, object? payload = null)
    {
        _ = _session.Enqueue(IpcProtocol.CreateRequest(command, payload))
            .ContinueWith(task => { if (task.IsCompletedSuccessfully && !task.Result.Success) _notices.Enqueue($"{task.Result.ErrorCode}: {task.Result.Payload}"); }, TaskScheduler.Default);
    }

    private void Activate(int delta = 1)
    {
        if (_page == "MAIN MENU")
        {
            switch (_selected)
            {
                case 0: CloseMenu(); break;
                case 1: OpenPage("VIDEO SETTINGS"); break;
                case 2: OpenPage("MACHINE / HARDWARE"); break;
                case 3: OpenPage("MEDIA"); break;
                case 4: OpenPage("NETWORK"); break;
                case 5: OpenPage("AUDIO SETTINGS"); break;
                case 6: OpenPage("RESET / POWER"); break;
                case 7: Close(); break;
            }
        }
        else if (_page == "RESET / POWER")
        {
            switch (_selected)
            {
                case 0: Command("SendCtrlAltDel"); CloseMenu(); break;
                case 1: Command("HardwareReset"); CloseMenu(); break;
                case 2: Command("PowerCycleCurrent"); CloseMenu(); break;
                case 3: OpenPage("MAIN MENU"); break;
            }
        }
        else if (_page == "VIDEO SETTINGS")
        {
            string Cycle(string current, string[] values) => values[(Array.IndexOf(values, current) + delta + values.Length) % values.Length];
            switch (_selected)
            {
                case 0: _settings = _settings with { Filter = Cycle(_settings.Filter, ["Sharp", "Crt", "Tv"]) }; break;
                case 1: _settings = _settings with { Upscale = Cycle(_settings.Upscale, ["None", "Scale2x", "Scale3x"]) }; break;
                case 2: _settings = _settings with { CorrectAspect = !_settings.CorrectAspect }; break;
                case 3: _settings = _settings with { VSync = !_settings.VSync }; PresentationVSyncEnabled = _settings.VSync; break;
                case 4: OpenPage("MAIN MENU"); return;
            }
            SaveSettings(); RebuildItems(); _notice = "APPLIED LIVE / HOST PRESENTATION ONLY";
        }
        else if (_page == "AUDIO SETTINGS")
        {
            switch (_selected)
            {
                case 1: _settings = _settings with { AudioMuted = !_settings.AudioMuted }; break;
                case 2: _settings = _settings with { MasterVolume = Math.Clamp(_settings.MasterVolume + delta * 10, 0, 100) }; break;
                case 3: _settings = _settings with { SpeakerVolume = Math.Clamp(_settings.SpeakerVolume + delta * 10, 0, 100) }; break;
                case 4: _settings = _settings with { DigitalVolume = Math.Clamp(_settings.DigitalVolume + delta * 10, 0, 100) }; break;
                case 5: _settings = _settings with { FmVolume = Math.Clamp(_settings.FmVolume + delta * 10, 0, 100) }; break;
                case 6: OpenPage("MAIN MENU"); return;
                default: return;
            }
            ApplyAudioLevels(); SaveSettings(); RebuildItems(); _notice = "APPLIED LIVE";
        }
        else if (_page == "MEDIA")
        {
            if (_selected == 1)
            {
                using var dialog = new OpenFileDialog { Title = "Open read-only raw IDE image (development CPU limitations apply)", Filter = "Raw IDE disk (*.img;*.raw)|*.img;*.raw|All files (*.*)|*.*" };
                if (dialog.ShowDialog() == DialogResult.OK) BootRawImage(dialog.FileName);
            }
            else if (_selected == 2)
            {
                using var image = new OpenFileDialog { Title = "Select read-only base IDE image", Filter = "Raw IDE disk (*.img;*.raw)|*.img;*.raw" };
                if (image.ShowDialog() != DialogResult.OK) return;
                using var overlay = new SaveFileDialog { Title = "Create or resume private disk overlay", Filter = "Private overlay (*.cow)|*.cow",
                    DefaultExt = "cow", OverwritePrompt = false, FileName = Path.GetFileNameWithoutExtension(image.FileName) + ".cow",
                    InitialDirectory = Path.GetDirectoryName(_settingsPath) };
                if (overlay.ShowDialog() != DialogResult.OK) return;
                Command("PowerCycleOverlayDisk", new { path = Path.GetFullPath(image.FileName), overlayPath = Path.GetFullPath(overlay.FileName) });
                CloseMenu();
            }
            else if (_selected == 3) OpenPage("FLOPPY A");
            else if (_selected == 4) OpenPage("FLOPPY B");
            else if (_selected == 5) { Command("PowerCycleDemo"); CloseMenu(); }
            else if (_selected == 6) OpenPage("MAIN MENU");
        }
        else if (_page == "MACHINE / HARDWARE")
        {
            string CycleDrive(string current)
            {
                string[] values = ["none", "360k", "720k", "1200k", "1440k", "2880k"];
                return values[(Array.IndexOf(values, current) + delta + values.Length) % values.Length];
            }
            switch (_selected)
            {
                case 1: ChangeProfile(p => { int[] values = [4, 8, 16]; return p with { RamMiB = values[(Array.IndexOf(values, p.RamMiB) + delta + values.Length) % values.Length] }; }); break;
                case 2: ChangeProfile(p => p with { FloppyDriveA = CycleDrive(p.FloppyDriveA) }); break;
                case 3: ChangeProfile(p => p with { FloppyDriveB = CycleDrive(p.FloppyDriveB) }); break;
                case 4: ChangeProfile(p => p with { BootDevice = p.BootDevice == "ide" ? "floppy-a" : "ide" }); break;
                case 5: ChangeProfile(p =>
                {
                    string[] cards = ["none", "adlib-opl2-development", "sb2-dsp-development", "sb2-opl2-development"];
                    return p with { SoundCard = cards[(Array.IndexOf(cards, p.SoundCard) + delta + cards.Length) % cards.Length] };
                }); break;
                case 6: OpenPage("SOUND CARD RESOURCES"); break;
                case 7: ChangeProfile(p => p with { Mouse = p.Mouse == "none" ? "ps2-two-button" : "none" }); break;
                case 9: OpenPage("MAIN MENU"); break;
            }
        }
        else if (_page == "SOUND CARD RESOURCES")
        {
            if (_selected == _items.Length - 1) OpenPage("MACHINE / HARDWARE");
            else if (Volatile.Read(ref _status)?.Configuration?.SoundCard is "sb2-dsp-development" or "sb2-opl2-development")
            {
                if (_selected == 0) ChangeProfile(p => p with { SoundBlasterBasePort = p.SoundBlasterBasePort == 0x220 ? 0x240 : 0x220 });
                else if (_selected == 1) ChangeProfile(p =>
                {
                    int[] irqs = [3, 5, 7, 9];
                    return p with { SoundBlasterIrq = irqs[(Array.IndexOf(irqs, p.SoundBlasterIrq) + delta + irqs.Length) % irqs.Length] };
                });
            }
        }
        else if (_page is "FLOPPY A" or "FLOPPY B")
        {
            int drive = _page == "FLOPPY A" ? 0 : 1;
            if (_selected is 1 or 2)
            {
                using var image = new OpenFileDialog { Title = "Insert raw floppy image matching the configured drive", Filter = "Floppy images (*.img;*.ima;*.raw)|*.img;*.ima;*.raw" };
                if (image.ShowDialog() != DialogResult.OK) return;
                string? overlayPath = null;
                if (_selected == 2)
                {
                    using var overlay = new SaveFileDialog { Title = "Create or resume private floppy overlay", Filter = "Private overlay (*.cow)|*.cow",
                        DefaultExt = "cow", OverwritePrompt = false, FileName = Path.GetFileNameWithoutExtension(image.FileName) + ".cow", InitialDirectory = Path.GetDirectoryName(_settingsPath) };
                    if (overlay.ShowDialog() != DialogResult.OK) return;
                    overlayPath = Path.GetFullPath(overlay.FileName);
                }
                Command("InsertFloppy", new { drive, path = Path.GetFullPath(image.FileName), overlayPath });
                _notice = "MEDIA CHANGE REQUESTED / DRIVE MUST BE IDLE";
            }
            else if (_selected == 3) Command("EjectFloppy", new { drive });
            else if (_selected == 4) OpenPage("MEDIA");
        }
        else if (_selected == _items.Length - 1) OpenPage("MAIN MENU");
    }

    private void ChangeProfile(Func<MachineProfile, MachineProfile> change)
    {
        ConfigurationPlan plan;
        lock (_gate) plan = new(_session.Revision, change(_session.Machine.Profile), true);
        Command("CommitConfigurationChange", plan); Command("Pause"); _notice = "HARDWARE CHANGE REQUESTED / POWER CYCLE";
    }

    private void BootRawImage(string path)
    {
        Command("PowerCycleReadOnlyDisk", new { path = Path.GetFullPath(path) });
        CloseMenu();
    }

    private void SaveSettings()
    {
        string temporary = _settingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_settings, new JsonSerializerOptions(IpcProtocol.JsonOptions) { WriteIndented = true }));
        File.Move(temporary, _settingsPath, true);
    }

    public override void OnUserKeyDown(KeyEventArgs e)
    {
        bool hotkey = (e.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == (KeyModifiers.Control | KeyModifiers.Alt);
        if (hotkey && _mouseInput.Captured) { ReleaseMouse(); ReleaseGuestKeys(); }
        if (hotkey && e.Key == Key.F10) { if (_page.Length == 0) OpenPage("MAIN MENU"); else CloseMenu(); return; }
        if (hotkey && e.Key == Key.F11) { ToggleFullscreen(); return; }
        if (_page.Length == 0) { if (_guestKeys.Add(e.Key)) SendGuestKey(e.Key, false); return; }
        switch (e.Key)
        {
            case Key.Up: _selected = (_selected + _items.Length - 1) % _items.Length; break;
            case Key.Down: _selected = (_selected + 1) % _items.Length; break;
            case Key.Enter: case Key.Right: Activate(); break;
            case Key.Left: Activate(-1); break;
            case Key.Escape: if (_page == "MAIN MENU") CloseMenu(); else OpenPage("MAIN MENU"); break;
        }
    }
    public override void OnUserKeyPress(KeyEventArgs e) { }
    public override void OnUserKeyUp(KeyEventArgs e) { if (_guestKeys.Remove(e.Key)) SendGuestKey(e.Key, true); }
    public override void OnUserMouseMove(int x, int y) { }
    public override void OnUserMouseClick(int x, int y, MouseState mouseState) { }
    private void NativeMouseButton(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton button, bool down)
    {
        int bit = button switch { OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left => 1,
            OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right => 2, _ => 0 };
        if (bit == 0) return;
        if (down && !_mouseInput.Captured && bit == 1 && _page.Length == 0 && IsFocused &&
            Volatile.Read(ref _status)?.Configuration?.Mouse == "ps2-two-button")
        {
            // Capture only a click inside the displayed guest, never a menu/status bar.
            var frame = Volatile.Read(ref _frame);
            double aspect = _settings.CorrectAspect ? 4.0 / 3 : frame is null ? 4.0 / 3 : (double)frame.Width / frame.Height;
            int width = Math.Min(PixelsWidth, (int)((PixelsHeight - 40) * aspect)), height = (int)(width / aspect);
            double x = MousePosition.X * PixelsWidth / ClientSize.X, y = MousePosition.Y * PixelsHeight / ClientSize.Y;
            if (x < (PixelsWidth - width) / 2 || x >= (PixelsWidth + width) / 2 || y < (PixelsHeight - 40 - height) / 2 || y >= (PixelsHeight - 40 + height) / 2) return;
            _mouseInput.Capture(); CursorState = CursorState.Grabbed;
        }
        SendMouse(_mouseInput.Button(bit, down));
    }
    private void SendMouse(RelativeMouseInput.Input? input)
    {
        if (_nativeMouseAcceptance && input is not null && _nativeGuestMouseEvents.Count < 64) _nativeGuestMouseEvents.Add(input);
        if (input is not null) Command("SendMouseInput", input);
    }
    private void ReleaseMouse()
    {
        if (!_mouseInput.Captured) return;
        SendMouse(_mouseInput.Release()); CursorState = CursorState.Normal;
    }
    private void ReleaseGuestKeys()
    {
        foreach (Key key in _guestKeys) SendGuestKey(key, true);
        _guestKeys.Clear();
    }
    private void SendGuestKey(Key key, bool released)
    {
        byte code = key switch
        {
            Key.Escape => 0x01, Key.BackSpace => 0x0e, Key.Tab => 0x0f, Key.Enter => 0x1c,
            Key.ControlLeft => 0x1d, Key.ShiftLeft => 0x2a, Key.ShiftRight => 0x36, Key.AltLeft => 0x38, Key.Space => 0x39,
            Key.A => 0x1e, Key.B => 0x30, Key.C => 0x2e, Key.D => 0x20, Key.E => 0x12, Key.F => 0x21,
            Key.G => 0x22, Key.H => 0x23, Key.I => 0x17, Key.J => 0x24, Key.K => 0x25, Key.L => 0x26,
            Key.M => 0x32, Key.N => 0x31, Key.O => 0x18, Key.P => 0x19, Key.Q => 0x10, Key.R => 0x13,
            Key.S => 0x1f, Key.T => 0x14, Key.U => 0x16, Key.V => 0x2f, Key.W => 0x11, Key.X => 0x2d, Key.Y => 0x15, Key.Z => 0x2c,
            Key.Number0 => 0x0b, >= Key.Number1 and <= Key.Number9 => (byte)(2 + key - Key.Number1),
            Key.Minus => 0x0c, Key.Plus => 0x0d, Key.BracketLeft => 0x1a, Key.BracketRight => 0x1b,
            Key.Semicolon => 0x27, Key.Quote => 0x28, Key.BackSlash => 0x2b,
            Key.Comma => 0x33, Key.Period => 0x34, Key.Slash => 0x35, Key.KeypadMultiply => 0x37,
            >= Key.F1 and <= Key.F10 => (byte)(0x3b + key - Key.F1), Key.F11 => 0x57, Key.F12 => 0x58,
            Key.Up => 0x48, Key.Down => 0x50, Key.Left => 0x4b, Key.Right => 0x4d,
            Key.Delete => 0x53, Key.Home => 0x47, Key.PageUp => 0x49, Key.PageDown => 0x51,
            _ => 0
        };
        if (code == 0) return;
        if (key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Delete or Key.Home or Key.PageUp or Key.PageDown)
            Command("SendScanCode", new { scanCode = (byte)0xe0 });
        Command("SendScanCode", new { scanCode = (byte)(code | (released ? 0x80 : 0)) });
    }
    public override void OnUserFileDrop(string[] fileNames)
    {
        if (fileNames.Length > 0) BootRawImage(fileNames[0]);
    }

    private void AdvanceSmokeTest()
    {
        if (_nativeMouseAcceptance) { AdvanceNativeMouseAcceptance(); return; }
        if (_audioAcceptance) { AdvanceAudioAcceptance(); return; }
        if (_resourceAcceptance) { AdvanceSoundResourceAcceptance(); return; }
        if (_smokeStage == 0)
        {
            var current = Volatile.Read(ref _status);
            if (_freeDosAcceptance)
            {
                string prompt = current?.Configuration?.BootDevice == "floppy-a" ? "A:\\>" : "C:\\>";
                string marker = _captureAfter ?? "\r\nM1-INTERACTIVE-OK\r\n" + prompt;
                if (current?.DebugOutput.Contains(marker, StringComparison.Ordinal) != true) return;
            }
            else if (current?.State != MachineRunState.Halted) return;
        }
        if (_elapsed.ElapsedMilliseconds < _nextSmokeTime || _captureIndex >= 0) return;
        switch (_smokeStage)
        {
            case 0: _captureIndex = 0; break;
            case 1: OnUserKeyDown(new(Key.F10, KeyModifiers.Control | KeyModifiers.Alt)); _captureIndex = 1; break;
            case 2: OnUserKeyDown(new(Key.Down, 0)); OnUserKeyDown(new(Key.Enter, 0)); OnUserKeyDown(new(Key.Enter, 0)); _captureIndex = 2; break;
            case 3: OnUserKeyDown(new(Key.Down, 0)); OnUserKeyDown(new(Key.Enter, 0)); _captureIndex = 3; break;
            case 4:
                OnUserKeyDown(new(Key.Escape, 0)); OnUserKeyDown(new(Key.Down, 0)); OnUserKeyDown(new(Key.Down, 0)); OnUserKeyDown(new(Key.Enter, 0));
                _captureIndex = 4; break;
            case 5: OnUserKeyDown(new(Key.Escape, 0)); OnUserKeyDown(new(Key.Escape, 0)); _captureIndex = 5; break;
            case 6:
                File.WriteAllText(Path.Combine(_smokeDirectory!, "smoke-result.json"), JsonSerializer.Serialize(new { status = Volatile.Read(ref _status), settings = _settings, captures = _smokeCaptures,
                    audio = _audio?.Snapshot(),
                    freeDosAcceptance = _freeDosAcceptance, inputBytesSent = _automaticKeyIndex, inputBytesTotal = _automaticKeys.Length,
                    scenario = _scenario is null ? null : new { _scenario.CompletedSteps, _scenario.TotalSteps, _scenario.InputBytesSent, _scenario.InputBytesTotal, _scenario.MouseInputsSent, _scenario.Complete } }, IpcProtocol.JsonOptions));
                Close(); break;
        }
        _captureDelay = 2;
        _nextSmokeTime = _elapsed.ElapsedMilliseconds + 450;
        _smokeStage++;
    }

    private void ApplyAudioLevels() => _audio?.SetLevels(_settings.AudioMuted, _settings.MasterVolume);
    private void AdvanceAudioAcceptance()
    {
        if (_audio is null || _elapsed.ElapsedMilliseconds < _nextSmokeTime || _captureIndex >= 0) return;
        if (_smokeStage == 0 && (Volatile.Read(ref _status)?.DebugOutput.Contains(_captureAfter ?? "SPEAKER-HOLD-READY", StringComparison.Ordinal) != true || _audio.NonSilentFrames < 4800)) return;
        bool digital = Volatile.Read(ref _status)?.Configuration?.SoundCard == "sb2-dsp-development";
        bool fm = _captureAfter == "OPL2-HOLD-READY";
        void Measure(string phase) => _audioMeasurements.Add(new { phase, frames = _audio.ReadFrames, nonSilent = _audio.NonSilentFrames,
            energy = _audio.Energy, output = _audio.Snapshot() });
        void AudioMenu()
        {
            OnUserKeyDown(new(Key.F10, KeyModifiers.Control | KeyModifiers.Alt));
            for (int i = 0; i < 5; i++) OnUserKeyDown(new(Key.Down, 0));
            OnUserKeyDown(new(Key.Enter, 0));
        }
        void BackToGuest() { OnUserKeyDown(new(Key.Escape, 0)); OnUserKeyDown(new(Key.Escape, 0)); }
        switch (_smokeStage)
        {
            case 0: Measure("normal-start"); _captureIndex = 0; break;
            case 1: Measure("normal-end"); AudioMenu(); _captureIndex = 1; break;
            case 2: OnUserKeyDown(new(Key.Down, 0)); OnUserKeyDown(new(Key.Enter, 0)); _captureIndex = 2; break;
            case 3: BackToGuest(); break;
            case 4: Measure("muted-start"); break;
            case 5:
                Measure("muted-end"); AudioMenu(); OnUserKeyDown(new(Key.Down, 0)); OnUserKeyDown(new(Key.Enter, 0));
                OnUserKeyDown(new(Key.Down, 0));
                for (int i = 0; i < 10; i++) OnUserKeyDown(new(Key.Left, 0));
                for (int i = 0; i < 5; i++) OnUserKeyDown(new(Key.Right, 0));
                OnUserKeyDown(new(Key.Down, 0));
                for (int i = 0; i < 10; i++) OnUserKeyDown(new(Key.Left, 0));
                if (digital || fm)
                {
                    // Zero the other source as well: PCM must remain audible with the speaker level at zero.
                    OnUserKeyDown(new(Key.Down, 0));
                    for (int i = 0; i < 10; i++) OnUserKeyDown(new(Key.Left, 0));
                }
                if (fm)
                {
                    OnUserKeyDown(new(Key.Down, 0));
                    for (int i = 0; i < 10; i++) OnUserKeyDown(new(Key.Left, 0));
                }
                for (int i = 0; i < 5; i++) OnUserKeyDown(new(Key.Right, 0));
                _captureIndex = 3; break;
            case 6: BackToGuest(); break;
            case 7: Measure("reduced-start"); break;
            case 8: Measure("reduced-end"); break;
            case 9:
                File.WriteAllText(Path.Combine(_smokeDirectory!, "audio-result.json"), JsonSerializer.Serialize(new { status = Volatile.Read(ref _status), settings = _settings,
                    audio = _audio.Snapshot(), measurements = _audioMeasurements, captures = _smokeCaptures,
                    inputBytesSent = _automaticKeyIndex, inputBytesTotal = _automaticKeys.Length }, IpcProtocol.JsonOptions));
                Close(); break;
        }
        _captureDelay = 2; _nextSmokeTime = _elapsed.ElapsedMilliseconds + 450; _smokeStage++;
    }

    protected override void OnFrameReady()
    {
        base.OnFrameReady();
        if (_captureIndex < 0 || _smokeDirectory is null) return;
        if (_captureDelay-- > 0) return;
        string path = Path.Combine(_smokeDirectory, $"{_captureIndex:D2}-frame.png");
        _captureIndex = -1;
        int width = FramebufferSize.X, height = FramebufferSize.Y;
        int[] viewport = new int[4]; GL.GetInteger(GetPName.Viewport, viewport);
        GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out int textureWidth);
        GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureHeight, out int textureHeight);
        byte[] pixels = new byte[checked(width * height * 4)];
        GL.ReadBuffer(ReadBufferMode.Back);
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        GL.ReadPixels(0, 0, width, height, OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
        int footerBrightPixels = 0;
        int footerHeight = Math.Max(1, height * 38 / PixelsHeight);
        for (int pixel = 0; pixel < footerHeight * width; pixel++) if (pixels[pixel * 4 + 2] > 70) footerBrightPixels++;
        var guestFrame = Volatile.Read(ref _frame);
        File.WriteAllText(path + ".json", JsonSerializer.Serialize(new { page = _page, viewport, textureWidth, textureHeight,
            guestWidth = guestFrame?.Width, guestHeight = guestFrame?.Height, modeGeneration = guestFrame?.ModeGeneration,
            footerBrightPixels, error = GL.GetError().ToString() }));
        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }
        bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY); bitmap.Save(path, ImageFormat.Png);
        _smokeCaptures.Add(path);
        GL.ReadBuffer(ReadBufferMode.Back);
    }

    protected override void OnUnload()
    {
        ReleaseMouse();
        FinishNativeMouseInput();
        _stop.Cancel(); _worker?.GetAwaiter().GetResult();
        _audio?.Dispose();
        _ipc.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _session.Dispose(); _stop.Dispose();
        base.OnUnload();
    }
}
