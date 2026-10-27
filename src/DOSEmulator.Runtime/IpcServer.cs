// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
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
    /// <summary>Unique current-user local-pipe name published for this emulator instance.</summary>
    public string PipeName { get; }
    /// <summary>Most recent component fault description, or null while no fault is recorded.</summary>
    public string? Fault { get; private set; }

    /// <summary>Initializes IpcServer and connects the supplied dependencies before the component is used.</summary>
    public IpcServer(MachineSession session, string? pipeName = null, string? instanceDirectory = null)
    {
        _session = session;
        PipeName = pipeName ?? $"dosemulator-{Environment.ProcessId}-{Guid.NewGuid():N}";
        if (instanceDirectory is not null)
        {
            Directory.CreateDirectory(instanceDirectory);
            _instanceFile = Path.Combine(instanceDirectory, $"{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        }
        // The GUI may stop its message loop before synchronously disposing this
        // service. Pipe continuations must never depend on that UI context.
        _server = Task.Run(RunAsync);
    }

    /// <summary>Serves bounded framed local-pipe requests until cancellation, independent from any GUI message loop.</summary>
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

    /// <summary>Stops asynchronous service work and releases its owned resources without requiring a GUI synchronization context.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _server.ConfigureAwait(false);
        if (_instanceFile is not null && File.Exists(_instanceFile)) File.Delete(_instanceFile);
        _stop.Dispose();
    }
}
