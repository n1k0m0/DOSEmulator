// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.DiskExplorer;

/// <summary>Windows entry point; its metadata check deliberately precedes any native GUI initialization.</summary>
internal static class Program
{
    /// <summary>Handles the metadata-only check before any native application or form initialization.</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--check-ui", StringComparer.Ordinal))
        {
            if (ExplorerForm.WindowTitle != "DOSE Disk Explorer" || ExplorerForm.UiActions.Length != 9)
                throw new InvalidOperationException("Explorer action contract differs.");
            Console.WriteLine("PASS: English explorer actions; no Form, application loop or native window initialized.");
            return 0;
        }
        string? root = null, open = null;
        for (int n = 0; n < args.Length; n++)
            if (args[n] == "--vm-root" && n + 1 < args.Length) root = args[++n];
            else open = args[n];
        ApplicationConfiguration.Initialize();
        Application.Run(new ExplorerForm(root, open));
        return 0;
    }
}
