// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;

namespace DOSEmulator;

/// <summary>Hardware draft, apply and navigation policy for the existing English overlay.</summary>
public sealed partial class DosWindow
{
    private MachineSettingsDraft? _configurationDraft;
    private IpcResponse? _configurationResult;
    private bool _configurationApplying;
    private int _presentationHz = 60;

    /// <summary>Captures one actual owner/profile revision before entering either hardware or network editing.</summary>
    private void BeginConfiguration()
    {
        if (_configurationDraft is not null) return;
        lock (_gate) _configurationDraft = new(_session.Machine.Profile, _session.Revision);
    }

    /// <summary>Submits the complete draft once and leaves the old PC usable if validation or owner admission fails.</summary>
    private void ApplyConfiguration()
    {
        if (_configurationApplying) { _notice = "APPLY IN PROGRESS"; return; }
        if (_configurationDraft?.HasChanges != true) { _notice = "NO PENDING HARDWARE CHANGES"; return; }
        try
        {
            ConfigurationPlan plan;
            lock (_gate) plan = _configurationDraft.Plan(_session.Revision);
            _configurationApplying = true; _notice = "APPLYING HARDWARE / COLD RESTART";
            _ = _session.Enqueue(IpcProtocol.CreateRequest("CommitConfigurationChange", plan), Volatile.Read(ref _uiInputGeneration)).ContinueWith(task =>
                Volatile.Write(ref _configurationResult, task.IsCompletedSuccessfully ? task.Result :
                    new(1, Guid.NewGuid().ToString("N"), false, "ConfigurationFailed", new { message = task.Exception?.GetBaseException().Message ?? "Configuration request failed." })), TaskScheduler.Default);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { _notice = error.Message; }
    }

    /// <summary>Handles owner completion only on the UI thread; never reports an applied change as a rejected draft.</summary>
    private void CompleteConfiguration()
    {
        var result = Interlocked.Exchange(ref _configurationResult, null);
        if (result is null) return;
        _configurationApplying = false;
        if (result.Success || result.ErrorCode == "ConfigurationAppliedNotSaved")
        {
            _configurationDraft = null; Interlocked.Exchange(ref _networkRefreshAt, 0);
            CloseMenu();
            _notices.Enqueue(result.Success ? (_session.DefinitionPath is null ? "HARDWARE APPLIED / SAVE THIS PC TO KEEP SETTINGS" : "HARDWARE APPLIED / PC CONFIGURATION SAVED") : ResponseMessage(result));
        }
        else _notice = ResponseMessage(result);
    }

    /// <summary>Extracts the actual owner error while preserving useful long details in the menu footer.</summary>
    private static string ResponseMessage(IpcResponse result)
    {
        var value = JsonSerializer.SerializeToElement(result.Payload, IpcProtocol.JsonOptions);
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("message", out var message)
            ? message.GetString() ?? result.ErrorCode ?? "REQUEST FAILED" : result.ErrorCode ?? "REQUEST FAILED";
    }

    /// <summary>Discards only frontend edits, without a restart, network disconnect or write to the PC file.</summary>
    private void DiscardConfiguration()
    {
        if (_configurationApplying) { _notice = "WAIT FOR THE CURRENT APPLY REQUEST"; return; }
        _configurationDraft = null; _notice = "PENDING CHANGES DISCARDED";
    }

    /// <summary>Routes back navigation to its actual parent while retaining a pending draft until explicit Apply or Discard.</summary>
    private void BackPage()
    {
        if (_page == "MAIN MENU") { CloseMenu(); return; }
        OpenPage(_page switch
        {
            "SOUND CARD RESOURCES" or "CPU / PLATFORM" => "MACHINE / HARDWARE",
            "NETWORK / ADAPTER 1" or "NETWORK / ADAPTER 2" or "NETWORK HELP" => "NETWORK",
            "HARD DISKS" or "OPTICAL DRIVE" or "FLOPPY A" or "FLOPPY B" => "MEDIA",
            "IDE 0" or "IDE 1" or "IDE 2" or "IDE 3" => "HARD DISKS",
            _ => "MAIN MENU"
        });
    }

    /// <summary>Edits a persistent MAC or Host-only LAN through one explicit English host value dialog.</summary>
    private void EditNetworkText(int slot, bool mac)
    {
        BeginConfiguration();
        var adapter = _configurationDraft!.Requested.NetworkAdapters.FirstOrDefault(value => value.Slot == slot);
        if (adapter is null) { _notice = "ADD A NETWORK ADAPTER FIRST"; return; }
        string? value = StorageDialogs.AskText(mac ? "Network MAC address" : "Host-only virtual LAN", mac ? "Locally administered unicast MAC address:" : "LAN name shared by your DOSE guests:",
            mac ? adapter.MacAddress : adapter.VirtualLan, mac ? 32 : 64);
        if (value is null) return;
        ChangeNetwork(profile => WithNetwork(profile, slot, mac ? adapter with { MacAddress = MachineSettingsDraft.NormalizeMac(value), CableConnected = false }
            : adapter with { VirtualLan = value.Trim(), CableConnected = false }));
    }

    /// <summary>Accepts an actual overlay click without passing it to the guest PS/2 capture path.</summary>
    private void ClickMenu(int x, int y)
    {
        int row = SettingsMenuLayout.Hit(_items.Length, _selected, x, y);
        if (row < 0) return;
        _selected = row; Activate();
    }

    /// <summary>Reads only an existing supported presentation preference; invalid or legacy files retain 60 Hz.</summary>
    private static int LoadPresentationRate(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("presentationHz", out var value) && value.TryGetInt32(out int rate) && rate is 30 or 60 or 120) return rate;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return 60;
    }
}
