using System.Collections.Concurrent;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

/// <summary>UI and IPC enqueue work; only the emulation owner pumps and mutates the machine.</summary>
public sealed class MachineSession(PcMachine machine) : IDisposable
{
    private readonly ConcurrentQueue<(IpcRequest Request, TaskCompletionSource<IpcResponse> Result)> _commands = new();
    private bool _disposed;
    private readonly object _queueGate = new();
    private long _revision = 1;
    public PcMachine Machine { get; private set; } = machine;
    public long Revision => _revision;

    public Task<IpcResponse> Enqueue(IpcRequest request)
    {
        var completion = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_queueGate)
        {
            if (_disposed) completion.SetResult(new(1, request.RequestId, false, "SessionStopped", null));
            else if (_commands.Count >= 256) completion.SetResult(new(1, request.RequestId, false, "QueueFull", null));
            else _commands.Enqueue((request, completion));
        }
        return completion.Task;
    }

    public void PumpCommands()
    {
        int count = 0;
        while (count++ < 256 && _commands.TryDequeue(out var command))
        {
            try { command.Result.TrySetResult(Handle(command.Request)); }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
            {
                command.Result.TrySetResult(new(1, command.Request.RequestId, false, "InvalidRequest", new { message = ex.Message }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException)
            {
                command.Result.TrySetResult(new(1, command.Request.RequestId, false, "ResourceUnavailable", new { message = ex.Message }));
            }
        }
    }

    private IpcResponse Handle(IpcRequest request)
    {
        if (request.ProtocolVersion != IpcProtocol.Version) return new(1, request.RequestId, false, "ProtocolMismatch", null);
        object? result;
        switch (request.Command)
        {
            case "Ping": result = new { pong = true }; break;
            case "GetStatus": result = Machine.GetStatus(); break;
            case "GetCapabilities":
                result = new { development = true, freeDosMinimalBoot = true, msDos622InstalledRealMode = true, biosFloppyFormatNativeGeometry = true, biosFixedDiskChsVerify = true,
                    privateDiskOverlays = true, floppyDma = true, dosBoot = "FreeDOS 1.4 minimal; local German MS-DOS 6.22 installed real-mode profile", protectedMode = false, sound = true, pcSpeaker = true, sb2Dsp = true, sb2HighSpeedPcm = true, opl2Timers = true, opl2Melodic = true, opl2CsmKeying = true, soundBlaster = false, opl = false, guestNetwork = false,
                    ps2Mouse = true, mouseWheel = false,
                    commands = new[] { "Ping", "GetStatus", "GetCapabilities", "Pause", "Resume", "PowerCycleCurrent", "PowerCycleDemo", "PowerCycleReadOnlyDisk", "PowerCycleOverlayDisk", "InsertFloppy", "EjectFloppy", "PrepareConfigurationChange", "CommitConfigurationChange", "SendScanCode", "SendMouseInput" } }; break;
            case "Pause": Machine.Running = false; result = Machine.GetStatus(); break;
            case "Resume": Machine.Running = true; result = Machine.GetStatus(); break;
            case "PowerCycleCurrent":
            {
                var replacement = Machine.CreatePowerCycle(Machine.Profile);
                Machine.Dispose(); Machine = replacement; Machine.Running = true; _revision++;
                result = Machine.GetStatus(); break;
            }
            case "PowerCycleDemo":
            {
                // This explicit operation always boots the built-in test disk, never swaps an attached user image silently.
                var replacement = PcMachine.CreateDemo(Machine.Profile);
                Machine.Dispose(); Machine = replacement; Machine.Running = true; _revision++;
                result = Machine.GetStatus(); break;
            }
            case "PowerCycleReadOnlyDisk":
            case "PowerCycleOverlayDisk":
            {
                string path = request.Payload.GetProperty("path").GetString() ?? throw new ArgumentException("Missing image path.");
                string extension = Path.GetExtension(path);
                if (!Path.IsPathFullyQualified(path) || (!extension.Equals(".img", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".raw", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".ima", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Use an absolute path to a raw IDE sector image (.img/.raw/.ima). This is not a floppy or ISO controller.");
                string? overlay = request.Command == "PowerCycleOverlayDisk"
                    ? request.Payload.GetProperty("overlayPath").GetString() ?? throw new ArgumentException("Missing overlay path.") : null;
                if (overlay is not null && (!Path.IsPathFullyQualified(overlay) || !Path.GetExtension(overlay).Equals(".cow", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Use an absolute path to a private .cow overlay file.");
                IReadOnlyBlockDevice disk = overlay is not null ? new CopyOnWriteDisk(path, overlay) : new ReadOnlyRawDisk(path);
                IReadOnlyBlockDevice? a = null, b = null;
                PcMachine replacement;
                try { a = Machine.Floppy.ReopenMedia(0); b = Machine.Floppy.ReopenMedia(1); replacement = new PcMachine(Machine.Profile, disk, a, b); }
                catch { disk.Dispose(); a?.Dispose(); b?.Dispose(); throw; }
                Machine.Dispose(); Machine = replacement; Machine.Running = true; _revision++;
                result = Machine.GetStatus(); break;
            }
            case "InsertFloppy":
            {
                int drive = request.Payload.GetProperty("drive").GetInt32();
                string path = request.Payload.GetProperty("path").GetString() ?? throw new ArgumentException("Missing floppy path.");
                if (!Path.IsPathFullyQualified(path) || Path.GetExtension(path).ToLowerInvariant() is not (".img" or ".ima" or ".raw"))
                    throw new ArgumentException("Use an absolute raw floppy image path.");
                string? overlay = request.Payload.TryGetProperty("overlayPath", out var overlayProperty) ? overlayProperty.GetString() : null;
                if (overlay is not null && (!Path.IsPathFullyQualified(overlay) || !Path.GetExtension(overlay).Equals(".cow", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Use an absolute private .cow overlay path.");
                IReadOnlyBlockDevice disk = overlay is not null ? new CopyOnWriteDisk(path, overlay) : new ReadOnlyRawDisk(path);
                try { Machine.Floppy.ReplaceMedia(drive, disk); } catch { disk.Dispose(); throw; }
                _revision++; result = Machine.GetStatus(); break;
            }
            case "EjectFloppy":
                Machine.Floppy.ReplaceMedia(request.Payload.GetProperty("drive").GetInt32(), null);
                _revision++; result = Machine.GetStatus(); break;
            case "PrepareConfigurationChange":
            {
                var requested = request.Payload.Deserialize<MachineProfile>(IpcProtocol.JsonOptions) ?? throw new ArgumentException("Missing profile.");
                requested.Validate(); result = new ConfigurationPlan(_revision, requested, requested != Machine.Profile); break;
            }
            case "CommitConfigurationChange":
            {
                var plan = request.Payload.Deserialize<ConfigurationPlan>(IpcProtocol.JsonOptions) ?? throw new ArgumentException("Missing change plan.");
                if (plan.ExpectedRevision != _revision) return new(1, request.RequestId, false, "StaleConfiguration", null);
                plan.Requested.Validate();
                var replacement = Machine.CreatePowerCycle(plan.Requested);
                Machine.Dispose(); Machine = replacement; Machine.Running = true; _revision++;
                result = new { revision = _revision, status = Machine.GetStatus() }; break;
            }
            case "SendScanCode": Machine.Keyboard.InjectTranslatedSet1(request.Payload.GetProperty("scanCode").GetByte()); result = null; break;
            case "SendMouseInput":
            {
                int x = request.Payload.GetProperty("x").GetInt32(), y = request.Payload.GetProperty("y").GetInt32();
                int buttons = request.Payload.GetProperty("buttons").GetInt32();
                if (x is < -32767 or > 32767 || y is < -32767 or > 32767 || (uint)buttons > 3)
                    throw new ArgumentException("Mouse input requires bounded relative counts and two button bits.");
                if (Machine.Mouse is null) throw new InvalidOperationException("No PS/2 mouse configured.");
                Machine.Mouse.Inject(x, y, buttons); result = null; break;
            }
            default: return new(1, request.RequestId, false, "UnsupportedCommand", null);
        }
        return new(1, request.RequestId, true, null, result);
    }

    public void Dispose()
    {
        lock (_queueGate)
        {
            _disposed = true;
            while (_commands.TryDequeue(out var item)) item.Result.TrySetResult(new(1, item.Request.RequestId, false, "SessionStopped", null));
        }
        Machine.Dispose();
    }
}
