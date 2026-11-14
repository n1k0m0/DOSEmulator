// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Runtime.Networking;

namespace DOSEmulator;

public sealed partial class DosWindow
{
    private CancellationTokenSource? _transferStop;
    private Task? _transferTask;
    private string? _transferInstruction;

    /// <summary>Queues one explicitly selected host file for a Win9x guest's manual DOSE Import action.</summary>
    private void BeginGuestFileImport()
    {
        if (_configurationDraft?.HasChanges == true || _configurationApplying)
        {
            _notice = "APPLY OR DISCARD NETWORK CHANGES BEFORE IMPORTING";
            _noticeUntil = _elapsed.ElapsedMilliseconds + 8000;
            return;
        }
        if (_transferTask is { IsCompleted: false })
        {
            _notice = "ONE FILE IMPORT IS ALREADY WAITING FOR THE GUEST";
            _noticeUntil = _elapsed.ElapsedMilliseconds + 8000;
            return;
        }
        var originalLease = _session.GetTransferLease();
        using var dialog = new OpenFileDialog
        {
            Title = "Select one file to import into the Windows guest's C:\\Share",
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        var lease = _session.GetTransferLease();
        if (lease.Generation != originalLease.Generation)
        {
            _notice = "THE VIRTUAL MACHINE CHANGED / SELECT THE FILE AGAIN";
            _noticeUntil = _elapsed.ElapsedMilliseconds + 8000;
            return;
        }
        string selectedFile = Path.GetFullPath(dialog.FileName);
        _transferStop?.Dispose();
        var transferStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, lease.Token);
        string code = DoseTransferGateway.CreateCode();
        if (!_session.BeginTransfer(lease.Generation))
        {
            transferStop.Dispose();
            _notice = "A PREVIOUS IMPORT MAY STILL BE IN GUEST RAM / RESET THE GUEST FIRST";
            _noticeUntil = _elapsed.ElapsedMilliseconds + 8000;
            return;
        }
        _transferStop = transferStop;
        CloseMenu();
        int terminalReply = 0;
        _transferTask = Task.Run(async () =>
        {
            try
            {
                bool imported = await DoseTransferGateway.ServeOneAsync(selectedFile, code, transferStop.Token, () =>
                {
                    if (!transferStop.IsCancellationRequested)
                    {
                        string instruction = "IN WINDOWS RUN C:\\DOSETOOLS\\DOSEIMPORT.EXE " + code;
                        Volatile.Write(ref _transferInstruction, instruction);
                        _notices.Enqueue(instruction);
                    }
                }, () => _session.MarkTransferPayload(lease.Generation),
                    () => Interlocked.Exchange(ref terminalReply, 1)).ConfigureAwait(false);
                if (!transferStop.IsCancellationRequested)
                    _notices.Enqueue(imported ? "GUEST ACKNOWLEDGED DOSE IMPORT / CHECK C:\\SHARE IN WINDOWS" :
                        "DOSE IMPORT ABORTED BY THE GUEST / HOST FILE UNCHANGED");
            }
            catch (OperationCanceledException)
            {
                if (!_stop.IsCancellationRequested)
                    _notices.Enqueue(transferStop.IsCancellationRequested ? "DOSE IMPORT CANCELLED / VM OR NETWORK CHANGED" :
                        "DOSE IMPORT TIMED OUT / HOST FILE UNCHANGED");
            }
            catch (Exception error)
            {
                _notices.Enqueue("DOSE IMPORT FAILED: " + error.Message);
            }
            finally
            {
                _session.EndTransfer(lease.Generation, Volatile.Read(ref terminalReply) != 0);
                Volatile.Write(ref _transferInstruction, null);
            }
        });
    }
}
