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
/// This form only ever calls into <see cref="VrfInsights.Pipeline.FullPipeline"/>, which itself
/// only shells out to an already-built <c>vrfkit.exe</c> as an external process. No decoding or
/// payload-transform logic lives in this GUI.
/// </summary>
public sealed class MainForm : Form
{
    private readonly AppSettings _settings;

    private ComboBox _replayCombo = null!;
    private Button _refreshReplaysButton = null!;
    private Button _browseReplayButton = null!;

    private TextBox _vrfkitPathBox = null!;
    private Button _browseVrfkitButton = null!;

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
    private bool _isRunning;

    public MainForm()
    {
        _settings = AppSettings.Load();

        Text = "VALORANT Replay Insights";
        Width = 860;
        Height = 760;
        MinimumSize = new Size(720, 620);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        LoadSettingsIntoControls();
        RefreshReplayList();
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
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

        var label = new Label { Text = "Detected in your VALORANT replay folder:", Location = new Point(12, 24), AutoSize = true };

        _replayCombo = new ComboBox
        {
            Location = new Point(12, 46),
            Width = 560,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };

        _refreshReplaysButton = new Button
        {
            Text = "Refresh",
            Location = new Point(580, 45),
            Width = 80,
            Height = 24,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _refreshReplaysButton.Click += (_, _) => RefreshReplayList();

        _browseReplayButton = new Button
        {
            Text = "Browse...",
            Location = new Point(666, 45),
            Width = 90,
            Height = 24,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _browseReplayButton.Click += (_, _) => BrowseForReplay();

        group.Controls.Add(label);
        group.Controls.Add(_replayCombo);
        group.Controls.Add(_refreshReplaysButton);
        group.Controls.Add(_browseReplayButton);
        return group;
    }

    private GroupBox BuildVrfkitGroup()
    {
        var group = new GroupBox { Text = "2. vrfkit.exe (decodes the replay — https://github.com/yakisoba0728/vrfkit)", Dock = DockStyle.Fill };

        _vrfkitPathBox = new TextBox
        {
            Location = new Point(12, 28),
            Width = 630,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _browseVrfkitButton = new Button
        {
            Text = "Browse...",
            Location = new Point(650, 26),
            Width = 106,
            Height = 24,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _browseVrfkitButton.Click += (_, _) => BrowseForVrfkitExe();

        group.Controls.Add(_vrfkitPathBox);
        group.Controls.Add(_browseVrfkitButton);
        return group;
    }

    private GroupBox BuildOutputGroup()
    {
        var group = new GroupBox { Text = "3. Output folder (analysis JSON + the decoded export will be written here)", Dock = DockStyle.Fill };

        _outputDirBox = new TextBox
        {
            Location = new Point(12, 28),
            Width = 540,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _browseOutputButton = new Button
        {
            Text = "Browse...",
            Location = new Point(560, 26),
            Width = 90,
            Height = 24,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _browseOutputButton.Click += (_, _) => BrowseForOutputDirectory();

        _openOutputButton = new Button
        {
            Text = "Open folder",
            Location = new Point(656, 26),
            Width = 100,
            Height = 24,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _openOutputButton.Click += (_, _) => OpenOutputFolder();

        group.Controls.Add(_outputDirBox);
        group.Controls.Add(_browseOutputButton);
        group.Controls.Add(_openOutputButton);
        return group;
    }

    private GroupBox BuildOptionsGroup()
    {
        var group = new GroupBox { Text = "4. Options (vision cones are derived from position + facing, not extracted data — see README)", Dock = DockStyle.Fill };

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
            Width = 600,
            AutoSize = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(12, 50),
            Width = 812,
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

    // ---- Replay discovery / browsing -----------------------------------------------------

    private void RefreshReplayList()
    {
        _discoveredReplays = ReplayDiscovery.FindReplays().ToList();
        _replayCombo.Items.Clear();

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
        if (_replayCombo.Enabled && _replayCombo.SelectedIndex >= 0 && _replayCombo.SelectedIndex < _discoveredReplays.Count)
        {
            return _discoveredReplays[_replayCombo.SelectedIndex].FullPath;
        }

        return _manuallyBrowsedReplayPath;
    }

    private string? _manuallyBrowsedReplayPath;

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
            _replayCombo.Items.Clear();
            _replayCombo.Items.Add($"{Path.GetFileName(dialog.FileName)}  (manually selected)");
            _replayCombo.SelectedIndex = 0;
            _replayCombo.Enabled = true;
            _discoveredReplays = new List<ReplayDiscovery.ReplayFile>();
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

    // ---- Run ------------------------------------------------------------------------------

    private async Task RunButtonClickAsync()
    {
        if (_isRunning) return;

        string? vrfFile = SelectedReplayPath();
        string vrfkitPath = _vrfkitPathBox.Text.Trim();
        string outputDir = _outputDirBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(vrfFile) || !File.Exists(vrfFile))
        {
            MessageBox.Show(this, "Pick a replay (.vrf) file first.", "No replay selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(vrfkitPath) || !File.Exists(vrfkitPath))
        {
            MessageBox.Show(this, "Point this at your vrfkit executable first (Browse... in section 2).", "vrfkit not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            MessageBox.Show(this, "Choose an output folder first.", "No output folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SaveSettingsFromControls();

        _isRunning = true;
        _runButton.Enabled = false;
        _logBox.Clear();
        _progressBar.MarqueeAnimationSpeed = 30;
        SetStatus("Starting...");

        try
        {
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
            _progressBar.MarqueeAnimationSpeed = 0;
            _runButton.Enabled = true;
            _isRunning = false;
        }
    }

    // ---- Thread-safe UI updates -------------------------------------------------------------
    // Process.OutputDataReceived/ErrorDataReceived fire on background I/O threads, not the UI
    // thread, so every log/status update is marshaled through InvokeRequired/BeginInvoke rather
    // than assuming the caller is already on the UI thread.

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
