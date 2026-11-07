// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.DiskTools;

namespace DOSEmulator.DiskExplorer;

/// <summary>English offline disk browser; long storage operations run away from the UI thread and never start a VM.</summary>
internal sealed class ExplorerForm : Form
{
    internal const string WindowTitle = "DOSE Disk Explorer";
    internal static readonly string[] UiActions = ["Open VM…", "Open IMG…", "Close", "Import files…", "Import folder…", "Export selected…", "New folder…", "Save complete IMG…", "Cancel"];
    private readonly ToolStripButton[] _actions = UiActions.Select(text => new ToolStripButton(text)).ToArray();
    private readonly ToolStripStatusLabel _status = new("Shut down the guest and close DOSE, then open its VM or IMG here.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly ComboBox _machines = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _disks = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _volumes = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _encoding = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly ListView _files = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
    private readonly ToolStripLabel _path = new(@"\");
    private readonly ToolStripLabel _binding = new();
    private readonly CheckBox _replace = new() { Text = "Replace existing files", AutoSize = true, Padding = new(6, 6, 0, 0) };
    private readonly string? _vmRoot, _initial;
    private DiskSession? _session;
    private string _directory = "";
    private CancellationTokenSource? _cancel;
    private bool _busy, _updating, _closeRequested;
    /// <summary>Currently selected supported filesystem volume.</summary>
    private DiskVolume? Volume => _volumes.SelectedItem as DiskVolume;

    /// <summary>Builds ordinary native controls only when explicitly launched by the user.</summary>
    public ExplorerForm(string? vmRoot, string? initial)
    {
        _vmRoot = vmRoot is null ? null : Path.GetFullPath(vmRoot);
        _initial = initial;
        Text = WindowTitle;
        Size = new(1250, 780);
        MinimumSize = new(1050, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new("Segoe UI", 9);
        AutoScaleMode = AutoScaleMode.Dpi;
        var top = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        top.Items.AddRange(_actions);
        top.Items.Add(new ToolStripControlHost(_replace));
        var selectors = new TableLayoutPanel { Dock = DockStyle.Top, Height = 67, ColumnCount = 6, RowCount = 2, Padding = new(8) };
        selectors.ColumnStyles.Add(new(SizeType.Absolute, 36));
        selectors.ColumnStyles.Add(new(SizeType.Percent, 34));
        selectors.ColumnStyles.Add(new(SizeType.Absolute, 40));
        selectors.ColumnStyles.Add(new(SizeType.Percent, 33));
        selectors.ColumnStyles.Add(new(SizeType.Absolute, 52));
        selectors.ColumnStyles.Add(new(SizeType.Percent, 33));
        selectors.Controls.Add(new Label { Text = "VM", AutoSize = true }, 0, 0);
        selectors.Controls.Add(_machines, 1, 0);
        selectors.Controls.Add(new Label { Text = "Disk", AutoSize = true }, 2, 0);
        selectors.Controls.Add(_disks, 3, 0);
        selectors.Controls.Add(new Label { Text = "Volume", AutoSize = true }, 4, 0);
        selectors.Controls.Add(_volumes, 5, 0);
        var encodings = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        encodings.Controls.Add(new Label { Text = "DOS code page", AutoSize = true, Padding = new(0, 5, 0, 0) });
        _encoding.Items.AddRange(["437", "850"]);
        _encoding.SelectedIndex = 0;
        encodings.Controls.Add(_encoding);
        selectors.Controls.Add(encodings, 1, 1);
        var splitter = new SplitContainer { Dock = DockStyle.Fill, Size = new(1200, 600), SplitterDistance = 280, Panel1MinSize = 150, Panel2MinSize = 300 };
        splitter.Panel1.Controls.Add(_tree);
        splitter.Panel2.Controls.Add(_files);
        _files.Columns.Add("Name", 350);
        _files.Columns.Add("Type", 80);
        _files.Columns.Add("Size", 110, HorizontalAlignment.Right);
        _files.Columns.Add("Modified", 170);
        var navigation = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        navigation.Items.Add("Up", null, async (_, _) => await Navigate(GuestParent(_directory)));
        navigation.Items.Add("Refresh", null, async (_, _) => await Navigate(_directory));
        navigation.Items.Add(new ToolStripSeparator());
        navigation.Items.Add(_path);
        var binding = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Bottom };
        binding.Items.Add(_binding);
        var status = new StatusStrip();
        status.Items.AddRange([_status, _progress]);
        Controls.Add(splitter);
        Controls.Add(navigation);
        Controls.Add(selectors);
        Controls.Add(top);
        Controls.Add(binding);
        Controls.Add(status);
        _actions[0].Click += async (_, _) => await ChooseMachine();
        _actions[1].Click += async (_, _) => await ChooseImage();
        _actions[2].Click += (_, _) =>
        {
            CloseDisk();
            _updating = true; _disks.SelectedIndex = -1; _updating = false;
            _status.Text = "Disk closed. You can start DOSE now.";
        };
        _actions[3].Click += async (_, _) => await Import(false);
        _actions[4].Click += async (_, _) => await Import(true);
        _actions[5].Click += async (_, _) => await Export();
        _actions[6].Click += async (_, _) => await NewFolder();
        _actions[7].Click += async (_, _) => await SaveImage();
        _actions[8].Click += (_, _) => _cancel?.Cancel();
        _disks.SelectedIndexChanged += async (_, _) => { if (!_updating) await OpenSelectedDisk(); };
        _volumes.SelectedIndexChanged += async (_, _) => { if (!_updating) await Navigate(""); };
        _machines.SelectedIndexChanged += async (_, _) => { if (!_updating && _machines.SelectedItem is VmItem item) await LoadMachine(item.Path); };
        _encoding.SelectedIndexChanged += async (_, _) => { if (!_updating && _session is not null) await OpenSelectedDisk(); };
        _tree.AfterSelect += async (_, e) => { if (!_updating && e.Node?.Tag is string path) await Navigate(path); };
        _files.DoubleClick += async (_, _) => { if (_files.SelectedItems.Count == 1 && _files.SelectedItems[0].Tag is DiskEntry { IsDirectory: true } entry) await Navigate(entry.GuestPath); };
        _files.SelectedIndexChanged += (_, _) => UpdateActions();
        FormClosing += (_, e) => { if (_busy) { e.Cancel = true; _closeRequested = true; _cancel?.Cancel(); } else _session?.Dispose(); };
        Shown += async (_, _) =>
        {
            if (_vmRoot is not null && Directory.Exists(_vmRoot))
                foreach (string directory in Directory.EnumerateDirectories(_vmRoot))
                {
                    string machine = Path.Combine(directory, "machine.dospc.json");
                    if (File.Exists(machine)) _machines.Items.Add(new VmItem(Path.GetFileName(directory), machine));
                }
            if (_initial is not null)
            {
                if (_initial.EndsWith(".dospc.json", StringComparison.OrdinalIgnoreCase)) await LoadMachine(_initial);
                else await LoadImage(_initial);
            }
        };
        UpdateActions();
    }

    /// <summary>Disables all state-changing controls while a single cancellable worker owns the session.</summary>
    private void UpdateActions()
    {
        for (int n = 0; n < _actions.Length; n++) _actions[n].Enabled = !_busy;
        _actions[2].Enabled = !_busy && _session is not null;
        foreach (int n in new[] { 3, 4, 6 }) _actions[n].Enabled = !_busy && _session?.CanImport == true && Volume is not null;
        _actions[5].Enabled = !_busy && _session is not null && _files.SelectedItems.Count > 0;
        _actions[7].Enabled = !_busy && _session is not null;
        _actions[8].Enabled = _busy;
        foreach (Control control in new Control[] { _tree, _files, _machines, _disks, _volumes, _encoding, _replace }) control.Enabled = !_busy;
    }

    /// <summary>Runs checked storage work with responsive cancellation and one consistent error surface.</summary>
    private async Task Run(Func<CancellationToken, IProgress<TransferProgress>, Task> action)
    {
        if (_busy) return;
        _busy = true;
        using var cancellation = _cancel = new();
        _progress.Visible = true;
        UpdateActions();
        try
        {
            var progress = new Progress<TransferProgress>(value => { if (!IsDisposed) _status.Text = value.Message; });
            await action(cancellation.Token, progress);
        }
        catch (OperationCanceledException) { _status.Text = "Cancelled. Unpublished changes were discarded."; }
        catch (Exception error)
        {
            string message = (error.HResult & 0xFFFF) is 32 or 33 ? "The disk is in use. Close DOSE and other disk tools, then retry." : error.Message;
            _status.Text = message;
            MessageBox.Show(this, message, WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cancel = null;
            _busy = false;
            _progress.Visible = false;
            UpdateActions();
            if (_closeRequested) Close();
        }
    }

    /// <summary>Selects the current VM's actual image/COW bindings, including secondary hard disks and floppies.</summary>
    private async Task LoadMachine(string path)
    {
        if (_busy) return;
        try
        {
            var disks = DiskSelection.FromMachine(path);
            if (disks.Length == 0) throw new IOException("This machine has no file-backed hard disk or floppy.");
            _updating = true;
            string fullPath = Path.GetFullPath(path);
            var machineItem = _machines.Items.Cast<VmItem>().FirstOrDefault(item => item.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
            if (machineItem is null)
            {
                machineItem = new(Path.GetFileName(Path.GetDirectoryName(fullPath))!, fullPath);
                _machines.Items.Add(machineItem);
            }
            _machines.SelectedItem = machineItem;
            _disks.Items.Clear();
            _disks.Items.AddRange(disks);
            _disks.SelectedIndex = 0;
            _updating = false;
            await OpenSelectedDisk();
        }
        catch (Exception error) { _updating = false; MessageBox.Show(this, error.Message, WindowTitle); }
    }

    /// <summary>Binds a raw image to its adjacent changes file without creating the journal prematurely.</summary>
    private async Task LoadImage(string path)
    {
        _updating = true;
        _machines.SelectedIndex = -1;
        _disks.Items.Clear();
        _disks.Items.Add(DiskSelection.FromImage(path));
        _disks.SelectedIndex = 0;
        _updating = false;
        await OpenSelectedDisk();
    }

    /// <summary>Opens media away from the UI thread; no host mount driver is required.</summary>
    private async Task OpenSelectedDisk()
    {
        if (_busy || _disks.SelectedItem is not DiskSelection selection) return;
        int codePage = int.Parse((string)_encoding.SelectedItem!);
        await Run(async (_, _) =>
        {
            CloseDisk();
            _status.Text = "Opening and checking disk…";
            _session = await Task.Run(() => new DiskSession(selection, codePage));
            _updating = true;
            _volumes.Items.AddRange(_session.Volumes.Cast<object>().ToArray());
            _volumes.SelectedIndex = 0;
            _updating = false;
            _binding.Text = selection.CowPath is null ? "Read-only VM attachment" : "Changes: " + selection.CowPath;
            await ShowDirectory("");
        });
    }

    /// <summary>Releases all media locks and clears the displayed directory state.</summary>
    private void CloseDisk()
    {
        _session?.Dispose(); _session = null;
        _updating = true;
        _volumes.Items.Clear(); _files.Items.Clear(); _tree.Nodes.Clear();
        _updating = false;
        _binding.Text = ""; _directory = ""; _path.Text = @"\";
        UpdateActions();
    }

    /// <summary>Changes directory only when no transfer owns the current session.</summary>
    private async Task Navigate(string path)
    {
        if (_busy || _session is null || Volume is null) return;
        await Run(async (_, _) => await ShowDirectory(path));
    }

    /// <summary>Shows directory contents and immediate children without recursively walking an entire disk.</summary>
    private async Task ShowDirectory(string path)
    {
        if (_session is null || Volume is not { } volume) return;
        var entries = await Task.Run(() => _session.List(volume, path));
        _directory = path;
        _path.Text = @"\" + path;
        _updating = true;
        _files.BeginUpdate();
        _files.Items.Clear();
        foreach (var entry in entries)
        {
            var item = new ListViewItem(entry.Name) { Tag = entry };
            item.SubItems.Add(entry.IsDirectory ? "Folder" : "File");
            item.SubItems.Add(entry.IsDirectory ? "" : entry.Length.ToString("N0"));
            item.SubItems.Add(entry.Modified.ToString("g"));
            _files.Items.Add(item);
        }
        _files.EndUpdate();
        _tree.Nodes.Clear();
        var root = _tree.Nodes.Add(@"\");
        root.Tag = "";
        TreeNode node = root;
        string current = "";
        foreach (string part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? part : current + "\\" + part;
            node = node.Nodes.Add(part); node.Tag = current;
        }
        foreach (var entry in entries.Where(e => e.IsDirectory)) node.Nodes.Add(new TreeNode(entry.Name) { Tag = entry.GuestPath });
        _tree.ExpandAll();
        _updating = false;
        _status.Text = $"{entries.Length} items — offline disk access; close this disk before starting DOSE.";
    }

    /// <summary>Offers the user an explicit machine-definition file picker.</summary>
    private async Task ChooseMachine()
    {
        using var dialog = new OpenFileDialog { Title = "Open DOSE machine", Filter = "DOSE machine (*.dospc.json)|*.dospc.json", InitialDirectory = _vmRoot };
        if (dialog.ShowDialog(this) == DialogResult.OK) await LoadMachine(dialog.FileName);
    }

    /// <summary>Offers the user an explicit raw-image file picker.</summary>
    private async Task ChooseImage()
    {
        using var dialog = new OpenFileDialog { Title = "Open raw disk image", Filter = "Raw disk images|*.img;*.ima;*.raw|All files|*.*", InitialDirectory = _vmRoot };
        if (dialog.ShowDialog(this) == DialogResult.OK) await LoadImage(dialog.FileName);
    }

    /// <summary>Imports explicit user-selected files or one folder, with replacement opt-in and cancellable verification.</summary>
    private async Task Import(bool folder)
    {
        if (_session is null || Volume is not { } volume) return;
        string[] paths;
        if (folder)
        {
            using var dialog = new FolderBrowserDialog { Description = "Choose a host folder to import" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            paths = [dialog.SelectedPath];
        }
        else
        {
            using var dialog = new OpenFileDialog { Title = "Import host files", Multiselect = true, Filter = "All files|*.*" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            paths = dialog.FileNames;
        }
        bool overwrite = _replace.Checked;
        string directory = _directory;
        await Run(async (cancel, progress) =>
        {
            await Task.Run(() => _session.Import(volume, paths, directory, overwrite, cancel, progress));
            await ShowDirectory(directory);
            _status.Text = "Import saved and verified. The previous changes file was kept as a backup.";
        });
    }

    /// <summary>Publishes selected guest entries into a new user-chosen host export directory.</summary>
    private async Task Export()
    {
        if (_session is null || Volume is not { } volume) return;
        string[] paths = _files.SelectedItems.Cast<ListViewItem>().Select(item => ((DiskEntry)item.Tag!).GuestPath).ToArray();
        using var dialog = new FolderBrowserDialog { Description = "Choose where the new DOSE export folder will be created" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string destination = Path.Combine(dialog.SelectedPath, "DOSE Export " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        await Run(async (cancel, progress) =>
        {
            await Task.Run(() => _session.Export(volume, paths, destination, cancel, progress));
            _status.Text = "Export saved and verified: " + destination;
        });
    }

    /// <summary>Lets the user save a verified standalone image under a new filename.</summary>
    private async Task SaveImage()
    {
        if (_session is null) return;
        using var dialog = new SaveFileDialog { Title = "Save complete merged image", Filter = "Raw disk image|*.img", FileName = "Disk-complete.img" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await Run(async (cancel, progress) =>
        {
            await Task.Run(() => _session.SaveMergedImage(dialog.FileName, cancel, progress));
            _status.Text = "Complete image saved and verified: " + dialog.FileName;
        });
    }

    /// <summary>Collects a guest folder name and submits a cancellable journal transaction.</summary>
    private async Task NewFolder()
    {
        if (_session is null || Volume is not { } volume) return;
        using var prompt = new Form { Text = "New guest folder", ClientSize = new(420, 110), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var input = new TextBox { Left = 12, Top = 14, Width = 395 };
        var ok = new Button { Text = "Create", DialogResult = DialogResult.OK, Left = 240, Top = 60, Width = 80 };
        var close = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 328, Top = 60, Width = 80 };
        prompt.Controls.AddRange([input, ok, close]); prompt.AcceptButton = ok; prompt.CancelButton = close;
        if (prompt.ShowDialog(this) != DialogResult.OK) return;
        string path = _directory.Length == 0 ? input.Text : _directory + "\\" + input.Text;
        await Run(async (cancel, _) => { await Task.Run(() => _session.CreateDirectory(volume, path, cancel)); await ShowDirectory(_directory); });
    }

    /// <summary>Returns a guest parent without treating the path as a host drive.</summary>
    private static string GuestParent(string path) => path.Contains('\\') ? path[..path.LastIndexOf('\\')] : "";
    private sealed record VmItem(string Name, string Path) { 
        /// <summary>Displays the VM folder name without exposing its full configuration path.</summary>
        public override string ToString() => Name; }
}
