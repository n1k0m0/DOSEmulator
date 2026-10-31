// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Hardware;

namespace DOSEmulator;

/// <summary>Small host dialogs for values that cannot be entered in the keyboard-driven overlay menu.</summary>
internal static class StorageDialogs
{
    private sealed class PreviewForm : Form
    {
        /// <summary>Suppresses activation only for the explicit offscreen rendering check; normal user dialogs retain normal focus.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool NonActivatingPreview { get; set; }
        /// <summary>Lets Windows render actual child controls for a private preview without requesting the foreground.</summary>
        protected override bool ShowWithoutActivation => NonActivatingPreview;
    }
    /// <summary>Asks for an explicit binary capacity and validates it with the same bounds as the CLI.</summary>
    public static long? AskDiskSize()
    {
        using var form = CreateDiskSizeDialog(out var size, out var unit);
        while (form.ShowDialog() == DialogResult.OK)
        {
            try { return RawDiskImage.SizeInBytes(size.Value, unit.SelectedIndex == 0 ? "MiB" : "GiB"); }
            catch (ArgumentException ex) { MessageBox.Show(form, ex.Message, "Disk size", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        return null;
    }

    /// <summary>Builds the actual capacity editor so the same controls can be checked without synthetic Windows input.</summary>
    private static Form CreateDiskSizeDialog(out NumericUpDown sizeControl, out ComboBox unitControl)
    {
        var form = Dialog("Create blank disk", 440, 190);
        var caption = new Label { Text = "Capacity", Left = 20, Top = 22, AutoSize = true };
        var size = new NumericUpDown { Left = 20, Top = 48, Width = 180, Minimum = 1, Maximum = 131072, Value = 512, DecimalPlaces = 3 };
        var unit = new ComboBox { Left = 215, Top = 48, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        unit.Items.AddRange(["MiB (Megabyte, 1024 KiB)", "GiB (Gigabyte, 1024 MiB)"]); unit.SelectedIndex = 0;
        var help = new Label { Left = 20, Top = 83, Width = 395, Height = 43,
            Text = "Blank RAW image, 1 MiB to 128 GiB.\nPartition and format the disk inside the guest." };
        var ok = new Button { Text = "Continue", Left = 230, Top = 140, Width = 85, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 325, Top = 140, Width = 90, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange([caption, size, unit, help, ok, cancel]); form.AcceptButton = ok; form.CancelButton = cancel;
        unit.SelectedIndexChanged += (_, _) =>
        {
            decimal converted = unit.SelectedIndex == 1 ? size.Value / 1024 : size.Value * 1024;
            decimal minimum = unit.SelectedIndex == 1 ? 1m / 1024 : 1m;
            decimal maximum = unit.SelectedIndex == 1 ? 128m : 131072m;
            size.Minimum = 0; size.Maximum = 131072; size.DecimalPlaces = 10;
            size.Value = Math.Clamp(converted, minimum, maximum); size.Minimum = minimum; size.Maximum = maximum;
        };
        sizeControl = size; unitControl = unit; return form;
    }

    /// <summary>Obtains a human-readable PC name without changing its stable machine identity.</summary>
    public static string? AskMachineName(string current)
    {
        using var form = CreateMachineNameDialog(current, out var input);
        return form.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
    }

    /// <summary>Builds the name editor without displaying a window or changing the saved identity.</summary>
    private static Form CreateMachineNameDialog(string current, out TextBox inputControl)
    {
        var form = Dialog("Save machine", 440, 125);
        var input = new TextBox { Left = 20, Top = 20, Width = 395, MaxLength = 128, Text = current };
        var ok = new Button { Text = "Save", Left = 225, Top = 75, Width = 90, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 325, Top = 75, Width = 90, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange([input, ok, cancel]); form.AcceptButton = ok; form.CancelButton = cancel;
        inputControl = input; return form;
    }

    /// <summary>Obtains one explicit typed setting without accepting a partially entered or cancelled value.</summary>
    public static string? AskText(string title, string prompt, string current, int maximumLength)
    {
        using var form = Dialog(title, 520, 160);
        var label = new Label { Text = prompt, Left = 20, Top = 20, Width = 480, Height = 25 };
        var input = new TextBox { Text = current, Left = 20, Top = 50, Width = 480, MaxLength = maximumLength };
        var ok = new Button { Text = "OK", Left = 310, Top = 110, Width = 90, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 410, Top = 110, Width = 90, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange([label, input, ok, cancel]); form.AcceptButton = ok; form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? input.Text : null;
    }

    /// <summary>Renders the real editors offscreen without activation or Windows input, and checks the actual unit-change controls.</summary>
    internal static object CapturePreview(string directory)
    {
        using var disk = CreateDiskSizeDialog(out var size, out var unit);
        // Real native combo/spin child text requires visible controls. Keep this
        // owned preview offscreen and nonactivating; do not fabricate control text.
        void Capture(Form form, string name)
        {
            ((PreviewForm)form).NonActivatingPreview = true; form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual; form.Location = new(-20000, -20000);
            form.Show(); form.Refresh(); using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(directory, name + ".png")); form.Hide();
        }
        Capture(disk, "disk-size-mib"); unit.SelectedIndex = 1;
        if (size.Value != .5m || RawDiskImage.SizeInBytes(size.Value, "GiB") != 512L * 1048576) throw new InvalidOperationException("Unit conversion changed disk capacity.");
        Capture(disk, "disk-size-gib"); unit.SelectedIndex = 0;
        if (size.Value != 512) throw new InvalidOperationException("Unit round trip changed disk capacity.");
        using var name = CreateMachineNameDialog("Mein DOS-PC", out var input); Capture(name, "machine-name");
        return new { passed = input.Text == "Mein DOS-PC", initialMiB = 512, convertedGiB = .5m, roundTripMiB = size.Value,
            nativeInputSent = false, offscreenNonActivatingWindows = true };
    }

    /// <summary>Creates a fixed-size modal editor using the user's Windows display font.</summary>
    private static Form Dialog(string title, int width, int height) => new PreviewForm
    {
        Text = title, ClientSize = new(width, height), FormBorderStyle = FormBorderStyle.FixedDialog,
        MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterScreen,
        Font = SystemFonts.MessageBoxFont, AutoScaleMode = AutoScaleMode.Dpi
    };
}
