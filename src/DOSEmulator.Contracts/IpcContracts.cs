// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;

namespace DOSEmulator.Contracts;

public sealed record IpcRequest(int ProtocolVersion, string RequestId, string Command, JsonElement Payload);
public sealed record IpcResponse(int ProtocolVersion, string RequestId, bool Success, string? ErrorCode, object? Payload);

public static class IpcProtocol
{
    /// <summary>Use the wire naming policy for both UI records and anonymous payloads.</summary>
    public static IpcRequest CreateRequest(string command, object? payload = null) =>
        new(Version, Guid.NewGuid().ToString("N"), command, JsonSerializer.SerializeToElement(payload ?? new { }, JsonOptions));
    public const int Version = 1;
    public const int MaximumMessageBytes = 64 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
