using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using VrfInsights.Pipeline;

namespace VrfInsights.Gui;

/// <summary>
/// The whole GUI in one hand-coded form (no designer/.resx file — every control is created and
/// positioned in code, so nothing here depends on a designer serializer this project can't
/// verify at build time).
///
/// This form only ever calls into <see cref="VrfInsights.Pipeline.FullPipeline"/> and
/// <see cref="VrfInsights.Pipeline.VrfkitBootstrapper"/>, which themselves only launch
/// already-built or freshly-built <c>vrfkit</c> (and, for the bootstrapper, <c>git</c>/
/// <c>cargo</c>) as ordinary external processes. No decoding or payload-transform logic lives in
/// this GUI, and vrfkit's own source is never modified — only fetched and compiled as published.
/// </summary>
public sealed class MainForm : Form
{
    // Every container that has right-anchored children (GroupBox/Panel) is explicitly sized to
    // this width *before* those children are added. WinForms computes an Anchor="Right" child's
    // fixed distance from its parent's right edge at the moment the child is parented — if the
    // parent is still at its default ~100px width then (because it hasn't been through a Dock
    // layout pass yet), that distance comes out wrong and the child can end up positioned way
    // outside the visible window once the parent is later resized to its real width. Giving the
    // parent its real width first avoids that entirely.
    private const int ContentWidth = 800;

    private readonly AppSettings _settings;

    private ComboBox _replayCombo = null!;
    private Button _refreshReplaysButton = null!;
    private Button _browseReplayButton = null!;

    private TextBox _vrfkitPathBox = null!;
    private Button _browseVrfkitButton = null!;
    private Button _autoSetupVrfkitButton = null!;

    private TextBox _outputDirBox = null!;
    private Button _browseOutputButton = null!;
    private Button _openOutputButton = null!;

    private NumericUpDown _fovUpDown = null!;
    private NumericUpDown _rangeUpDown = null!;
    private NumericUpDown _eyeHeightUpDown = null!;
    private CheckBox _withVisionCheckBox = null!;

    private Button _runButton = null!;
    private ProgressBar _progressBar = null!;
    private Label _statusLabel = null!;
    private TextBox _logBox = null!;

    private List<ReplayDiscovery.ReplayFile> _discoveredReplays = new();
    private string? _manuallyBrowsedReplayPath;
    private bool _isBusy;

    public MainForm()
    {
        _settings = AppSettings.Load();

        Text = "VALORANT Replay Insights";
        Width = 900;
        Height = 780;
        MinimumSize = new Size(760, 640);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        LoadSettingsIntoControls();
        RefreshReplayList();
        TryAutoDetectVrfkit();
    }

    // ---- Layout -----------------------------------------------------------------------

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildReplayGroup(), 0, 0);
        root.Controls.Add(BuildVrfkitGroup(), 0, 1);
        root.Controls.Add(BuildOutputGroup(), 0, 2);
        root.Controls.Add(BuildOptionsGroup(), 0, 3);
        root.Controls.Add(BuildRunPanel(), 0, 4);
        root.Controls.Add(BuildLogGroup(), 0, 5);

        Controls.Add(root);
    }

    private GroupBox BuildReplayGroup()
    {
        var group = new GroupBox { Text = "1. Replay file (.vrf)", Dock = DockStyle.Fill };
        group.Size = new Size(ContentWidth, 90); // see ContentWidth's comment — must happen before adding anchored children

        var label = new Label { Text = "Detected in your VALORANT replay folder:", Location = new Point(12, 24), AutoSize = true };

        // Left edge fixed at 12; right edge computed backwards from the two right-anchored
        // buttons so everything lines up inside ContentWidth with a 12px margin on both sides.
        _browseReplayButton = new Button
        {
            Text = "Browse...",
            Location = new Point(ContentWidth - 12 - 100, 45),
            Width = 100,
            Height = 24,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _browseReplayButton.Click += (_, _) => BrowseForReplay();

        _refreshReplaysButton = new Button
        {
            Text = "Refresh",
            Location = new Point(_browseReplayButton.Location.X - 8 - 80, 45),
            Width = 80,
            Height = 24,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _refreshReplaysButton.Click += (_, _) => RefreshReplayList();

        _replayCombo = new ComboBox
        {
            Location = new Point(12, 46),
            Width = _refreshReplaysButton.Location.X - 8 - 12,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };

        group.Controls.Add(label);
        group.Controls.Add(_replayCombo);
        group.Controls.Add(_refreshReplaysButton);
        group.Controls.Add(_browseReplayButton);
        return group;
    }

    private GroupBox BuildVrfkitGroup()
    {
        var group = new GroupBox { Text = "2. vrfkit (decodes the replay — https://github.com/yakisoba0728/vrfkit)", Dock = DockStyle.Fill };
        group.Size = new Size(ContentWidth, 130);

        var pathLabel = new Label { Text = "vrfkit executable path:", Location = new Point(12, 24), AutoSize = true };

        _browseVrfkitButton = new Button
        {
            Text = "Browse...",
            Location = new Point(ContentWidth - 12 - 100, 44),
            Width = 100,
            Height = 24,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _browseVrfkitButton.Click += (_, _) => BrowseForVrfkitExe();

        _vrfkitPathBox = new TextBox
        {
            Location = new Point(12, 46),
            Width = _browseVrfkitButton.Location.X - 8 - 12,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _autoSetupVrfkitButton = new Button
        {
            Text = "Set up vrfkit automatically (download + build)",
            Location = new Point(12, 82),
            Width = 320,
            Height = 28,
        };
        _autoSetupVrfkitButton.Click += async (_, _) => await AutoSetupVrfkitAsync();

        var autoSetupHint = new Label
        {
            Text = "First time only — needs Git and Rust installed. After that, it's remembered.",
            Location = new Point(344, 90),
            Width = ContentWidth - 12 - 344,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            AutoSize = false,
            ForeColor = SystemColors.GrayText,
        };

        group.Controls.Add(pathLabel);
        group.Controls.Add(_vrfkitPathBox);
        group.Controls.Add(_browseVrfkitButton);
        group.Controls.Add(_autoSetupVrfkitButton);
        group.Controls.Add(autoSetupHint);
        return group;
    }

    private GroupBox BuildOutputGroup()
    {
        var group = new GroupBox { Text = "3. Output folder (analysis JSON + the decoded export will be written here)", Dock = DockStyle.Fill };
        group.Size = new Size(ContentWidth, 70);

        _openOutputButton = new Button
        {
            Text = "Open folder",
            Location = new Point(ContentWidth - 12 - 100, 26),
            Width = 100,
            Height = 24,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _openOutputButton.Click += (_, _) => OpenOutputFolder();

        _browseOutputButton = new Button
        {
            Text = "Browse...",
            Location = new Point(_openOutputButton.Location.X - 8 - 90, 26),
            Width = 90,
            Height = 24,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _browseOutputButton.Click += (_, _) => BrowseForOutputDirectory();

        _outputDirBox = new TextBox
        {
            Location = new Point(12, 28),
            Width = _browseOutputButton.Location.X - 8 - 12,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        group.Controls.Add(_outputDirBox);
        group.Controls.Add(_browseOutputButton);
        group.Controls.Add(_openOutputButton);
        return group;
    }

    private GroupBox BuildOptionsGroup()
    {
        var group = new GroupBox { Text = "4. Options (vision cones are derived from position + facing, not extracted data — see README)", Dock = DockStyle.Fill };
        group.Size = new Size(ContentWidth, 120);

        var fovLabel = new Label { Text = "Field of view (degrees):", Location = new Point(12, 28), AutoSize = true };
        _fovUpDown = new NumericUpDown { Location = new Point(180, 25), Width = 80, Minimum = 1, Maximum = 179, DecimalPlaces = 1, Increment = 1 };

        var rangeLabel = new Label { Text = "Vision range (cm):", Location = new Point(280, 28), AutoSize = true };
        _rangeUpDown = new NumericUpDown { Location = new Point(420, 25), Width = 90, Minimum = 0, Maximum = 1_000_000, DecimalPlaces = 0, Increment = 500 };

        var eyeHeightLabel = new Label { Text = "Eye height (cm):", Location = new Point(12, 60), AutoSize = true };
        _eyeHeightUpDown = new NumericUpDown { Location = new Point(180, 57), Width = 80, Minimum = 0, Maximum = 500, DecimalPlaces = 1, Increment = 1 };

        _withVisionCheckBox = new CheckBox
        {
            Text = "Compute vision cones (can be slow/large on long replays)",
            Location = new Point(12, 90),
            AutoSize = true,
        };

        group.Controls.Add(fovLabel);
        group.Controls.Add(_fovUpDown);
        group.Controls.Add(rangeLabel);
        group.Controls.Add(_rangeUpDown);
        group.Controls.Add(eyeHeightLabel);
        group.Controls.Add(_eyeHeightUpDown);
        group.Controls.Add(_withVisionCheckBox);
        return group;
    }

    private Panel BuildRunPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Size = new Size(ContentWidth, 80);

        _runButton = new Button
        {
            Text = "Run (decode + analyze)",
            Location = new Point(12, 10),
            Width = 200,
            Height = 34,
        };
        _runButton.Click += async (_, _) => await RunButtonClickAsync();

        _statusLabel = new Label
        {
            Text = "Ready.",
            Location = new Point(224, 18),
            Width = ContentWidth - 12 - 224,
            Height = 20,
            AutoSize = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(12, 50),
            Width = ContentWidth - 12 - 12,
            Height = 18,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 0,
        };

        panel.Controls.Add(_runButton);
        panel.Controls.Add(_statusLabel);
        panel.Controls.Add(_progressBar);
        return panel;
    }

    private GroupBox BuildLogGroup()
    {
        var group = new GroupBox { Text = "Log", Dock = DockStyle.Fill };
        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9f),
        };
        group.Controls.Add(_logBox);
        return group;
    }

    // ---- Settings -----------------------------------------------------------------------

    private void LoadSettingsIntoControls()
    {
        _vrfkitPathBox.Text = _settings.VrfkitExePath ?? "";
        _outputDirBox.Text = _settings.LastOutputDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VrfInsights");
        _fovUpDown.Value = ClampDecimal(_settings.FovDegrees, _fovUpDown);
        _rangeUpDown.Value = ClampDecimal(_settings.RangeCm, _rangeUpDown);
        _eyeHeightUpDown.Value = ClampDecimal(_settings.EyeHeightCm, _eyeHeightUpDown);
        _withVisionCheckBox.Checked = _settings.WithVision;
    }

    private static decimal ClampDecimal(double value, NumericUpDown target)
    {
        decimal d = (decimal)value;
        if (d < target.Minimum) return target.Minimum;
        if (d > target.Maximum) return target.Maximum;
        return d;
    }

    private void SaveSettingsFromControls()
    {
        _settings.VrfkitExePath = _vrfkitPathBox.Text.Trim();
        _settings.LastOutputDirectory = _outputDirBox.Text.Trim();
        _settings.FovDegrees = (double)_fovUpDown.Value;
        _settings.RangeCm = (double)_rangeUpDown.Value;
        _settings.EyeHeightCm = (double)_eyeHeightUpDown.Value;
        _settings.WithVision = _withVisionCheckBox.Checked;
        _settings.Save();
    }

    /// <summary>On startup, if no vrfkit path is remembered yet, check whether one was already
    /// built by a previous <see cref="VrfkitBootstrapper.SetupAsync"/> run (or lives at the
    /// bootstrapper's well-known location for some other reason) and use it silently — no
    /// browsing, no button click needed.</summary>
    private void TryAutoDetectVrfkit()
    {
        if (!string.IsNullOrWhiteSpace(_vrfkitPathBox.Text) && File.Exists(_vrfkitPathBox.Text))
        {
            return;
        }

        string? found = VrfkitBootstrapper.FindExisting();
        if (found is not null)
        {
            _vrfkitPathBox.Text = found;
            SaveSettingsFromControls();
        }
    }

    // ---- Replay discovery / browsing -----------------------------------------------------

    private void RefreshReplayList()
    {
        _discoveredReplays = ReplayDiscovery.FindReplays().ToList();
        _replayCombo.Items.Clear();
        _manuallyBrowsedReplayPath = null;

        if (_discoveredReplays.Count == 0)
        {
            _replayCombo.Items.Add("(none found — use Browse...)");
            _replayCombo.SelectedIndex = 0;
            _replayCombo.Enabled = false;
            return;
        }

        _replayCombo.Enabled = true;
        foreach (var replay in _discoveredReplays)
        {
            _replayCombo.Items.Add($"{replay.FileName}  ({replay.LastWriteTimeUtc.ToLocalTime():g})");
        }

        _replayCombo.SelectedIndex = 0;
    }

    private string? SelectedReplayPath()
    {
        if (_manuallyBrowsedReplayPath is not null)
        {
            return _manuallyBrowsedReplayPath;
        }

        if (_replayCombo.Enabled && _replayCombo.SelectedIndex >= 0 && _replayCombo.SelectedIndex < _discoveredReplays.Count)
        {
            return _discoveredReplays[_replayCombo.SelectedIndex].FullPath;
        }

        return null;
    }

    private void BrowseForReplay()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select a VALORANT replay file",
            Filter = "VALORANT replay files (*.vrf)|*.vrf|All files (*.*)|*.*",
            InitialDirectory = ReplayDiscovery.DefaultReplayDirectory() is { } dir && Directory.Exists(dir) ? dir : "",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _manuallyBrowsedReplayPath = dialog.FileName;
            _discoveredReplays = new List<ReplayDiscovery.ReplayFile>();
            _replayCombo.Items.Clear();
            _replayCombo.Items.Add($"{Path.GetFileName(dialog.FileName)}  (manually selected)");
            _replayCombo.Enabled = true;
            _replayCombo.SelectedIndex = 0;
        }
    }

    private void BrowseForVrfkitExe()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select vrfkit(.exe)",
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _vrfkitPathBox.Text = dialog.FileName;
            SaveSettingsFromControls();
        }
    }

    private void BrowseForOutputDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where the decoded export and analysis JSON should be written",
        };

        if (!string.IsNullOrWhiteSpace(_outputDirBox.Text) && Directory.Exists(_outputDirBox.Text))
        {
            dialog.SelectedPath = _outputDirBox.Text;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputDirBox.Text = dialog.SelectedPath;
        }
    }

    private void OpenOutputFolder()
    {
        string path = _outputDirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "Pick an output folder first.", "No output folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"Couldn't open that folder: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- vrfkit automatic setup ------------------------------------------------------------

    private async Task AutoSetupVrfkitAsync()
    {
        if (_isBusy) return;

        SetBusy(true, "Setting up vrfkit...");
        _logBox.Clear();

        try
        {
            VrfkitBootstrapper.SetupResult result = await VrfkitBootstrapper.SetupAsync(
                onStatus: SetStatus,
                onLogLine: AppendLog);

            if (result.Success && result.VrfkitExePath is not null)
            {
                _vrfkitPathBox.Text = result.VrfkitExePath;
                SaveSettingsFromControls();
                MessageBox.Show(this, "vrfkit is set up and ready to use.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, result.FailureReason ?? "Setting up vrfkit failed — see the log for details.", "Setup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Failed.");
            AppendLog($"ERROR: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Something went wrong", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, "Ready.");
        }
    }

    /// <summary>Resolves a usable vrfkit executable path for Run: whatever's already typed in
    /// the box if it exists, otherwise a previous build at the well-known location, otherwise
    /// runs setup right now (so Run "just works" the first time too, without a separate click).
    /// Returns null (having already shown the reason) if no usable vrfkit could be found.</summary>
    private async Task<string?> ResolveVrfkitPathAsync()
    {
        string typed = _vrfkitPathBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(typed) && File.Exists(typed))
        {
            return typed;
        }

        string? existing = VrfkitBootstrapper.FindExisting();
        if (existing is not null)
        {
            _vrfkitPathBox.Text = existing;
            return existing;
        }

        AppendLog("No vrfkit found yet — setting it up automatically (this only happens once)...");
        VrfkitBootstrapper.SetupResult result = await VrfkitBootstrapper.SetupAsync(onStatus: SetStatus, onLogLine: AppendLog);
        if (result.Success && result.VrfkitExePath is not null)
        {
            _vrfkitPathBox.Text = result.VrfkitExePath;
            return result.VrfkitExePath;
        }

        MessageBox.Show(this, result.FailureReason ?? "Couldn't set up vrfkit automatically — see the log for details.", "vrfkit not available", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return null;
    }

    // ---- Run ------------------------------------------------------------------------------

    private async Task RunButtonClickAsync()
    {
        if (_isBusy) return;

        string? vrfFile = SelectedReplayPath();
        if (string.IsNullOrWhiteSpace(vrfFile) || !File.Exists(vrfFile))
        {
            MessageBox.Show(this, "Pick a replay (.vrf) file first.", "No replay selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string outputDir = _outputDirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            MessageBox.Show(this, "Choose an output folder first.", "No output folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true, "Starting...");
        _logBox.Clear();

        try
        {
            string? vrfkitPath = await ResolveVrfkitPathAsync();
            if (vrfkitPath is null)
            {
                SetStatus("Ready.");
                return;
            }

            SaveSettingsFromControls();

            string exportDir = Path.Combine(outputDir, "export");
            var options = new FullPipeline.FullPipelineOptions(
                VrfFilePath: vrfFile,
                VrfkitExePath: vrfkitPath,
                ExportDirectory: exportDir,
                OutputDirectory: outputDir,
                AnalysisOptions: new AnalysisPipelineOptions(
                    FovDegrees: (double)_fovUpDown.Value,
                    RangeCm: (double)_rangeUpDown.Value,
                    EyeHeightCm: (double)_eyeHeightUpDown.Value,
                    WithVision: _withVisionCheckBox.Checked));

            FullPipeline.FullPipelineResult result = await FullPipeline.RunAsync(
                options,
                onStatus: SetStatus,
                onLogLine: AppendLog);

            if (result.ExportSucceeded)
            {
                SetStatus("Done.");
                AppendLog($"Finished. Analysis JSON written to: {Path.GetFullPath(outputDir)}");
                MessageBox.Show(this, "Finished! Your analysis JSON files are in the output folder.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                SetStatus($"vrfkit export failed (exit code {result.ExportExitCode}).");
                MessageBox.Show(this, "vrfkit couldn't decode that replay — check the log for details.", "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Failed.");
            AppendLog($"ERROR: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Something went wrong", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, "Ready.");
        }
    }

    // ---- Busy state / thread-safe UI updates ------------------------------------------------
    // Process.OutputDataReceived/ErrorDataReceived fire on background I/O threads, not the UI
    // thread, so every log/status update is marshaled through InvokeRequired/BeginInvoke rather
    // than assuming the caller is already on the UI thread.

    private void SetBusy(bool busy, string status)
    {
        _isBusy = busy;
        _runButton.Enabled = !busy;
        _autoSetupVrfkitButton.Enabled = !busy;
        _progressBar.MarqueeAnimationSpeed = busy ? 30 : 0;
        SetStatus(status);
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new MethodInvoker(() => SetStatus(text)));
            return;
        }

        _statusLabel.Text = text;
    }

    private void AppendLog(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new MethodInvoker(() => AppendLog(line)));
            return;
        }

        _logBox.AppendText(line + Environment.NewLine);
    }
}
