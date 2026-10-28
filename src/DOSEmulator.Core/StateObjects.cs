// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DOSEmulator.Core;

/// <summary>Explicit component state; implementations enumerate data and graph children without serializing live objects or delegates.</summary>
public interface IStateObject
{
    /// <summary>Captures an owned versioned set of explicitly selected fields at an owner boundary.</summary>
    StateBlock SaveState();
    /// <summary>Applies checked fields only to a newly prepared component, never the currently adopted PC.</summary>
    void LoadState(StateBlock state);
    /// <summary>Enumerates owned nested state, excluding references to other already registered components.</summary>
    IEnumerable<(string Name, IStateObject Value)> StateChildren { get; }
}

/// <summary>One device can rebind only its explicitly known pending operation kinds.</summary>
public interface IScheduledStateObject : IStateObject
{
    /// <summary>Recreates a callback from a stable operation and typed data; unknown operations are rejected.</summary>
    Action BindStateEvent(string operation, JsonElement[] payload);
}

/// <summary>Versioned component data. Type is a comparison key, never a CLR activation/type-loading instruction.</summary>
public sealed record StateBlock(string Type, int Version, Dictionary<string, JsonElement> Fields);

/// <summary>Strict values used only by explicit generated state accessors; no private-object graph reflection occurs.</summary>
public static class StateValues
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        IncludeFields = true, IgnoreReadOnlyProperties = true, MaxDepth = 64, RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    /// <summary>Encodes typed captured event arguments only when a snapshot is actually requested.</summary>
    public static JsonElement[] Pack<T>(T a) => [JsonSerializer.SerializeToElement(a, Json)];
    /// <summary>Encodes two typed captured event arguments without persisting a closure.</summary>
    public static JsonElement[] Pack<T, U>(T a, U b) => [JsonSerializer.SerializeToElement(a, Json), JsonSerializer.SerializeToElement(b, Json)];
    /// <summary>Encodes three typed event arguments only at capture time.</summary>
    public static JsonElement[] Pack<T, U, V>(T a, U b, V c) => [JsonSerializer.SerializeToElement(a, Json), JsonSerializer.SerializeToElement(b, Json), JsonSerializer.SerializeToElement(c, Json)];
    /// <summary>Encodes a receive descriptor and its reset generation only when a snapshot is captured.</summary>
    public static JsonElement[] Pack<T, U, V, W>(T a, U b, V c, W d) =>
        [JsonSerializer.SerializeToElement(a, Json), JsonSerializer.SerializeToElement(b, Json), JsonSerializer.SerializeToElement(c, Json), JsonSerializer.SerializeToElement(d, Json)];
    /// <summary>Encodes a complete UART shifter descriptor only at capture time.</summary>
    public static JsonElement[] Pack<T, U, V, W, X, Y>(T a, U b, V c, W d, X e, Y f) =>
        [JsonSerializer.SerializeToElement(a, Json), JsonSerializer.SerializeToElement(b, Json), JsonSerializer.SerializeToElement(c, Json),
         JsonSerializer.SerializeToElement(d, Json), JsonSerializer.SerializeToElement(e, Json), JsonSerializer.SerializeToElement(f, Json)];
    /// <summary>Reads one event argument under the same strict numeric/record rules as component data.</summary>
    public static T Read<T>(JsonElement value) => value.Deserialize<T>(Json)!;
}

/// <summary>Builds one owned field block, preserving raw integers and exact JSON round-trip float encodings.</summary>
public sealed class StateWriter(string type)
{
    private readonly Dictionary<string, JsonElement> _fields = new(StringComparer.Ordinal);
    /// <summary>Copies one explicitly named scalar, immutable value, array or queue into the captured block.</summary>
    public void Value<T>(string name, T value) => _fields.Add(name, JsonSerializer.SerializeToElement(value, StateValues.Json));
    /// <summary>Finishes a version-one block whose exact field set is checked during import.</summary>
    public StateBlock Finish() => new(type, 1, _fields);
}

/// <summary>Checks every field and shape while importing into an unadopted graph.</summary>
public sealed class StateReader
{
    private readonly StateBlock _state;
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);
    /// <summary>Rejects unknown component type/version before any component field is applied.</summary>
    public StateReader(StateBlock state, string type)
    {
        if (state is null || state.Type != type || state.Version != 1 || state.Fields is null)
            throw new InvalidDataException("Unknown component state type/version.");
        _state = state;
    }
    /// <summary>Reads one required typed field exactly once, rejecting missing or duplicate accessor names.</summary>
    public T Value<T>(string name)
    {
        if (!_read.Add(name) || !_state.Fields.TryGetValue(name, out var value)) throw new InvalidDataException("Missing component state field: " + name);
        return StateValues.Read<T>(value);
    }
    /// <summary>Reads a bounded non-null owned collection before a queue or set is mutated.</summary>
    public T[] Sequence<T>(string name)
    {
        var values = Value<T[]>(name);
        if (values is null || values.Length > 262144) throw new InvalidDataException("Invalid state collection: " + name);
        return values;
    }
    /// <summary>Restores a fixed-size array while retaining constructor-created ownership and aliases.</summary>
    public void Array<T>(string name, T[] destination)
    {
        var values = Value<T[]>(name);
        if (values is null || values.Length != destination.Length) throw new InvalidDataException("State array shape differs: " + name);
        values.CopyTo(destination, 0);
    }
    /// <summary>Checks each VGA plane shape before copying any plane bytes.</summary>
    public void Planes(string name, byte[][] destination)
    {
        var values = Value<byte[][]>(name);
        if (values is null || values.Length != destination.Length || values.Where((v, i) => v is null || v.Length != destination[i].Length).Any())
            throw new InvalidDataException("State VRAM plane shape differs.");
        for (int i = 0; i < values.Length; i++) values[i].CopyTo(destination[i], 0);
    }
    /// <summary>Requires a constructor-fixed scalar setting to match the selected machine topology.</summary>
    public void Invariant<T>(string name, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(Value<T>(name), value)) throw new InvalidDataException("State configuration differs: " + name);
    }
    /// <summary>Rejects extra fields instead of silently discarding unknown state from another build.</summary>
    public void Finish()
    {
        if (_read.Count != _state.Fields.Count) throw new InvalidDataException("Unknown component state fields.");
    }
}

/// <summary>Names one pending callback's data; owner references stay local and never enter the persisted representation.</summary>
public sealed record ScheduledStateEvent(long Id, ulong Time, string Owner, string Operation, JsonElement[] Payload);
/// <summary>Complete scheduler clock, conversion caches, stable tokens, cancellations and queued deadlines.</summary>
public sealed record SchedulerState(ulong Now, long Sequence, ulong Remainder, uint CachedCycles, uint CachedFrequency,
    ulong Whole, ulong Fraction, long[] Cancelled, ScheduledStateEvent[] Events);
