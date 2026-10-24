using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using DOSEmulator.Contracts;

namespace DOSEmulator.Runtime;

public sealed class IpcServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly MachineSession _session;
    private readonly Task _server;
    private readonly string? _instanceFile;
    public string PipeName { get; }
    public string? Fault { get; private set; }

    public IpcServer(MachineSession session, string? pipeName = null, string? instanceDirectory = null)
    {
        _session = session;
        PipeName = pipeName ?? $"dosemulator-{Environment.ProcessId}-{Guid.NewGuid():N}";
        if (instanceDirectory is not null)
        {
            Directory.CreateDirectory(instanceDirectory);
            _instanceFile = Path.Combine(instanceDirectory, $"{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        }
        _server = RunAsync();
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                if (_instanceFile is not null && !File.Exists(_instanceFile))
                {
                    File.WriteAllText(_instanceFile, JsonSerializer.Serialize(new { pid = Environment.ProcessId,
                        processStartedUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                        pipeName = PipeName, protocolVersion = IpcProtocol.Version, development = true }, IpcProtocol.JsonOptions));
                }
                await pipe.WaitForConnectionAsync(_stop.Token);
                // One client at a time is sufficient for the development protocol. Every read has a deadline.
                while (pipe.IsConnected && !_stop.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    byte[] header = new byte[4];
                    await pipe.ReadExactlyAsync(header, timeout.Token);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length is <= 0 or > IpcProtocol.MaximumMessageBytes) break;
                    byte[] body = new byte[length];
                    await pipe.ReadExactlyAsync(body, timeout.Token);
                    var request = JsonSerializer.Deserialize<IpcRequest>(body, IpcProtocol.JsonOptions);
                    if (request is null) break;
                    IpcResponse response = await _session.Enqueue(request).WaitAsync(timeout.Token);
                    byte[] reply = JsonSerializer.SerializeToUtf8Bytes(response, IpcProtocol.JsonOptions);
                    if (reply.Length > IpcProtocol.MaximumMessageBytes) break;
                    BinaryPrimitives.WriteInt32LittleEndian(header, reply.Length);
                    await pipe.WriteAsync(header, timeout.Token); await pipe.WriteAsync(reply, timeout.Token);
                    await pipe.FlushAsync(timeout.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException) { }
            catch (UnauthorizedAccessException ex) { Fault = ex.Message; break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _server;
        if (_instanceFile is not null && File.Exists(_instanceFile)) File.Delete(_instanceFile);
        _stop.Dispose();
    }
}
