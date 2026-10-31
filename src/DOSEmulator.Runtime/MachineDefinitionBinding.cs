// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using DOSEmulator.Contracts;

namespace DOSEmulator.Runtime;

/// <summary>Opt-in frontend binding that persists configuration changes without opening media or overwriting concurrent file edits.</summary>
internal sealed class MachineDefinitionBinding
{
    private byte[] _expected;
    /// <summary>The explicit configuration file currently owned by this frontend, never a running snapshot.</summary>
    internal string Path { get; }

    /// <summary>Binds an already loaded PC identity and records its current configuration bytes.</summary>
    internal MachineDefinitionBinding(string path, string id)
    {
        Path = System.IO.Path.GetFullPath(path);
        if (MachineDefinitionStore.Load(Path).Id != id) throw new ArgumentException("The machine file belongs to a different PC.");
        _expected = SHA256.HashData(File.ReadAllBytes(Path));
    }

    /// <summary>Saves only explicit hardware/media/cable edits; changed or removed external configurations require a reload.</summary>
    internal void Save(MachineDefinition definition)
    {
        if (!SHA256.HashData(File.ReadAllBytes(Path)).AsSpan().SequenceEqual(_expected))
            throw new IOException("The PC file changed outside DOSE. Save this configuration to a new PC file or reload it.");
        MachineDefinitionStore.Save(Path, definition);
        _expected = SHA256.HashData(File.ReadAllBytes(Path));
    }
}
