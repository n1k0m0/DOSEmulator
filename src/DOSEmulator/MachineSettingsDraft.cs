// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;

namespace DOSEmulator;

/// <summary>UI-owned hardware edits with one explicit validated restart, preserving the original owner revision until Apply.</summary>
internal sealed class MachineSettingsDraft
{
    private readonly MachineProfile _original;
    /// <summary>Revision captured before editing; a different live owner cannot accept this draft.</summary>
    internal long Revision { get; }
    /// <summary>Current editable immutable profile, without changing the running PC or its media.</summary>
    internal MachineProfile Requested { get; private set; }
    /// <summary>Whether any persisted hardware value differs from the original profile.</summary>
    internal bool HasChanges => Requested != _original;

    /// <summary>Copies the adapter array so draft changes cannot mutate the owner's profile collection.</summary>
    internal MachineSettingsDraft(MachineProfile profile, long revision)
    {
        _original = profile with { NetworkAdapters = profile.NetworkAdapters.ToArray() };
        Requested = _original; Revision = revision;
    }

    /// <summary>Stages one edit; resource conflicts may be resolved with further edits before whole-profile validation.</summary>
    internal void Stage(MachineProfile profile) => Requested = profile;

    /// <summary>Validates the complete resource set once, rejecting a stale owner before a restart request is issued.</summary>
    internal ConfigurationPlan Plan(long currentRevision)
    {
        if (currentRevision != Revision) throw new InvalidOperationException("The PC changed while editing. Discard this draft and reopen settings.");
        Requested.Validate(); return new(Revision, Requested, HasChanges);
    }

    /// <summary>Changes only an implemented CPU/clock tuple and retains RAM, media and user device choices.</summary>
    internal static MachineProfile CycleCpu(MachineProfile profile, int delta)
    {
        if (delta == 0) return profile;
        if (profile.IsWindows95Board)
        {
            if (profile.IsP55c)
            {
                int[] mhz = [166, 200, 233]; int at = Array.IndexOf(mhz, profile.CpuHz / 1_000_000);
                return profile.WithMmxCpu(true, mhz[(at + (delta < 0 ? 2 : 1)) % mhz.Length]);
            }
            bool fast = profile.Id != MachineProfile.Windows95Profile133Id;
            return profile with { Id = fast ? MachineProfile.Windows95Profile133Id : MachineProfile.Windows95ProfileId,
                CpuHz = fast ? MachineProfile.Windows95Cpu133Hz : MachineProfile.FxHostCpuHz };
        }
        if (profile.IsFxHost) throw new InvalidOperationException("This chipset test board has a fixed CPU. Windows boards support 100 and 133 MHz.");
        return profile.WithDevelopmentCpu(!profile.IsP54c);
    }

    /// <summary>Selects a complete implemented board tuple; incompatible installed PCI cards must be removed explicitly in the draft.</summary>
    internal static MachineProfile CyclePlatform(MachineProfile profile, int delta)
    {
        string[] choices = ["virtual486-dev-v1", MachineProfile.P54cProfileId, MachineProfile.Windows95ProfileId, MachineProfile.Windows95Profile133Id,
            MachineProfile.MmxProfile166Id, MachineProfile.MmxProfile200Id, MachineProfile.MmxProfile233Id];
        int position = Array.IndexOf(choices, profile.Id);
        int next = position < 0 ? (delta < 0 ? choices.Length - 1 : 0) : (position + delta + choices.Length) % choices.Length;
        bool pci = next >= 2;
        if (!pci && profile.NetworkAdapters.Any(adapter => adapter.Model == NetworkAdapterProfile.Rtl8029Pci))
            throw new ArgumentException("Remove PCI network adapters in the draft before selecting an ISA-only board.");
        if (!pci && profile.IsVoodoo) throw new ArgumentException("Remove the additional Voodoo card in the draft before selecting an ISA-only board.");
        int ram = pci ? Math.Max(16, profile.RamMiB) : Math.Min(16, profile.RamMiB);
        return profile with { Id = choices[next], Cpu = next == 0 ? "i486dx-development" : next >= 4 ? MachineProfile.P55cCpuId : MachineProfile.P54cCpuId,
            CpuHz = next switch { 0 => 33_000_000, 1 => 90_000_000, 2 => 100_000_000, 3 => 133_000_000, 4 => 166_000_000, 5 => 200_000_000, _ => 233_000_000 }, RamMiB = ram,
            OpticalDrive = pci ? MachineProfile.AtapiCdRomId : "none", AtapiSlot = pci && profile.IsAtapi ? profile.AtapiSlot : 2,
            VideoCard = pci ? MachineProfile.S3Trio64VideoId : profile.IsTrio64 ? "et4000ax-isa-1m-development" : profile.VideoCard };
    }

    /// <summary>Normalizes typed MAC punctuation/case while keeping the validator's complete local unicast identity requirement.</summary>
    internal static string NormalizeMac(string text)
    {
        string hex = text.Trim().Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit)) throw new ArgumentException("Enter all six MAC bytes, for example 02:44:4F:53:45:01.");
        string canonical = string.Join(":", Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2)));
        _ = (new NetworkAdapterProfile { MacAddress = canonical }).GetMacBytes(); return canonical;
    }
}

/// <summary>Pure overlay geometry shared by drawing, keyboard paging and mouse hit testing.</summary>
internal static class SettingsMenuLayout
{
    internal const int Left = 40, Top = 90, Width = 880, Height = 540, RowTop = 186, RowHeight = 33, VisibleRows = 10;
    /// <summary>Returns the first visible row of the selected page, including short and final partial pages.</summary>
    internal static int First(int count, int selected) => count == 0 ? 0 : Math.Clamp(selected, 0, count - 1) / VisibleRows * VisibleRows;
    /// <summary>Bounds long display rows while preserving their full value for the selected-row detail area.</summary>
    internal static string Label(string value) => value.Length <= 66 ? value : value[..63] + "...";
    /// <summary>Maps a logical overlay click to one visible row without accepting the title, footer or offscreen rows.</summary>
    internal static int Hit(int count, int selected, int x, int y)
    {
        if (x < Left + 12 || x >= Left + Width - 12 || y < RowTop - 6 || y >= RowTop - 6 + VisibleRows * RowHeight) return -1;
        int index = First(count, selected) + (y - RowTop + 6) / RowHeight;
        return index < count ? index : -1;
    }
}

/// <summary>Host presentation admission only; skipped captures never skip guest instructions, audio or device time.</summary>
internal sealed class PresentationCadence
{
    private ulong _next;
    /// <summary>Admits one current image at the selected host rate without queuing obsolete catch-up images.</summary>
    internal bool Due(ulong hostNanoseconds, int hz, bool force = false)
    {
        if (hz is not (30 or 60 or 120)) throw new ArgumentOutOfRangeException(nameof(hz));
        if (!force && hostNanoseconds < _next) return false;
        ulong interval = 1_000_000_000UL / (uint)hz;
        _next = ulong.MaxValue - hostNanoseconds < interval ? ulong.MaxValue : hostNanoseconds + interval;
        return true;
    }
}
