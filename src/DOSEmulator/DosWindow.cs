// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;
using DOSEmulator.Whpx;
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
    private int _closeRequested;
    /// <summary>Schedules a clean close on the window owner after an update is ready.</summary>
    public void RequestClose() => Interlocked.Exchange(ref _closeRequested, 1);
    private readonly string _settingsPath;
    private readonly string? _smokeDirectory;
    private readonly bool _enableHostAudio;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private Task? _worker;
    private JoinedWorker? _videoWorker;
    private Task? _diskCreation;
    private readonly CancellationTokenSource _diskCreationStop = new();
    private int _machineOperationPending;
    private long _noticeUntil;
    private HostSettings _settings;
    private bool _showDebugOverlay = true;
    private bool _parallelVideo;
    private VideoFrame? _frame;
    private long _displayPixelRevision;
    private readonly LatestBufferExchange<VgaFrameBuffer> _frames = new(() => new());
    // Held until the NEXT UI update: SharpPixels uploads after OnUserUpdate returns.
    private LatestBufferExchange<VgaFrameBuffer>.ReadLease? _displayFrame;
    private MachineStatus? _status;
    private double _realtimePercent = double.NaN;
    private string _guestCalendar = "";
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
    private int _guestRepeatEnabled;
    private readonly RelativeMouseInput _mouseInput = new();
    private long _uiInputGeneration;
    /// <summary>Own immutable network-status response tied to the confirmed input owner; it contains no live backend.</summary>
    private sealed record NetworkMenuSnapshot(long Generation, JsonElement Status);
    /// <summary>Published only after an owner-queued status request completes for the same PC generation.</summary>
    private NetworkMenuSnapshot? _networkMenu;
    /// <summary>Bounds asynchronous status queries while a network page is visible.</summary>
    private int _networkQueryPending;
    /// <summary>Next monotonic frontend status refresh; this is presentation policy, never saved guest time.</summary>
    private long _networkRefreshAt;
    private readonly bool _freeDosAcceptance;
    private readonly string? _captureAfter;
    private readonly byte[] _automaticKeys;
    private int _automaticKeyIndex;
    private readonly string? _audioOutPath;
    private readonly bool _audioAcceptance;
    private HostAudioOutput? _audio;
    private readonly List<object> _audioMeasurements = [];
    private readonly GuestTestScenario? _scenario;

    /// <summary>Uses the reclaimed footer space consistently for guest presentation and mouse capture.</summary>
    private int GuestAreaHeight => PixelsHeight - (_showDebugOverlay ? 40 : 0);

    /// <summary>Reports the instantiated board and nominal clock without presenting its validation subset as a finished historical PC.</summary>
    private static string PlatformLabel(MachineProfile? profile) => profile?.IsP55c == true
        ? $"DOSE MMX / FX + PIIX + S3 / P55C subset / {profile.CpuHz / 1_000_000} MHz" : profile?.IsWindows95Board == true
        ? $"430FX / PIIX / S3 / P54C B5 subset / {profile.CpuHz / 1_000_000} MHz" : profile?.IsPciBiosBoard == true
        ? "430FX / PIIX / S3 / PCI BIOS subset / P54C 100 MHz" : profile?.IsTrio64 == true
        ? "430FX / PIIX / Trio64 subset / P54C 100 MHz" : profile?.IsPiixReset == true
        ? "430FX / PIIX reset subset / P54C 100 MHz" : profile?.IsPiixIde == true
        ? "430FX / PIIX IDE subset / P54C 100 MHz" : profile?.IsPiixIrq == true
        ? "430FX / PIIX IRQ subset / P54C 100 MHz" : profile?.IsFxHost == true
        ? "430FX host / P54C B5 subset / 100 MHz"
        : profile?.IsP54c == true ? "DOSE AT / P54C B5 subset / 90 MHz" : "486 Development";

    /// <summary>Menus may repeat while the guest is paused; guest makes require its current owner to permit execution.</summary>
    protected override bool KeyRepeatsEnabled => IsFocused && (_page.Length != 0 || Volatile.Read(ref _guestRepeatEnabled) != 0);

    /// <summary>Initializes DosWindow and connects the supplied dependencies before the component is used.</summary>
    public DosWindow(PcMachine machine, HostSettings settings, string settingsPath, string? smokeDirectory, bool freeDosAcceptance = false, string? automaticText = null, string? captureAfter = null, string? audioOutPath = null, bool audioAcceptance = false, GuestTestScenario? scenario = null, bool resourceAcceptance = false, bool nativeMouseAcceptance = false, bool storageAcceptance = false, string? machinePath = null, string engine = "software", string? startupNotice = null, bool enableHostAudio = true)
        : base(960, 720, DOSEIdentity.DisplayName + " - " + PlatformLabel(machine.Profile))
    {
        if (startupNotice is not null) _notices.Enqueue(startupNotice);
        _session = new(machine, enableHostNetworking: true, definitionPath: machinePath,
            executionFactory: engine == "whpx" ? new WhpxExecutionFactory() : null,
            executionResolver: name => name == "whpx" ? new WhpxExecutionFactory() : throw new ArgumentException("Unknown CPU engine.")); _uiInputGeneration = _session.InputGeneration; _settings = settings with { MasterVolume = Math.Clamp(settings.MasterVolume, 0, 100),
            SpeakerVolume = Math.Clamp(settings.SpeakerVolume, 0, 100), DigitalVolume = Math.Clamp(settings.DigitalVolume, 0, 100),
            FmVolume = Math.Clamp(settings.FmVolume, 0, 100) }; _settingsPath = settingsPath;
        _showDebugOverlay = LoadDebugOverlay(settingsPath);
        _parallelVideo = LoadVideoPreference(settingsPath, "parallelVideo", false);
        _presentationHz = LoadPresentationRate(settingsPath);
        _smokeDirectory = smokeDirectory;
        _enableHostAudio = enableHostAudio;
        _freeDosAcceptance = freeDosAcceptance;
        _captureAfter = captureAfter;
        _audioOutPath = audioOutPath; _audioAcceptance = audioAcceptance;
        _automaticKeys = automaticText is null ? [] : GuestKeyboard.Encode(automaticText);
        _scenario = scenario;
        _resourceAcceptance = resourceAcceptance;
        _nativeMouseAcceptance = nativeMouseAcceptance;
        _storageAcceptance = storageAcceptance;
        if (storageAcceptance && (smokeDirectory is null || nativeMouseAcceptance || resourceAcceptance || audioAcceptance))
            throw new ArgumentException("Storage acceptance requires its own private directory and no other acceptance mode.");
        if (nativeMouseAcceptance && (smokeDirectory is null || machine.Mouse is null || resourceAcceptance || audioAcceptance))
            throw new ArgumentException("Native mouse acceptance requires a PS/2 profile and its own acceptance directory.");
        if (scenario is not null && automaticText is not null) throw new ArgumentException("Choose scenario or automatic text.");
        if (smokeDirectory is not null) Directory.CreateDirectory(smokeDirectory);
        _ipc = new(_session, instanceDirectory: Path.Combine(Path.GetDirectoryName(settingsPath)!, "ipc", "instances"));
    }

    /// <summary>Initializes renderer/session integration on the window's owning UI thread.</summary>
    public override void OnUserInitialize()
    {
        var audio = _audio = new HostAudioOutput(_audioOutPath, _enableHostAudio ? "wasapi" : "none");
        ApplyAudioLevels();
        if (audio.Fault is { } audioFault) _notices.Enqueue("AUDIO UNAVAILABLE: " + audioFault);
        PresentationVSyncEnabled = _settings.VSync;
        CursorState = CursorState.Normal;
        FocusedChanged += e => { if (!e.IsFocused) { if (_mouseInput.Captured) _nativeCapturedFocusLosses++; ReleaseMouse(); ReleaseGuestKeys(); } };
        MouseMove += e => SendMouse(_mouseInput.Move(e.DeltaX, e.DeltaY));
        MouseDown += e => NativeMouseButton(e.Button, true);
        MouseUp += e => NativeMouseButton(e.Button, false);
        if (!_session.Machine.IsRestoredSnapshot) _session.Machine.Running = true;
        _worker = Task.Run(() =>
        {
            var hostClock = Stopwatch.StartNew();
            // A small audio lead absorbs callback/batch jitter; it does not change guest rates.
            // Retain short disk/JIT stalls for real execution to catch up; each owner slice stays at most 20 ms.
            var pacing = new RealtimePacer(audio.Available ? 40_000_000UL : 0, 5_000_000_000);
            var clockSync = new HostClockSynchronizer();
            var speed = new RealtimeSpeedMeter();
            var presentation = new PresentationCadence();
            // Converts the monotonic host timestamp to nanoseconds using integer arithmetic.
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
                        bool changedMachine = !ReferenceEquals(previousMachine, machine);
                        if (!_session.UsesExternalCpu) _scenario?.Pump(machine);
                        if (!ReferenceEquals(previousMachine, machine))
                        {
                            if (previousMachine is not null) previousMachine.Audio.AudioReady -= audio.Write;
                            audio.Clear(); machine.Audio.AudioReady += audio.Write;
                            pacing.Reset(machine.Scheduler.Now, HostNanoseconds()); speed.Reset(); previousMachine = machine;
                        }
                        var levels = Volatile.Read(ref _settings);
                        clockSync.Update(_session, levels.SynchronizeClock, DateTime.Now, HostNanoseconds());
                        machine.Speaker.Volume = levels.SpeakerVolume / 100.0;
                        if (machine.SoundBlaster is { } sb) sb.Volume = levels.DigitalVolume / 100.0;
                        if (machine.SoundBlaster16 is { } sb16) sb16.Volume = levels.DigitalVolume / 100.0;
                        if (machine.Opl is { } opl) opl.Volume = levels.FmVolume / 100.0;
                        if (machine.Opl3 is { } opl3) opl3.Volume = levels.FmVolume / 100.0;
                        if (!_session.UsesExternalCpu && _automaticKeyIndex < _automaticKeys.Length && machine.Board.DebugText.Contains(machine.Profile.BootDevice == "floppy-a" ? "A:\\>" : "C:\\>", StringComparison.Ordinal)
                            && GuestKeyboard.TryInjectForBios(machine, _automaticKeys[_automaticKeyIndex])) _automaticKeyIndex++;
                        if (machine.Running && machine.Fault is null)
                        {
                            if (_session.UsesExternalCpu)
                            {
                                _session.RunRealtime(TimeSpan.FromMilliseconds(16), () =>
                                {
                                    if (presentation.Due(HostNanoseconds(), Volatile.Read(ref _presentationHz), changedMachine))
                                    {
                                        var renderer = Volatile.Read(ref _parallelVideo) ? _videoWorker ??= new JoinedWorker("DOSE video rows") : null;
                                        _frames.TryPublish((machine, renderer), static (surface, source) => source.machine.CaptureFrame(surface, source.renderer));
                                    }
                                }, _stop.Token);
                                delay = 0;
                            }
                            else
                            {
                                ulong target = pacing.Target(machine.Scheduler.Now, HostNanoseconds());
                                machine.RunSteps(200000, target);
                                delay = pacing.DelayMilliseconds(machine.Scheduler.Now, HostNanoseconds(), machine.Cpu.Halted);
                            }
                        }
                        else { audio.Clear(); pacing.Reset(machine.Scheduler.Now, HostNanoseconds()); }
                        speed.Observe(machine.Scheduler.Now, HostNanoseconds(), machine.Running && machine.Fault is null);
                        Volatile.Write(ref _realtimePercent, speed.Percent ?? double.NaN);
                        Volatile.Write(ref _guestCalendar, machine.Rtc.Calendar.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
                        Volatile.Write(ref _guestRepeatEnabled, machine.Running && machine.Fault is null && !machine.CpuResetHeld ? 1 : 0);
                        Volatile.Write(ref _status, _session.GetStatus());
                        if (!_session.UsesExternalCpu && presentation.Due(HostNanoseconds(), Volatile.Read(ref _presentationHz), changedMachine))
                        {
                            var renderer = Volatile.Read(ref _parallelVideo) ? _videoWorker ??= new JoinedWorker("DOSE video rows") : null;
                            _frames.TryPublish((machine, renderer),
                                static (surface, source) => source.machine.CaptureFrame(surface, source.renderer));
                        }
                    }
                    _stop.Token.WaitHandle.WaitOne(delay);
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _guestRepeatEnabled, 0);
                lock (_gate) Volatile.Write(ref _status, _session.GetStatus() with { State = MachineRunState.Faulted, Fault = ex.Message });
            }
        });
    }

    /// <summary>Presents the most recent leased guest frame and handles UI-only overlays/settings.</summary>
    public override void OnUserUpdate(double time)
    {
        if (Interlocked.Exchange(ref _closeRequested, 0) != 0) { Close(); return; }
        CompleteConfiguration();
        if (_settings.CpuEngine != _session.CpuEngine) { _settings = _settings with { CpuEngine = _session.CpuEngine }; SaveSettings(); }
        if (_frames.AcquireLatest() is { } nextFrame)
        {
            var previousFrame = _displayFrame;
            if (previousFrame is not null && previousFrame.Value.HasSamePixels(nextFrame.Value)) nextFrame.Dispose();
            else
            {
                _displayFrame = nextFrame; unchecked { _displayPixelRevision++; }
                Volatile.Write(ref _frame, nextFrame.Value.Frame); previousFrame?.Dispose();
            }
        }
        long observedInputGeneration = _session.InputGeneration;
        if (observedInputGeneration != Volatile.Read(ref _uiInputGeneration))
        {
            // Clear local held state without guest releases, then acknowledge exactly the epoch observed above.
            // A second replacement during/after this UI update therefore still makes our old token stale.
            _mouseInput.Release(); CursorState = CursorState.Normal; _guestKeys.Clear(); ResetKeyRepeat(true);
            Volatile.Write(ref _uiInputGeneration, observedInputGeneration);
        }
        while (_notices.TryDequeue(out string? notice)) { _notice = notice; _noticeUntil = _elapsed.ElapsedMilliseconds + 8000; }
        if (_ipc.Fault is not null) _notice = "LOCAL IPC UNAVAILABLE: " + _ipc.Fault;
        if (SourceUploadError != ErrorCode.NoError)
        {
            string uploadNotice = "VIDEO OUTPUT ERROR: " + SourceUploadError;
            if (_notice != uploadNotice) { _notice = uploadNotice; _noticeUntil = _elapsed.ElapsedMilliseconds + 8000; }
        }
        if (_smokeDirectory is not null) AdvanceSmokeTest();
        var frame = Volatile.Read(ref _frame);
        var status = Volatile.Read(ref _status);
        bool hasGlide = TryCaptureGlideFrame();
        if (frame is not null || hasGlide)
        {
            int sourceWidth = hasGlide ? _glideWidth : frame!.Width;
            int sourceHeight = hasGlide ? _glideHeight : frame!.Height;
            double aspect = _settings.CorrectAspect ? 4.0 / 3 : (double)sourceWidth / sourceHeight;
            int width = Math.Min(PixelsWidth, (int)(GuestAreaHeight * aspect));
            int height = (int)(width / aspect);
            _ = Enum.TryParse(_settings.Filter, out SharpPixelsGpuFilterMode filter);
            _ = Enum.TryParse(_settings.Upscale, out SharpPixelsGpuUpscaleMode upscale);
            int factor = upscale switch { SharpPixelsGpuUpscaleMode.Scale2x => 2, SharpPixelsGpuUpscaleMode.Scale3x => 3, _ => 1 };
            uint[] pixels = hasGlide ? _glidePixels : frame!.ArgbPixels;
            DrawArgbPixelsGpuPresented(pixels, sourceWidth, sourceHeight, 0, 0, sourceWidth, sourceHeight,
                (PixelsWidth - width) / 2, (GuestAreaHeight - height) / 2, width, height, filter, upscale, factor, 8, 10, 21,
                hasGlide ? _glideRevision : _displayPixelRevision);
        }
        else ClearScreen();
        if (_showDebugOverlay)
        {
            DrawFilledRectangle(0, PixelsHeight - 38, PixelsWidth, 38, 12, 15, 29);
            Text(15, PixelsHeight - 31, $"{PlatformLabel(status?.Configuration).ToUpperInvariant()} | {status?.CpuEngine.ToUpperInvariant()} | {status?.State.ToString().ToUpperInvariant() ?? "STARTING"} | {(hasGlide ? "MINIGLIDE" : "VGA")} {(hasGlide ? _glideWidth : frame?.Width)}X{(hasGlide ? _glideHeight : frame?.Height)} | {status?.Position ?? "RESET"} | {FPS} FPS", 1, Color.LightSteelBlue);
            Text(15, PixelsHeight - 16, _mouseInput.Captured ? "MOUSE CAPTURED / CTRL+ALT RELEASE   CTRL+ALT+F10 MENU   CTRL+ALT+F11 FULLSCREEN" :
                status?.Configuration?.Mouse == "ps2-two-button" ? "CTRL+ALT+F10 MENU   CTRL+ALT+F11 FULLSCREEN   CLICK GUEST FOR PS/2 MOUSE" :
                _session.UsesExternalCpu ? "CTRL+ALT+F10 MENU   CTRL+ALT+F11 FULLSCREEN   WHPX / 1:1 CLOCK" : "CTRL+ALT+F10 MENU   CTRL+ALT+F11 FULLSCREEN   EXPERIMENTAL REAL-MODE DOS", 1, Color.Gray);
        }
        if (status?.Fault is not null && _page.Length == 0)
        {
            DrawFilledRectangleWithAlpha(20, 18, PixelsWidth - 40, 80, 30, 10, 20, 235);
            Text(32, 28, "EMULATION STOPPED", 2, Color.Salmon);
            Text(32, 57, status.Fault.Length > 112 ? status.Fault[..112] : status.Fault, 1, Color.White);
        }
        if (_page.Length > 0) { RebuildItems(); DrawMenu(status); }
        else if (Volatile.Read(ref _machineOperationPending) > 0 || _elapsed.ElapsedMilliseconds < _noticeUntil)
        {
            string message = Volatile.Read(ref _machineOperationPending) > 0 ? "LOADING PC / PLEASE WAIT..." : _notice;
            int y = status?.Fault is null ? 12 : 110;
            DrawFilledRectangleWithAlpha(16, y, PixelsWidth - 32, 28, 12, 15, 29, 235);
            Text(24, y + 9, message.Length > 135 ? message[..132] + "..." : message, 1, Color.LightSteelBlue);
        }
        else if (_notice.Length > 0)
        {
            int footer = _showDebugOverlay ? 38 : 0;
            DrawFilledRectangleWithAlpha(0, PixelsHeight - footer - 23, PixelsWidth, 23, 35, 15, 20, 240);
            Text(15, PixelsHeight - footer - 16, _notice.Length > 140 ? _notice[..140] : _notice, 1, Color.Salmon);
        }
        double speedPercent = Volatile.Read(ref _realtimePercent);
        string speedLabel = double.IsNaN(speedPercent) ? "SPEED --" : $"SPEED {speedPercent:0}%";
        Title = $"{DOSEIdentity.DisplayName} - {PlatformLabel(status?.Configuration)} | {status?.CpuEngine.ToUpperInvariant()} | {status?.State} | {speedLabel} | {FPS} FPS";
    }

    /// <summary>Renders the active keyboard-driven overlay and its published state without mutating guest hardware.</summary>
    private void DrawMenu(MachineStatus? status)
    {
        const int x = SettingsMenuLayout.Left, y = SettingsMenuLayout.Top, width = SettingsMenuLayout.Width, height = SettingsMenuLayout.Height;
        DrawFilledRectangleWithAlpha(x + 8, y + 8, width, height, 0, 0, 0, 140);
        DrawFilledRectangleWithAlpha(x, y, width, height, 9, 12, 29, 246);
        DrawLine(x, y, x + width, y, Color.SlateBlue); DrawLine(x, y + height, x + width, y + height, Color.Cyan);
        DrawLine(x, y, x, y + height, Color.SlateBlue); DrawLine(x + width, y, x + width, y + height, Color.Cyan);
        Text(x + 22, y + 24, DOSEIdentity.Name + " / " + _page + (_configurationDraft?.HasChanges == true ? " *" : ""), 2, Color.White);
        Text(x + 22, y + 58, "ENTER / CLICK CHANGE   LEFT/RIGHT ADJUST   PAGE UP/DOWN SCROLL   ESC BACK", 1, Color.LightSteelBlue);
        int first = SettingsMenuLayout.First(_items.Length, _selected);
        for (int i = first; i < Math.Min(_items.Length, first + SettingsMenuLayout.VisibleRows); i++)
        {
            int rowY = SettingsMenuLayout.RowTop + (i - first) * SettingsMenuLayout.RowHeight;
            if (i == _selected) DrawFilledRectangleWithAlpha(x + 12, rowY - 6, width - 24, 29, 53, 51, 85, 220);
            Text(x + 22, rowY, (i == _selected ? "> " : "  ") + SettingsMenuLayout.Label(_items[i]), 2, i == _selected ? Color.LightGoldenrodYellow : Color.LightSteelBlue);
        }
        Text(x + 22, y + height - 100, $"ROWS {first + 1}-{Math.Min(_items.Length, first + SettingsMenuLayout.VisibleRows)} / {_items.Length}   " +
            (_configurationDraft?.HasChanges == true ? "PENDING CHANGES / APPLY TO RESTART" : ""), 1, Color.Gray);
        if (_selected >= 0 && _selected < _items.Length) Text(x + 22, y + height - 81, _items[_selected].Length > 135 ? _items[_selected][..132] + "..." : _items[_selected], 1, Color.LightSteelBlue);
        Text(x + 22, y + height - 61, _notice.Length > 135 ? _notice[..132] + "..." : _notice, 1, Color.Khaki);
        int? selectedSlot = _page == "HARD DISKS" && _selected < 4 ? _selected :
            _page.StartsWith("IDE ", StringComparison.Ordinal) && _page.Length == 5 ? _page[^1] - '0' : null;
        string media = selectedSlot is { } slot ? $"IDE {slot}: {status?.HardDisks?.FirstOrDefault(d => d.Slot == slot)?.Name ?? "EMPTY"}" : $"IDE: {status?.MediaName ?? "NONE"}";
        Text(x + 22, y + height - 38, media.Length > 110 ? media[..107] + "..." : media, 1, Color.LightSteelBlue);
        Text(x + 22, y + height - 21, $"STATE: {status?.State}   PROFILE: {PlatformLabel(status?.Configuration).ToUpperInvariant()}", 1, Color.Gray);
    }

    /// <summary>Draws bounded bitmap UI text using the original project font.</summary>
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

    /// <summary>Opens an overlay page, pausing guest execution and releasing guest input capture on the first open.</summary>
    private void OpenPage(string page)
    {
        ResetKeyRepeat();
        bool wasClosed = _page.Length == 0;
        if (page is "MACHINE / HARDWARE" or "CPU / PLATFORM" or "SOUND CARD RESOURCES" or "NETWORK" || page.StartsWith("NETWORK / ADAPTER ", StringComparison.Ordinal)) BeginConfiguration();
        _page = page; _selected = 0; _notice = ""; RebuildItems();
        if (wasClosed) { ReleaseMouse(); ReleaseGuestKeys(); Command("Pause"); _audio?.Clear(); }
    }
    /// <summary>Derives the current overlay rows from published machine status and host presentation settings.</summary>
    private void RebuildItems()
    {
        var status = Volatile.Read(ref _status);
        var frame = Volatile.Read(ref _frame);
        var profile = _configurationDraft?.Requested ?? status?.Configuration ?? new MachineProfile();
        if ((_page == "NETWORK" || _page.StartsWith("NETWORK / ADAPTER ", StringComparison.Ordinal)) &&
            Environment.TickCount64 >= Interlocked.Read(ref _networkRefreshAt)) RefreshNetworkStatus();
        _items = _page switch
        {
        "MAIN MENU" => ["CONTINUE", "VIDEO SETTINGS", "MACHINE / HARDWARE", "MEDIA", "NETWORK ADAPTERS", "AUDIO SETTINGS", "RESET / POWER", "SAVED MACHINES", "TIME AND SPEED", "EXIT"],
        "UNSAVED HARDWARE CHANGES" => ["APPLY CHANGES / COLD RESTART", "DISCARD CHANGES / CONTINUE", "KEEP EDITING"],
        "CPU / PLATFORM" => ["PLATFORM: " + (profile.IsP55c ? "PENTIUM MMX / OWN FX + PIIX + S3 BOARD" : profile.IsWindows95Board ? "PENTIUM PCI / 430FX + PIIX + S3" : profile.IsFxHost ? "FIXED CHIPSET TEST BOARD" : profile.IsP54c ? "PENTIUM ISA AT" : "486 ISA AT"),
            $"CPU CLOCK: {profile.CpuHz / 1_000_000} MHZ (SUPPORTED TUPLES)", $"RAM: {profile.RamMiB} MIB", "BOARD CHANGE CAN REQUIRE NEW GUEST DRIVERS",
            "DETACH CD MEDIA / PCI CARDS BEFORE AN ISA BOARD", "BACK"],
        "NETWORK HELP" => ["NAT: DHCP / GATEWAY 10.0.2.2 / DNS 10.0.2.3", "ADAPTER 2 NAT USES 10.0.3.0/24",
            "HOST-ONLY: SAME LAN NAME / DHCP / NO INTERNET", "BRIDGED: NPCAP + EXPLICIT HOST ADAPTER REQUIRED", "WINDOWS: INSTALL NIC DRIVER + TCP/IP / DHCP",
            "WIN95 A: USE THE PROVIDED ORIGINAL RTL8029 DRIVER", "WIN98: USE ITS ORIGINAL WINDOWS DRIVER / CD", "INSERT NETWORK DRIVER DISK...", "INSERT DOSE APM POWER MANAGEMENT DISK", "BACK"],
        "TIME AND SPEED" => [$"HOST CLOCK SYNC: {(_settings.SynchronizeClock ? "ON" : "OFF")}",
            "HOST: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            "RTC:  " + Volatile.Read(ref _guestCalendar),
            "REALTIME TARGET: 100% / LIVE SPEED IN WINDOW TITLE",
            "RESTORE / RESUME: CURRENT HOST LOCAL TIME", "WINDOWS CLOCK SYNC REQUIRES DOSE TOOLS", $"CPU ENGINE: {_session.CpuEngine.ToUpperInvariant()} (COLD RESTART)", "BACK"],
        "SAVED MACHINES" => ["SAVE THIS PC...", "LOAD PC... (COLD START)", "SAVE FULL SNAPSHOT...", "LOAD FULL SNAPSHOT...", "PC FILES COLD START / SNAPSHOTS RESUME", "BACK"],
        "OPTICAL DRIVE" => [status?.OpticalDisc is { } cd ? $"IDE {cd.Slot}: {ShortName(cd.Name)} / {cd.Bytes / 1048576m:0.##} MIB" : "NO PACKET TARGET INSTALLED",
            "MOUNT READ-ONLY ISO... (RESTART)", "REMOVE ISO (RESTART)", "BACK"],
        "HARD DISKS" => [HardDiskLabel(status, 0), HardDiskLabel(status, 1), HardDiskLabel(status, 2), HardDiskLabel(status, 3), "CREATE BLANK DISK IMAGE...", "BACK"],
        "IDE 0" or "IDE 1" or "IDE 2" or "IDE 3" => [HardDiskLabel(status, _page[^1] - '0'), "ATTACH READ-ONLY IMAGE... (RESTART)",
            "ATTACH WITH PRIVATE OVERLAY... (RESTART)", "DETACH DISK (RESTART)", "BACK"],
        "RESET / POWER" => ["SEND CTRL+ALT+DEL TO GUEST", "HARDWARE RESET / CPU, RAM RETAINED", "POWER CYCLE / RAM CLEARED", "BACK"],
        "VIDEO SETTINGS" => [$"FILTER          {_settings.Filter.ToUpperInvariant()}", $"UPSCALE         {_settings.Upscale.ToUpperInvariant()}",
            $"ASPECT          {(_settings.CorrectAspect ? "4:3" : "SOURCE PIXELS")}", $"VSYNC           {(_settings.VSync ? "ON" : "OFF")}",
            $"DEBUG OVERLAY   {(_showDebugOverlay ? "ON" : "OFF")}", $"MULTICORE VIDEO {(_parallelVideo ? "ON" : "OFF")}", $"HOST FRAME CAP  {_presentationHz} HZ (GUEST TIME UNCHANGED)", "BACK"],
        "MACHINE / HARDWARE" => [profile.IsP55c ? $"CPU: PENTIUM MMX {profile.CpuHz / 1_000_000} MHZ / 166, 200 OR 233 (APPLY)" : profile.IsWindows95Board ? $"CPU: PENTIUM {profile.CpuHz / 1_000_000} MHZ / 100 OR 133 (APPLY)" : profile.IsTrio64 ? "CPU: FX/PIIX/S3 / P54C 100 MHZ (FIXED)" : profile.IsPiixReset ? "CPU: FX/PIIX RESET / P54C 100 MHZ (FIXED)" : profile.IsPiixIde ? "CPU: FX/PIIX IDE / P54C 100 MHZ (FIXED)" : profile.IsPiixIrq ? "CPU: FX/PIIX IRQ / P54C 100 MHZ (FIXED)" : profile.IsFxHost ? "CPU: 430FX HOST / P54C 100 MHZ (FIXED)" : profile.IsP54c ? "CPU: DOSE AT / P54C B5 SUBSET 90 MHZ (RESTART)" : "CPU: 486DX / 33 MHZ (RESTART)", $"RAM: {profile.RamMiB} MIB ({(profile.GetSupportedRamMiB().Length == 1 ? "FIXED HOST BOARD" : "CHANGE RESTARTS")})",
            $"FLOPPY A: {profile.FloppyDriveA.ToUpperInvariant()} (CHANGE RESTARTS)", $"FLOPPY B: {profile.FloppyDriveB.ToUpperInvariant()} (CHANGE RESTARTS)",
            $"BOOT: {profile.BootDevice.ToUpperInvariant()} (CHANGE RESTARTS)",
            profile.SoundCard switch { "adlib-opl2-development" => "SOUND: ADLIB OPL2 MELODIC DEV (RESTART)",
                "sb2-opl2-development" => $"SOUND: SB2+OPL2 A{profile.SoundBlasterBasePort:X3} I{profile.SoundBlasterIrq} D1 (RESTART)",
                "sb16-ct1740-dsp405-development" => "SOUND: SB16 CT1740 / PCM+OPL3 DEV (RESTART)",
                "sb2-dsp-development" => $"SOUND: SB2 DSP A{profile.SoundBlasterBasePort:X3} I{profile.SoundBlasterIrq} D1 (RESTART)", _ => "SOUND: SPEAKER (CHANGE RESTARTS)" },
            "SOUND CARD RESOURCES...",
            $"MOUSE: {profile.Mouse.ToUpperInvariant()} (CHANGE RESTARTS)",
            $"VIDEO: {(profile.IsTrio64 ? "S3 TRIO64 PCI 2M SUBSET (FIXED)" : profile.IsEt4000 ? "ET4000AX 1M DEV (CHANGE RESTARTS)" : "DOSE VGA (CHANGE RESTARTS)")}",
            $"GAMEPORT: {(profile.Gameport == "none" ? "NONE" : "IBM ANALOG / VIRTUAL INPUT")} (RESTART)",
            $"COM1: {(profile.SerialPort == "none" ? "NONE" : "PC16550D / VIRTUAL INPUT")} (RESTART)",
            $"LPT1: {(profile.ParallelPort == "none" ? "NONE" : "IBM PRINTER / VIRTUAL PINS")} (RESTART)",
            $"MIDI: {(profile.MidiInterface == "none" ? "NONE" : "CT1740 MPU-UART / VIRTUAL")} (RESTART)", "NETWORK ADAPTERS...", "APPLY CHANGES / COLD RESTART", "DISCARD CHANGES",
            $"3D ACCELERATOR: {(profile.IsVoodoo ? "VOODOO GRAPHICS 4M / DEVELOPMENT" : "NONE")} (APPLY)", "BACK"],
        "SOUND CARD RESOURCES" => profile.IsSb16
            ? [$"SB16 PORT: {profile.SoundBlasterBasePort:X3}H (CHANGE RESTARTS)",
                $"SB16 IRQ: {profile.SoundBlasterIrq} (CHANGE RESTARTS)",
                $"SB16 DMA8: {profile.SoundBlasterDmaChannel} (CHANGE RESTARTS)",
                $"SB16 DMA16: {profile.SoundBlasterDma16Channel} (CHANGE RESTARTS)",
                $"MPU PORT: {profile.MidiBasePort:X3}H (CHANGE RESTARTS)",
                $"DOS: SET BLASTER={profile.GetBlasterEnvironment()}", "PCM / OPL3 / OPTIONAL MPU UART; FULL CARD PENDING", "BACK"]
            : profile.SoundCard is "sb2-dsp-development" or "sb2-opl2-development"
            ? [$"SB2 PORT: {profile.SoundBlasterBasePort:X3}H (CHANGE RESTARTS)",
                $"SB2 IRQ: {profile.SoundBlasterIrq} (CHANGE RESTARTS)", "SB2 DMA: 1 (FIXED ON THIS CARD)",
                $"DOS: SET BLASTER={profile.GetBlasterEnvironment()}", "BACK"]
            : ["SELECT AN SB2 OR SB16 CARD IN HARDWARE FIRST", "BACK"],
        "MEDIA" => [status?.DiskWritable == true ? "IDE: WRITES TO PRIVATE OVERLAY" : "IDE: READ-ONLY", "OPEN RAW IDE IMAGE... (READ-ONLY)", "OPEN IDE + PRIVATE OVERLAY...",
            $"FLOPPY A: {ShortName(status?.FloppyAMedia)}", $"FLOPPY B: {ShortName(status?.FloppyBMedia)}", "BOOT BUILT-IN TEST SECTOR", "HARD DISKS / CREATE IMAGE...", "OPTICAL DRIVE...", "BACK"],
        "FLOPPY A" or "FLOPPY B" => [$"DRIVE TYPE: {(_page == "FLOPPY A" ? profile.FloppyDriveA : profile.FloppyDriveB).ToUpperInvariant()}",
            "INSERT READ-ONLY IMAGE...", "INSERT + PRIVATE OVERLAY...", "EJECT MEDIUM", "BACK"],
        "AUDIO SETTINGS" => [_audio?.Available == true ? "OUTPUT: WINDOWS DEFAULT / 48 KHZ STEREO" : "OUTPUT: UNAVAILABLE",
            $"MUTE            {(_settings.AudioMuted ? "ON" : "OFF")}", $"MASTER          {_settings.MasterVolume}%", $"PC SPEAKER      {_settings.SpeakerVolume}%",
            $"DIGITAL PCM     {_settings.DigitalVolume}%", $"FM SYNTH        {_settings.FmVolume}% (DEVELOPMENT)", "BACK"],
        "NETWORK" => NetworkItems(profile),
        "NETWORK / ADAPTER 1" or "NETWORK / ADAPTER 2" => NetworkAdapterItems(profile, _page[^1] - '1'),
        _ => ["GUEST NIC: NOT IMPLEMENTED", "REMOTE PLAY: NOT IMPLEMENTED", "BACK"]
        };
    }
    /// <summary>Bounds and normalizes a media name for the overlay's fixed-width display.</summary>
    private static string ShortName(string? name) => name is null ? "EMPTY" : name.Length > 38 ? name[..35] + "..." : name.ToUpperInvariant();
    /// <summary>Closes the overlay and requests guest execution to resume through the command queue.</summary>
    private void CloseMenu()
    {
        if (_configurationApplying) { _notice = "WAIT FOR THE CURRENT APPLY REQUEST"; return; }
        if (_configurationDraft?.HasChanges == true) { OpenPage("UNSAVED HARDWARE CHANGES"); return; }
        _configurationDraft = null; ResetKeyRepeat(true); _page = ""; Command("Resume");
    }
    /// <summary>Submits the requested operation through the component's normal command interface.</summary>
    private void Command(string command, object? payload = null)
    {
        bool topology = command is "SetCpuEngine" or "SetOpticalDisc" or "SetHardDisk" or "LoadMachineDefinition" or "LoadSnapshot" or "CommitConfigurationChange" or "PowerCycleReadOnlyDisk" or "PowerCycleOverlayDisk";
        if (topology) Interlocked.Increment(ref _machineOperationPending);
        _ = _session.Enqueue(IpcProtocol.CreateRequest(command, payload), Volatile.Read(ref _uiInputGeneration))
            .ContinueWith(task =>
            {
                if (topology) Interlocked.Decrement(ref _machineOperationPending);
                if (task.IsCompletedSuccessfully && !task.Result.Success) _notices.Enqueue($"{task.Result.ErrorCode}: {task.Result.Payload}");
                else if (task.IsCompletedSuccessfully && command == "SaveMachineDefinition") _notices.Enqueue("PC CONFIGURATION SAVED");
                else if (task.IsCompletedSuccessfully && command == "CreateSnapshot") _notices.Enqueue("FULL SNAPSHOT SAVED");
                else if (task.IsCompletedSuccessfully && command == "LoadSnapshot") _notices.Enqueue("FULL SNAPSHOT RESTORED");
            }, TaskScheduler.Default);
    }

    /// <summary>Applies the selected overlay action, routing hardware/media changes through the session owner.</summary>
    private void Activate(int delta = 1)
    {
        if (_configurationApplying) { _notice = "APPLY IN PROGRESS"; return; }
        if (_configurationDraft?.HasChanges == true && _page == "TIME AND SPEED" && _selected == 6)
        { OpenPage("UNSAVED HARDWARE CHANGES"); return; }
        if (_configurationDraft?.HasChanges == true && (_page == "RESET / POWER" || _page == "SAVED MACHINES" && _selected is 0 or 1 or 3 ||
            _page == "MEDIA" && _selected is 1 or 2 or 5 || _page.StartsWith("IDE ", StringComparison.Ordinal) && _selected is 1 or 2 or 3 ||
            _page == "OPTICAL DRIVE" && _selected is 1 or 2 || _page is "FLOPPY A" or "FLOPPY B" && _selected is 1 or 2 or 3 ||
            _page == "NETWORK HELP" && _selected is 7 or 8))
        { _notice = "APPLY OR DISCARD PENDING HARDWARE BEFORE CHANGING MEDIA OR RESTARTING"; return; }
        if (_page == "UNSAVED HARDWARE CHANGES")
        {
            if (_selected == 0) ApplyConfiguration();
            else if (_selected == 1) { DiscardConfiguration(); CloseMenu(); }
            else OpenPage("MACHINE / HARDWARE");
        }
        else if (_page == "MAIN MENU")
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
                case 7: OpenPage("SAVED MACHINES"); break;
                case 8: OpenPage("TIME AND SPEED"); break;
                case 9: Close(); break;
            }
        }
        else if (_page == "TIME AND SPEED")
        {
            if (_selected == 0) { _settings = _settings with { SynchronizeClock = !_settings.SynchronizeClock }; SaveSettings(); }
            else if (_selected == 6) Command("SetCpuEngine", new { engine = _session.CpuEngine == "whpx" ? "software" : "whpx" });
            else if (_selected == 7) OpenPage("MAIN MENU");
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
            // Selects the adjacent supported setting value with bounded wraparound.
            string Cycle(string current, string[] values) => values[(Array.IndexOf(values, current) + delta + values.Length) % values.Length];
            switch (_selected)
            {
                case 0: _settings = _settings with { Filter = Cycle(_settings.Filter, ["Sharp", "Crt", "Tv"]) }; break;
                case 1: _settings = _settings with { Upscale = Cycle(_settings.Upscale, ["None", "Scale2x", "Scale3x"]) }; break;
                case 2: _settings = _settings with { CorrectAspect = !_settings.CorrectAspect }; break;
                case 3: _settings = _settings with { VSync = !_settings.VSync }; PresentationVSyncEnabled = _settings.VSync; break;
                case 4: _showDebugOverlay = !_showDebugOverlay; break;
                case 5: Volatile.Write(ref _parallelVideo, !Volatile.Read(ref _parallelVideo)); break;
                case 6: Volatile.Write(ref _presentationHz, DosWindow.Cycle(new[] { 30, 60, 120 }, _presentationHz, delta)); break;
                case 7: OpenPage("MAIN MENU"); return;
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
            else if (_selected == 6) OpenPage("HARD DISKS");
            else if (_selected == 7) OpenPage("OPTICAL DRIVE");
            else if (_selected == 8) OpenPage("MAIN MENU");
        }
        else if (_page == "OPTICAL DRIVE")
        {
            if (_selected == 3) OpenPage("MEDIA");
            else if (Volatile.Read(ref _status)?.Configuration?.IsAtapi != true) _notice = "LOAD THE ATAPI BOARD PROFILE FIRST";
            else if (_selected == 1)
            {
                using var image = new OpenFileDialog { Title = "Read-only cooked CD-ROM image (2048-byte blocks)", Filter = "Cooked ISO (*.iso)|*.iso" };
                if (image.ShowDialog() != DialogResult.OK) return;
                Command("SetOpticalDisc", new { path = Path.GetFullPath(image.FileName) }); CloseMenu();
            }
            else if (_selected == 2) { Command("SetOpticalDisc", new { }); CloseMenu(); }
        }
        else if (_page == "SAVED MACHINES")
        {
            if (_selected == 0)
            {
                string currentName;
                lock (_gate) currentName = _session.Machine.CaptureDefinition().Name;
                string? name = StorageDialogs.AskMachineName(currentName);
                if (name is null) return;
                using var dialog = new SaveFileDialog { Title = "Save machine", Filter = "DOS-PC (*.dospc.json)|*.dospc.json", DefaultExt = "dospc.json",
                    FileName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".dospc.json", InitialDirectory = Path.GetDirectoryName(_settingsPath) };
                if (dialog.ShowDialog() == DialogResult.OK) { Command("SaveMachineDefinition", new { path = dialog.FileName, name }); _notice = "PC CONFIGURATION SAVE REQUESTED"; }
            }
            else if (_selected == 1)
            {
                using var dialog = new OpenFileDialog { Title = "Load machine", Filter = "DOS-PC (*.dospc.json;*.json)|*.dospc.json;*.json" };
                if (dialog.ShowDialog() == DialogResult.OK) { Command("LoadMachineDefinition", new { path = dialog.FileName }); CloseMenu(); }
            }
            else if (_selected == 2)
            {
                using var dialog = new SaveFileDialog { Title = "Create full DOSE snapshot (new folder)", Filter = _session.UsesExternalCpu ? "WHPX Snapshot (*.whpxsav)|*.whpxsav" : "DOSE Snapshot (*.dossav)|*.dossav",
                    DefaultExt = _session.UsesExternalCpu ? "whpxsav" : "dossav", FileName = "Snapshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + (_session.UsesExternalCpu ? ".whpxsav" : ".dossav"), InitialDirectory = Path.GetDirectoryName(_settingsPath) };
                if (dialog.ShowDialog() == DialogResult.OK) { Command("CreateSnapshot", new { path = dialog.FileName }); _notice = "FULL SNAPSHOT SAVE REQUESTED"; }
            }
            else if (_selected == 3)
            {
                using var source = new FolderBrowserDialog { Description = _session.UsesExternalCpu ? "Select WHPX snapshot folder (.whpxsav)" : "Select DOSE snapshot folder (.dossav)", UseDescriptionForTitle = true, ShowNewFolderButton = false };
                if (source.ShowDialog() != DialogResult.OK) return;
                string parent = Path.GetDirectoryName(source.SelectedPath)!;
                string destination = Path.Combine(parent, "Resume-" + Guid.NewGuid().ToString("N") + ".dosebranch");
                Command("LoadSnapshot", new { path = source.SelectedPath, destination });
                _notice = "FULL SNAPSHOT LOAD REQUESTED";
            }
            else if (_selected == 5) OpenPage("MAIN MENU");
        }
        else if (_page == "HARD DISKS")
        {
            if (_selected < 4) OpenPage("IDE " + _selected);
            else if (_selected == 4) CreateDiskImage();
            else OpenPage("MEDIA");
        }
        else if (_page is "IDE 0" or "IDE 1" or "IDE 2" or "IDE 3")
        {
            int slot = _page[^1] - '0';
            if (_selected is 1 or 2) AttachDiskImage(slot, _selected == 2);
            else if (_selected == 3) { Command("SetHardDisk", new { slot }); CloseMenu(); }
            else if (_selected == 4) OpenPage("HARD DISKS");
        }
        else if (_page == "MACHINE / HARDWARE")
        {
            // Cycles only the enumerated supported physical floppy geometries.
            string CycleDrive(string current)
            {
                string[] values = ["none", "360k", "720k", "1200k", "1440k", "2880k"];
                return values[(Array.IndexOf(values, current) + delta + values.Length) % values.Length];
            }
            switch (_selected)
            {
                case 0: OpenPage("CPU / PLATFORM"); break;
                case 1: ChangeProfile(p => { var values = p.GetSupportedRamMiB(); if (values.Length == 1) return p; return p with { RamMiB = values[(values.IndexOf(p.RamMiB) + delta + values.Length) % values.Length] }; }); break;
                case 2: ChangeProfile(p => p with { FloppyDriveA = CycleDrive(p.FloppyDriveA) }); break;
                case 3: ChangeProfile(p => p with { FloppyDriveB = CycleDrive(p.FloppyDriveB) }); break;
                case 4: ChangeProfile(p => p with { BootDevice = p.BootDevice == "ide" ? "floppy-a" : "ide" }); break;
                case 5: ChangeProfile(p =>
                {
                    string[] cards = ["none", "adlib-opl2-development", "sb2-dsp-development", "sb2-opl2-development", "sb16-ct1740-dsp405-development"];
                    string card = cards[(Array.IndexOf(cards, p.SoundCard) + delta + cards.Length) % cards.Length];
                    bool sb16 = card == "sb16-ct1740-dsp405-development";
                    return p with { SoundCard = card,
                        SoundBlasterBasePort = sb16 || p.SoundBlasterBasePort is 0x220 or 0x240 ? p.SoundBlasterBasePort : 0x220,
                        SoundBlasterIrq = sb16 ? (p.SoundBlasterIrq == 3 ? 5 : p.SoundBlasterIrq) : (p.SoundBlasterIrq == 10 ? 5 : p.SoundBlasterIrq),
                        SoundBlasterDmaChannel = sb16 ? p.SoundBlasterDmaChannel : 1, MidiInterface = sb16 ? p.MidiInterface : "none" };
                }); break;
                case 6: OpenPage("SOUND CARD RESOURCES"); break;
                case 7: ChangeProfile(p => p with { Mouse = p.Mouse == "none" ? "ps2-two-button" : "none" }); break;
                case 8:
                    if ((_configurationDraft?.Requested ?? Volatile.Read(ref _status)?.Configuration)?.IsTrio64 == true)
                        _notice = "THIS PCI BOARD USES S3 TRIO64 / CHANGE DISPLAY RESOLUTION INSIDE WINDOWS";
                    else ChangeProfile(p => p with { VideoCard = p.IsEt4000 ? "dose-vga-development" : "et4000ax-isa-1m-development" });
                    break;
                case 9: ChangeProfile(p => p with { Gameport = p.Gameport == "none" ? "ibm-game-control-development" : "none" }); break;
                case 10: ChangeProfile(p => p with { SerialPort = p.SerialPort == "none" ? "pc16550d-com1-development" : "none" }); break;
                case 11: ChangeProfile(p => p with { ParallelPort = p.ParallelPort == "none" ? "ibm-printer-adapter-development" : "none" }); break;
                case 12:
                    if ((_configurationDraft?.Requested ?? Volatile.Read(ref _status)?.Configuration)?.IsSb16 == true)
                        ChangeProfile(p => p with { MidiInterface = p.MidiInterface == "none" ? "ct1740-mpu401-uart-development" : "none" });
                    else _notice = "SELECT SB16 CT1740 BEFORE ENABLING ITS MPU UART";
                    break;
                case 13: OpenPage("NETWORK"); break;
                case 14: ApplyConfiguration(); break;
                case 15: DiscardConfiguration(); break;
                case 16:
                    if ((_configurationDraft?.Requested ?? Volatile.Read(ref _status)?.Configuration)?.IsWindows95Board == true)
                        ChangeProfile(p => p with { ThreeDAccelerator = p.IsVoodoo ? "none" : MachineProfile.VoodooGraphicsId });
                    else _notice = "SELECT THE PCI/S3 WINDOWS BOARD BEFORE ADDING VOODOO";
                    break;
                case 17: OpenPage("MAIN MENU"); break;
            }
        }
        else if (_page == "CPU / PLATFORM")
        {
            if (_selected == 0) ChangeProfile(profile => MachineSettingsDraft.CyclePlatform(profile, delta));
            else if (_selected == 1) ChangeProfile(profile => MachineSettingsDraft.CycleCpu(profile, delta));
            else if (_selected == 2) ChangeProfile(profile => { var values = profile.GetSupportedRamMiB(); return profile with { RamMiB = values[(values.IndexOf(profile.RamMiB) + delta + values.Length) % values.Length] }; });
            else if (_selected == 5) OpenPage("MACHINE / HARDWARE");
        }
        else if (_page == "SOUND CARD RESOURCES")
        {
            if (_selected == _items.Length - 1) OpenPage("MACHINE / HARDWARE");
            else if ((_configurationDraft?.Requested ?? Volatile.Read(ref _status)?.Configuration)?.IsSb16 == true && _selected < 5)
            {
                ChangeProfile(p => _selected switch
                {
                    0 => p with { SoundBlasterBasePort = Cycle(new[] { 0x220, 0x240, 0x260, 0x280 }, p.SoundBlasterBasePort, delta) },
                    1 => p with { SoundBlasterIrq = Cycle(new[] { 5, 7, 9, 10 }, p.SoundBlasterIrq, delta) },
                    2 => p with { SoundBlasterDmaChannel = Cycle(new[] { 0, 1, 3 }, p.SoundBlasterDmaChannel, delta) },
                    3 => p with { SoundBlasterDma16Channel = Cycle(new[] { 5, 6, 7 }, p.SoundBlasterDma16Channel, delta) },
                    4 => p with { MidiBasePort = p.MidiBasePort == 0x330 ? 0x300 : 0x330 },
                    _ => p
                });
            }
            else if ((_configurationDraft?.Requested ?? Volatile.Read(ref _status)?.Configuration)?.SoundCard is "sb2-dsp-development" or "sb2-opl2-development")
            {
                if (_selected == 0) ChangeProfile(p => p with { SoundBlasterBasePort = p.SoundBlasterBasePort == 0x220 ? 0x240 : 0x220 });
                else if (_selected == 1) ChangeProfile(p =>
                {
                    int[] irqs = [3, 5, 7, 9];
                    return p with { SoundBlasterIrq = irqs[(Array.IndexOf(irqs, p.SoundBlasterIrq) + delta + irqs.Length) % irqs.Length] };
                });
            }
        }
        else if (_page == "NETWORK")
        {
            if (_selected is 0 or 1) OpenPage("NETWORK / ADAPTER " + (_selected + 1));
            else if (_selected == 2) RefreshNetworkStatus();
            else if (_selected == 4) OpenPage("NETWORK HELP");
            else if (_selected == 5) ApplyConfiguration();
            else if (_selected == 6) DiscardConfiguration();
            else if (_selected == 7) OpenPage("MAIN MENU");
        }
        else if (_page == "NETWORK HELP")
        {
            if (_selected == 7)
            {
                using var disk = new OpenFileDialog { Title = "Select the NIC driver data disk for your Windows version", Filter = "Driver disk (*.img)|*.img", InitialDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "driver-media")) };
                if (disk.ShowDialog() == DialogResult.OK) Command("InsertFloppy", new { drive = 0, path = disk.FileName });
            }
            else if (_selected == 8)
            {
                string disk = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "guest-tools", "windows95", "dose-tools-apm.img"));
                Command("InsertFloppy", new { drive = 0, path = disk });
                _notice = "APM DATA DISK IN A: / OPEN README.TXT INSIDE WINDOWS";
            }
            else if (_selected == 9) OpenPage("NETWORK");
        }
        else if (_page is "NETWORK / ADAPTER 1" or "NETWORK / ADAPTER 2") ActivateNetwork(_page[^1] - '1', delta);
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

    /// <summary>Requests metadata only on the owner queue; stale PC responses cannot populate the current network menu.</summary>
    private void RefreshNetworkStatus()
    {
        if (Interlocked.CompareExchange(ref _networkQueryPending, 1, 0) != 0) return;
        Interlocked.Exchange(ref _networkRefreshAt, Environment.TickCount64 + 2000);
        long generation = Volatile.Read(ref _uiInputGeneration);
        _ = _session.Enqueue(IpcProtocol.CreateRequest("GetNetworkStatus"), generation).ContinueWith(task =>
        {
            try
            {
                if (task.IsCompletedSuccessfully && task.Result.Success && _session.InputGeneration == generation)
                {
                    var status = JsonSerializer.SerializeToElement(task.Result.Payload, IpcProtocol.JsonOptions);
                    Volatile.Write(ref _networkMenu, new(generation, status));
                }
                else if (task.IsCompletedSuccessfully && !task.Result.Success) _notices.Enqueue("NETWORK STATUS: " + task.Result.ErrorCode);
            }
            finally { Interlocked.Exchange(ref _networkQueryPending, 0); }
        }, TaskScheduler.Default);
    }

    /// <summary>Returns one immutable current-owner response, without reading or invoking a backend from the UI thread.</summary>
    private JsonElement NetworkStatus()
    {
        var snapshot = Volatile.Read(ref _networkMenu);
        return snapshot is not null && snapshot.Generation == Volatile.Read(ref _uiInputGeneration) ? snapshot.Status : default;
    }

    /// <summary>Reads a bounded scalar status string; unknown or null backend fields remain visibly unavailable.</summary>
    private static string? NetworkString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    /// <summary>Reads an actual advertised backend flag without turning missing metadata into success.</summary>
    private static bool NetworkFlag(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    /// <summary>Bounds external adapter descriptions for the existing fixed-width English overlay.</summary>
    private static string NetworkText(string value) => new string(value.Take(60).Select(character => char.IsControl(character) ? ' ' : character).ToArray()).ToUpperInvariant();

    /// <summary>Names only configured guest card models, preserving the distinction between ISA and PCI.</summary>
    private static string NetworkModel(string? model) => model switch
    { NetworkAdapterProfile.Ne2000Isa => "NE2000 ISA", NetworkAdapterProfile.Rtl8029Pci => "RTL8029 PCI", _ => "NONE" };

    /// <summary>Cycles only an explicit network string choice, independent from the existing integer resource menu helper.</summary>
    private static string CycleNetworkValue(string[] values, string current, int delta) => values[(Array.IndexOf(values, current) + delta + values.Length) % values.Length];

    /// <summary>Reads one actual slot result without accepting another card's status as the selected connection.</summary>
    private JsonElement NetworkSlotStatus(int slot)
    {
        var status = NetworkStatus();
        if (status.ValueKind == JsonValueKind.Object && status.TryGetProperty("adapters", out var values) && values.ValueKind == JsonValueKind.Array)
            foreach (var adapter in values.EnumerateArray().Take(2))
                if (adapter.ValueKind == JsonValueKind.Object && adapter.TryGetProperty("slot", out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out int actual) && actual == slot)
                    return adapter;
        return default;
    }

    /// <summary>Uses only metadata returned by GetNetworkStatus; enumeration does not connect or substitute a host interface.</summary>
    private (string Id, string Description)[] NetworkBridges()
    {
        var status = NetworkStatus(); var result = new List<(string, string)>();
        if (status.ValueKind == JsonValueKind.Object && status.TryGetProperty("bridgeAdapters", out var adapters) && adapters.ValueKind == JsonValueKind.Array)
            foreach (var adapter in adapters.EnumerateArray().Take(64))
            {
                string? id = NetworkString(adapter, "id");
                if (string.IsNullOrWhiteSpace(id) || id.Length > 512 || id != id.Trim() || id.Any(char.IsControl)) continue;
                result.Add((id, NetworkString(adapter, "description") ?? id));
            }
        return result.ToArray();
    }

    /// <summary>Displays both hardware slots and real bridge availability, with remote play left as a separate feature.</summary>
    private string[] NetworkItems(MachineProfile profile)
    {
        // Render the actual owner-reported cable for exactly this configured slot.
        string Slot(int slot)
        {
            var adapter = profile.NetworkAdapters.FirstOrDefault(value => value.Slot == slot);
            return $"ADAPTER {slot + 1}: {NetworkModel(adapter?.Model)}" + (adapter is null ? "" : $" / {adapter.Mode.ToUpperInvariant()} / CABLE {(NetworkFlag(NetworkSlotStatus(slot), "connected") ? "ON" : "OFF")}");
        }
        var status = NetworkStatus();
        return [Slot(0), Slot(1), "REFRESH NETWORK STATUS", status.ValueKind == JsonValueKind.Undefined ? "BRIDGED: CHECKING AVAILABILITY" :
            NetworkFlag(status, "bridgeAvailable") ? "BRIDGED: AVAILABLE" : "BRIDGED: UNAVAILABLE / NPCAP REQUIRED", "GUEST: INSTALL NIC DRIVER + TCP/IP / DHCP",
            "APPLY CHANGES / COLD RESTART", "DISCARD CHANGES", "BACK"];
    }

    /// <summary>Shows editable persisted resources and separately reported live cable/backend outcomes.</summary>
    private string[] NetworkAdapterItems(MachineProfile profile, int slot)
    {
        var adapter = profile.NetworkAdapters.FirstOrDefault(value => value.Slot == slot);
        if (adapter is null) return ["MODEL: NONE (CHANGE RESTARTS)", "MODE: -", "CABLE: OFF", "MAC: CREATED WHEN ADDED", "I/O BASE: -", "IRQ: -",
            "BRIDGE ADAPTER: -", "VIRTUAL LAN: DOSE", "STATUS: NO ADAPTER INSTALLED", "REFRESH STATUS", "APPLY CHANGES / COLD RESTART", "DISCARD CHANGES", "BACK"];
        var state = NetworkSlotStatus(slot); var bridges = NetworkBridges();
        string bridge = bridges.FirstOrDefault(value => value.Id == adapter.HostAdapter).Description ?? adapter.HostAdapter ?? "NONE";
        string connection = NetworkString(state, "error") ?? NetworkString(state, "description") ?? "CHECKING STATUS";
        if (adapter.Mode == "bridged" && NetworkStatus().ValueKind != JsonValueKind.Undefined && !NetworkFlag(NetworkStatus(), "bridgeAvailable"))
            connection = "BRIDGED UNAVAILABLE / NPCAP REQUIRED";
        return [$"MODEL: {NetworkModel(adapter.Model)} (CHANGE RESTARTS)", $"MODE: {adapter.Mode.ToUpperInvariant()} (CHANGE RESTARTS)",
            $"CABLE: {(NetworkFlag(NetworkSlotStatus(slot), "connected") ? "ON" : "OFF")}", "MAC: " + adapter.MacAddress + " (EDIT)",
            $"I/O BASE: {adapter.IoBase:X4}H (CHANGE RESTARTS)", $"IRQ: {adapter.Irq}" + (adapter.Model == NetworkAdapterProfile.Rtl8029Pci ? " (FIXED PCI ROUTE)" : " (CHANGE RESTARTS)"),
            "BRIDGE ADAPTER: " + bridge, "VIRTUAL LAN: " + adapter.VirtualLan + " (EDIT)", "STATUS: " + connection, "REFRESH STATUS",
            "APPLY CHANGES / COLD RESTART", "DISCARD CHANGES", "BACK"];
    }

    /// <summary>Allocates one persistent locally administered unicast address only for a newly added guest card.</summary>
    private static string NewNetworkMac(MachineProfile profile)
    {
        Span<byte> bytes = stackalloc byte[6];
        for (int attempt = 0; attempt < 8; attempt++)
        {
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes); bytes[0] = (byte)((bytes[0] & 0xfc) | 2);
            string address = string.Join(":", bytes.ToArray().Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
            if (profile.NetworkAdapters.All(value => value.MacAddress != address)) return address;
        }
        throw new InvalidOperationException("Could not allocate a unique guest MAC address.");
    }

    /// <summary>Replaces exactly one slot, preserving all other cards, user identities and resources.</summary>
    private static MachineProfile WithNetwork(MachineProfile profile, int slot, NetworkAdapterProfile? adapter)
    {
        var current = profile.NetworkAdapters.FirstOrDefault(value => value.Slot == slot);
        if (current == adapter) return profile;
        var values = profile.NetworkAdapters.Where(value => value.Slot != slot).ToList();
        if (adapter is not null) values.Add(adapter);
        return profile with { NetworkAdapters = values.OrderBy(value => value.Slot).ToArray() };
    }

    /// <summary>Finds one complete conflict-free resource set before requesting a cold owner replacement.</summary>
    private static MachineProfile WithNetworkModel(MachineProfile profile, int slot, int delta)
    {
        var current = profile.NetworkAdapters.FirstOrDefault(value => value.Slot == slot);
        string[] models = profile.IsPciBiosBoard ? ["none", NetworkAdapterProfile.Ne2000Isa, NetworkAdapterProfile.Rtl8029Pci] : ["none", NetworkAdapterProfile.Ne2000Isa];
        string model = CycleNetworkValue(models, current?.Model ?? "none", delta);
        if (model == "none") return WithNetwork(profile, slot, null);
        string mac = current?.MacAddress ?? NewNetworkMac(profile);
        int[] bases = model == NetworkAdapterProfile.Ne2000Isa ? [0x300, 0x320] : [0x6000, 0x6020];
        int[] irqs = model == NetworkAdapterProfile.Ne2000Isa ? [10, 9] : [11];
        foreach (int basis in bases) foreach (int irq in irqs)
        {
            var adapter = (current ?? new NetworkAdapterProfile { Slot = slot, MacAddress = mac }) with
                { Model = model, IoBase = basis, Irq = irq, CableConnected = false };
            var requested = WithNetwork(profile, slot, adapter);
            try { requested.Validate(); return requested; } catch (ArgumentException) { }
        }
        throw new ArgumentException("No free network I/O window and IRQ remain. Change the other card or sound/MPU resources first.");
    }

    /// <summary>Validates only a requested network change locally; the real owner still validates and commits its revision.</summary>
    private void ChangeNetwork(Func<MachineProfile, MachineProfile> change)
    {
        try { ChangeProfile(profile => { var requested = change(profile); requested.Validate(); return requested; }); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { _notice = NetworkText(error.Message); }
    }

    /// <summary>Changes hardware through cold plans and cable state solely through the existing asynchronous owner queue.</summary>
    private void ActivateNetwork(int slot, int delta)
    {
        if (_selected == 12) { OpenPage("NETWORK"); return; }
        if (_selected == 11) { DiscardConfiguration(); return; }
        if (_selected == 10) { ApplyConfiguration(); return; }
        if (_selected == 9) { RefreshNetworkStatus(); return; }
        if (_selected == 0) { ChangeNetwork(profile => WithNetworkModel(profile, slot, delta)); return; }
        var profile = _configurationDraft?.Requested ?? Volatile.Read(ref _status)?.Configuration;
        var adapter = profile?.NetworkAdapters.FirstOrDefault(value => value.Slot == slot);
        if (adapter is null) { _notice = "ADD A NETWORK ADAPTER FIRST"; return; }
        if (_selected == 2)
        {
            if (_configurationDraft?.HasChanges == true) { _notice = "APPLY HARDWARE CHANGES BEFORE CONNECTING THE CABLE"; return; }
            bool connected = !NetworkFlag(NetworkSlotStatus(slot), "connected");
            if (connected && adapter.Mode == "bridged" && (adapter.HostAdapter is null || !NetworkFlag(NetworkStatus(), "bridgeAvailable")))
            { _notice = "BRIDGED UNAVAILABLE / SELECT AN AVAILABLE HOST ADAPTER"; return; }
            Command("SetNetworkConnection", new { slot, connected });
            _configurationDraft = null;
            Interlocked.Exchange(ref _networkRefreshAt, 0); _notice = "NETWORK CABLE CHANGE REQUESTED"; return;
        }
        if (_selected == 3) { EditNetworkText(slot, true); return; }
        if (_selected == 7) { EditNetworkText(slot, false); return; }
        if (_selected == 8) return;
        ChangeNetwork(current =>
        {
            var value = current.NetworkAdapters.First(item => item.Slot == slot);
            var changed = value;
            if (_selected == 1)
            {
                string mode = CycleNetworkValue(new[] { "nat", "host-only", "bridged" }, value.Mode, delta);
                changed = value with { Mode = mode, CableConnected = false, HostAdapter = mode == "bridged" ? value.HostAdapter : null };
            }
            else if (_selected == 4) changed = value with { IoBase = Cycle(value.Model == NetworkAdapterProfile.Ne2000Isa ? new[] { 0x300, 0x320 } : new[] { 0x6000, 0x6020 }, value.IoBase, delta) };
            else if (_selected == 5 && value.Model == NetworkAdapterProfile.Ne2000Isa) changed = value with { Irq = value.Irq == 10 ? 9 : 10 };
            else if (_selected == 6)
            {
                if (value.Mode != "bridged") throw new ArgumentException("Choose bridged mode before selecting a host adapter.");
                var bridges = NetworkBridges();
                if (bridges.Length == 0) throw new ArgumentException("Bridged networking is unavailable. Install Npcap and refresh the adapter list.");
                int position = Array.FindIndex(bridges, item => item.Id == value.HostAdapter);
                int next = position < 0 ? (delta < 0 ? bridges.Length - 1 : 0) : (position + delta + bridges.Length) % bridges.Length;
                changed = value with { HostAdapter = bridges[next].Id, CableConnected = false };
            }
            return WithNetwork(current, slot, changed);
        });
        Interlocked.Exchange(ref _networkRefreshAt, 0);
    }

    /// <summary>Builds a revision-checked configuration plan and requests the required controlled power cycle.</summary>
    private void ChangeProfile(Func<MachineProfile, MachineProfile> change)
    {
        if (_configurationApplying) { _notice = "WAIT FOR THE CURRENT APPLY REQUEST"; return; }
        BeginConfiguration();
        try
        {
            var current = _configurationDraft!.Requested;
            var requested = change(current);
            if (requested == current) { _notice = "CONFIGURATION UNCHANGED"; return; }
            _configurationDraft.Stage(requested);
            _notice = "PENDING CHANGE / SELECT APPLY CHANGES TO RESTART AND SAVE";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { _notice = error.Message; }
    }

    /// <summary>Requests a cold start from an explicitly chosen protected raw IDE base image.</summary>
    private void BootRawImage(string path)
    {
        Command("PowerCycleReadOnlyDisk", new { path = Path.GetFullPath(path) });
        CloseMenu();
    }

    /// <summary>Shows fixed controller slots and actual image capacity without confusing BIOS drive numbering with physical placement.</summary>
    private static string HardDiskLabel(MachineStatus? status, int slot)
    {
        if (status?.OpticalDisc is { } cd && cd.Slot == slot) return $"{slot}: ATAPI CD-ROM / RESERVED / READ-ONLY ISO";
        var disk = status?.HardDisks?.FirstOrDefault(d => d.Slot == slot);
        string location = slot switch { 0 => "PRIMARY MASTER", 1 => "PRIMARY SLAVE", 2 => "SECONDARY MASTER", _ => "SECONDARY SLAVE" };
        return disk is null ? $"{slot}: {location} / EMPTY" : $"{slot}: {location} / {disk.Bytes / 1048576m:0.##} MIB / {(disk.Writable ? "OVERLAY" : "READ-ONLY")}";
    }

    /// <summary>Creates only a new image in the background so large zero-filled disks cannot freeze the UI or guest command owner.</summary>
    private void CreateDiskImage()
    {
        if (_diskCreation is { IsCompleted: false }) { _notice = "A DISK IMAGE IS ALREADY BEING CREATED"; return; }
        long? bytes = StorageDialogs.AskDiskSize(); if (bytes is null) return;
        using var dialog = new SaveFileDialog { Title = "Neue leere Festplattendatei", Filter = "RAW-Festplatte (*.img)|*.img", DefaultExt = "img", OverwritePrompt = false };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        string path = dialog.FileName;
        _notice = "CREATING BLANK IMAGE...";
        _diskCreation = Task.Run(() =>
        {
            try { RawDiskImage.Create(path, bytes.Value, _diskCreationStop.Token); _notices.Enqueue("IMAGE CREATED: " + path + " / ATTACH TO AN IDE SLOT"); }
            catch (OperationCanceledException) { _notices.Enqueue("IMAGE CREATION CANCELLED"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { _notices.Enqueue("IMAGE CREATION FAILED: " + ex.Message); }
        });
    }

    /// <summary>Selects a protected base and optional journal, then requests an atomic cold topology change on the emulation owner.</summary>
    private void AttachDiskImage(int slot, bool writable)
    {
        if (Volatile.Read(ref _status)?.Configuration is { IsAtapi: true } p && p.AtapiSlot == slot)
        { OpenPage("OPTICAL DRIVE"); return; }
        using var image = new OpenFileDialog { Title = "Festplatte fÃƒÆ’Ã‚Â¼r IDE " + slot, Filter = "RAW-Festplatten (*.img;*.raw;*.ima)|*.img;*.raw;*.ima" };
        if (image.ShowDialog() != DialogResult.OK) return;
        string? overlayPath = null;
        if (writable)
        {
            using var overlay = new SaveFileDialog { Title = "Privates Overlay anlegen oder fortsetzen", Filter = "Private Overlays (*.cow)|*.cow", DefaultExt = "cow", OverwritePrompt = false,
                FileName = Path.GetFileNameWithoutExtension(image.FileName) + "-ide" + slot + ".cow", InitialDirectory = Path.GetDirectoryName(_settingsPath) };
            if (overlay.ShowDialog() != DialogResult.OK) return;
            overlayPath = overlay.FileName;
        }
        Command("SetHardDisk", new { slot, path = image.FileName, overlayPath }); CloseMenu();
    }

    /// <summary>Loads the UI-only footer preference, retaining the former visible default for older settings.</summary>
    private static bool LoadDebugOverlay(string path)
        => LoadVideoPreference(path, "showDebugOverlay");

    /// <summary>Loads an optional UI-only video flag with its explicit compatibility default.</summary>
    private static bool LoadVideoPreference(string path, string name, bool fallback = true)
    {
        try
        {
            if (!File.Exists(path)) return fallback;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(name, out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return fallback;
    }

    /// <summary>Atomically replaces host preferences, including the UI-only footer without changing VM contracts.</summary>
    private void SaveSettings()
    {
        string temporary = _settingsPath + ".tmp";
        var options = new JsonSerializerOptions(IpcProtocol.JsonOptions) { WriteIndented = true };
        var values = JsonSerializer.SerializeToNode(_settings, options)!.AsObject();
        values["showDebugOverlay"] = _showDebugOverlay;
        values["parallelVideo"] = _parallelVideo;
        values["presentationHz"] = _presentationHz;
        File.WriteAllText(temporary, values.ToJsonString(options));
        File.Move(temporary, _settingsPath, true);
    }

    /// <summary>Routes a local key-down event to overlay navigation, host shortcuts or the guest keyboard.</summary>
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
            case Key.PageUp: _selected = Math.Max(0, _selected - SettingsMenuLayout.VisibleRows); break;
            case Key.PageDown: _selected = Math.Min(_items.Length - 1, _selected + SettingsMenuLayout.VisibleRows); break;
            case Key.Home: _selected = 0; break;
            case Key.Enter: case Key.Right: Activate(); break;
            case Key.Left: Activate(-1); break;
            case Key.Escape: BackPage(); break;
        }
    }
    /// <summary>Handles local character input only under the active overlay/guest input policy.</summary>
    public override void OnUserKeyPress(KeyEventArgs e)
    {
        if (!IsFocused) return;
        if (_page.Length == 0)
        {
            if (GuestKeyRepeatPolicy.ShouldRepeat(GuestScanCode(e.Key), _guestKeys.Contains(e.Key))) SendGuestKey(e.Key, false);
        }
        else if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right) OnUserKeyDown(e);
    }
    /// <summary>Selects the adjacent supported setting value with bounded wraparound.</summary>
    private static int Cycle(int[] values, int current, int delta) => values[(Array.IndexOf(values, current) + delta + values.Length) % values.Length];
    /// <summary>Balances a mapped local key release through the guest keyboard path.</summary>
    public override void OnUserKeyUp(KeyEventArgs e) { if (_guestKeys.Remove(e.Key)) SendGuestKey(e.Key, true); }
    /// <summary>Processes the window's mouse movement callback under its capture policy.</summary>
    public override void OnUserMouseMove(int x, int y) { }
    /// <summary>Handles a local guest-area click without treating menu/status areas as a capture request.</summary>
    public override void OnUserMouseClick(int x, int y, MouseState mouseState) { }
    /// <summary>Updates this window's captured-button state from an actual local mouse event.</summary>
    private void NativeMouseButton(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton button, bool down)
    {
        int bit = button switch { OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left => 1,
            OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right => 2, _ => 0 };
        if (bit == 0) return;
        if (_page.Length != 0)
        {
            if (bit == 1 && down && IsFocused) ClickMenu((int)(MousePosition.X * PixelsWidth / ClientSize.X), (int)(MousePosition.Y * PixelsHeight / ClientSize.Y));
            return;
        }
        if (down && !_mouseInput.Captured && bit == 1 && _page.Length == 0 && IsFocused &&
            Volatile.Read(ref _status)?.Configuration?.Mouse == "ps2-two-button")
        {
            // Capture only a click inside the displayed guest, never a menu/status bar.
            var frame = Volatile.Read(ref _frame);
            double aspect = _settings.CorrectAspect ? 4.0 / 3 : frame is null ? 4.0 / 3 : (double)frame.Width / frame.Height;
            int width = Math.Min(PixelsWidth, (int)(GuestAreaHeight * aspect)), height = (int)(width / aspect);
            double x = MousePosition.X * PixelsWidth / ClientSize.X, y = MousePosition.Y * PixelsHeight / ClientSize.Y;
            if (x < (PixelsWidth - width) / 2 || x >= (PixelsWidth + width) / 2 || y < (GuestAreaHeight - height) / 2 || y >= (GuestAreaHeight + height) / 2) return;
            _mouseInput.Capture(); CursorState = CursorState.Grabbed;
        }
        SendMouse(_mouseInput.Button(bit, down));
    }
    /// <summary>Queues bounded relative PS/2 input only when the local capture policy permits it.</summary>
    private void SendMouse(RelativeMouseInput.Input? input)
    {
        if (_nativeMouseAcceptance && input is not null && _nativeGuestMouseEvents.Count < 64) _nativeGuestMouseEvents.Add(input);
        if (input is not null) Command("SendMouseInput", input);
    }
    /// <summary>Releases this window's mouse capture and balances its emulated button state.</summary>
    private void ReleaseMouse()
    {
        if (!_mouseInput.Captured) return;
        SendMouse(_mouseInput.Release()); CursorState = CursorState.Normal;
    }
    /// <summary>Queues releases for guest keys held by this window before input ownership is lost.</summary>
    private void ReleaseGuestKeys()
    {
        foreach (Key key in _guestKeys) SendGuestKey(key, true);
        _guestKeys.Clear(); ResetKeyRepeat(true);
    }
    /// <summary>Maps an existing host key to its translated set-1 make code independently of repeat/release routing.</summary>
    private static byte GuestScanCode(Key key) => key switch
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
    /// <summary>Routes one complete mapped make/break packet through atomic owner-thread admission.</summary>
    private void SendGuestKey(Key key, bool released)
    {
        byte code = GuestScanCode(key);
        if (code == 0) return;
        byte value = (byte)(code | (released ? 0x80 : 0));
        if (key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Delete or Key.Home or Key.PageUp or Key.PageDown)
            Command("SendScanCode", new { scanCodes = new[] { (int)0xe0, value } });
        else Command("SendScanCode", new { scanCode = value });
    }
    /// <summary>Routes a user-dropped media file through the explicit media-selection workflow.</summary>
    public override void OnUserFileDrop(string[] fileNames)
    {
        if (fileNames.Length > 0) BootRawImage(fileNames[0]);
    }

    /// <summary>Advances the internally driven GUI acceptance sequence without synthesizing Windows input.</summary>
    private void AdvanceSmokeTest()
    {
        if (_storageAcceptance) { AdvanceStorageAcceptance(); return; }
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

    /// <summary>Publishes current host source/master levels without changing guest sample timing.</summary>
    private void ApplyAudioLevels() => _audio?.SetLevels(_settings.AudioMuted, _settings.MasterVolume);
    /// <summary>Collects actual provider-level audio/mute/gain evidence in the explicit internal GUI test.</summary>
    private void AdvanceAudioAcceptance()
    {
        if (_audio is null || _elapsed.ElapsedMilliseconds < _nextSmokeTime || _captureIndex >= 0) return;
        if (_smokeStage == 0 && (Volatile.Read(ref _status)?.DebugOutput.Contains(_captureAfter ?? "SPEAKER-HOLD-READY", StringComparison.Ordinal) != true || _audio.NonSilentFrames < 4800)) return;
        bool digital = Volatile.Read(ref _status)?.Configuration?.SoundCard == "sb2-dsp-development";
        bool fm = _captureAfter == "OPL2-HOLD-READY";
        // Measures the explicitly delimited phase without including fixture construction in its timing window.
        void Measure(string phase) => _audioMeasurements.Add(new { phase, frames = _audio.ReadFrames, nonSilent = _audio.NonSilentFrames,
            energy = _audio.Energy, output = _audio.Snapshot() });
        // Opens and navigates the audio overlay for the internally driven provider-level test.
        void AudioMenu()
        {
            OnUserKeyDown(new(Key.F10, KeyModifiers.Control | KeyModifiers.Alt));
            for (int i = 0; i < 5; i++) OnUserKeyDown(new(Key.Down, 0));
            OnUserKeyDown(new(Key.Enter, 0));
        }
        // Closes the test overlay and restores normal guest presentation.
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

    /// <summary>Runs the local pre-presentation hook used for explicit private screenshot evidence.</summary>
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
            footerBrightPixels, error = GL.GetError().ToString(), sourceUploadError = SourceUploadError.ToString() }));
        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }
        bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY); bitmap.Save(path, ImageFormat.Png);
        _smokeCaptures.Add(path);
        GL.ReadBuffer(ReadBufferMode.Back);
    }

    /// <summary>Writes an opt-in private teardown stage so playback and device-release failures remain distinguishable.</summary>
    private void ShutdownStage(string stage)
    {
        if (_smokeDirectory is null) return;
        File.AppendAllText(Path.Combine(_smokeDirectory, "shutdown.jsonl"), JsonSerializer.Serialize(new { stage, elapsedMilliseconds = _elapsed.ElapsedMilliseconds }) + "\n");
    }

    /// <summary>Stops owned workers before releasing audio, IPC, reader leases and machine media.</summary>
    protected override void OnUnload()
    {
        ShutdownStage("begin");
        ReleaseMouse();
        FinishNativeMouseInput();
        _stop.Cancel(); _worker?.GetAwaiter().GetResult(); ShutdownStage("worker-stopped");
        _videoWorker?.Dispose(); _videoWorker = null; ShutdownStage("video-worker-stopped");
        _diskCreationStop.Cancel(); _diskCreation?.GetAwaiter().GetResult(); _diskCreationStop.Dispose();
        _audio?.Dispose(stage => ShutdownStage("audio-" + stage)); ShutdownStage("audio-stopped");
        _ipc.DisposeAsync().AsTask().GetAwaiter().GetResult(); ShutdownStage("ipc-stopped");
        _displayFrame?.Dispose(); _displayFrame = null;
        ShutdownStage("frame-released");
        _session.Dispose(); _stop.Dispose();
        ShutdownStage("session-stopped");
        base.OnUnload();
        ShutdownStage("complete");
        if (_smokeDirectory is not null)
            File.WriteAllText(Path.Combine(_smokeDirectory, "shutdown-result.json"), JsonSerializer.Serialize(new
            {
                completed = true, audio = _audio?.Snapshot(), hostAudioTeardownAccepted = _audio?.ShutdownFault is null,
                nativeInputSent = _nativeInputsSent != 0 || _nativePointerMoved, nativeInputEventsSent = _nativeInputsSent,
                nativeInputCleanupFault = _nativeCleanupFault
            }, IpcProtocol.JsonOptions));
    }
}
