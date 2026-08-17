using System.Diagnostics;

namespace CodexSessionHotSync;

internal enum RailState
{
    Ready,
    Busy,
    Success,
    Error,
}

internal enum WorkspaceLayoutMode
{
    Wide,
    Compact,
    Narrow,
}

internal sealed class CommandTextControl : Control
{
    public CommandTextControl()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            ClientRectangle,
            ForeColor,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix |
            TextFormatFlags.NoPadding);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Invalidate();
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        Invalidate();
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        Invalidate();
    }
}

internal sealed class MainForm : Form
{
    private readonly SessionSyncService _syncService = new();
    private readonly ChatPackService _chatPackService = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly ThemePalette _theme = ThemePalette.Current();
    private readonly ToolTip _toolTip = new();
    private readonly TextBox _codexHomeBox = new();
    private readonly TextBox _backupRootBox = new();
    private readonly ComboBox _providerCombo = new();
    private readonly ModernCheckBox _includeArchivedCheck = new();
    private readonly RichTextBox _log = new();
    private readonly StatusGlyphControl _statusIcon = new();
    private readonly CommandTextControl _statusTitle = new();
    private readonly CommandTextControl _statusDetail = new();
    private readonly LinkLabel _footerLabel = new();
    private readonly ProgressBar _statusProgress = new();
    private readonly SyncTopologyPanel _topology;
    private readonly System.Windows.Forms.Timer _responsiveLayoutTimer = new() { Interval = 70 };
    private SurfacePanel _statusRail = null!;
    private SurfacePanel _commandStrip = null!;
    private Panel _viewport = null!;
    private TableLayoutPanel _root = null!;
    private TableLayoutPanel _commandLayout = null!;
    private TableLayoutPanel _statusLayout = null!;
    private Control _homeGroup = null!;
    private Control _providerGroup = null!;
    private Control _backupGroup = null!;
    private Control _optionsGroup = null!;
    private Control _commandDividerOne = null!;
    private Control _commandDividerTwo = null!;
    private Control _commandDividerThree = null!;
    private Control _statusText = null!;
    private Button _syncButton = null!;
    private Button _refreshButton = null!;
    private Button _openBackupButton = null!;
    private Button _browseButton = null!;
    private Button _browseBackupButton = null!;
    private Button _importButton = null!;
    private Button _exportButton = null!;
    private AppSettings _settings = new();
    private CancellationTokenSource? _refreshCancellation;
    private bool _busy;
    private bool _dpiLayoutReady;
    private bool _updatingProvider;
    private bool _providerSelectionExplicit;
    private string? _lastBackupDirectory;
    private InspectionSnapshot? _lastSnapshot;
    private WorkspaceLayoutMode? _layoutMode;

    public MainForm()
    {
        _topology = new SyncTopologyPanel(_theme);
        InitializeWindow();
        BuildLayout();
        ApplyTheme();
        LoadSettings();
        WireEvents();
    }

    private void InitializeWindow()
    {
        Text = "Codex 会话热同步";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 500);
        ClientSize = new Size(1220, 800);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
        KeyPreview = true;
        DoubleBuffered = true;
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? BrandLogoFactory.CreateIcon();
        }
        catch
        {
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BeginInvoke(new Action(() =>
        {
            FitWindowToWorkingArea();
            ApplyResponsiveLayout(true);
            Invalidate(true);
        }));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _dpiLayoutReady = true;
        FitWindowToWorkingArea();
        ApplyResponsiveLayout(true);
    }

    private void BuildLayout()
    {
        SuspendLayout();
        _viewport = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = _theme.Background,
        };
        _root = new TableLayoutPanel
        {
            Dock = DockStyle.None,
            Padding = new Padding(18, 12, 18, 14),
            ColumnCount = 1,
            RowCount = 7,
            BackColor = _theme.Background,
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 93));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 270));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        _root.Controls.Add(BuildHeader(), 0, 0);
        _root.Controls.Add(BuildCommandStrip(), 0, 1);
        _root.Controls.Add(BuildTopology(), 0, 2);
        _root.Controls.Add(BuildStatusRail(), 0, 3);
        _root.Controls.Add(BuildLogHeader(), 0, 4);
        _root.Controls.Add(BuildLogPanel(), 0, 5);
        _root.Controls.Add(_footerLabel, 0, 6);
        _viewport.Controls.Add(_root);
        Controls.Add(_viewport);
        _responsiveLayoutTimer.Tick += (_, _) =>
        {
            _responsiveLayoutTimer.Stop();
            ApplyResponsiveLayout();
        };
        _viewport.Resize += (_, _) => QueueResponsiveLayout();
        ResizeEnd += (_, _) =>
        {
            _responsiveLayoutTimer.Stop();
            ApplyResponsiveLayout();
        };
        ApplyResponsiveLayout(true);
        ResumeLayout(true);
    }

    private void ApplyResponsiveLayout(bool force = false)
    {
        if (_viewport is null || _root is null || _commandLayout is null || _statusLayout is null)
        {
            return;
        }

        int viewportWidth = Math.Max(1, _viewport.ClientSize.Width);
        WorkspaceLayoutMode mode = ResolveLayoutMode(
            viewportWidth,
            _dpiLayoutReady ? DeviceDpi : 96);
        bool modeChanged = _layoutMode != mode;
        if (force || modeChanged)
        {
            ConfigureCommandLayout(mode);
            ConfigureStatusLayout(mode);
            _root.Padding = mode == WorkspaceLayoutMode.Wide
                ? ScalePadding(18, 12, 18, 14)
                : ScalePadding(12, 10, 12, 12);
            _root.RowStyles[0].Height = ScaleLogical(mode == WorkspaceLayoutMode.Wide ? 93 : 86);
            _root.RowStyles[1].Height = ScaleLogical(mode switch
            {
                WorkspaceLayoutMode.Wide => 128,
                WorkspaceLayoutMode.Compact => 218,
                _ => 380,
            });
            _root.RowStyles[2].Height = ScaleLogical(mode == WorkspaceLayoutMode.Wide ? 270 : 242);
            _root.RowStyles[3].Height = ScaleLogical(mode == WorkspaceLayoutMode.Wide ? 96 : 148);
            _layoutMode = mode;
        }

        int minimumContentWidth = ScaleLogical(760);
        int minimumContentHeight = ScaleLogical(mode switch
        {
            WorkspaceLayoutMode.Wide => 760,
            WorkspaceLayoutMode.Compact => 860,
            _ => 1020,
        });
        bool needsVerticalScroll = minimumContentHeight > _viewport.ClientSize.Height;
        int verticalScrollWidth = needsVerticalScroll ? ScaleLogical(17) : 0;
        int contentWidth = Math.Max(minimumContentWidth, viewportWidth - verticalScrollWidth - 1);
        int contentHeight = Math.Max(minimumContentHeight, _viewport.ClientSize.Height - 1);
        Size minimumContentSize = new(minimumContentWidth, minimumContentHeight);
        bool boundsChanged = _root.Left != 0 || _root.Top != 0 ||
                             _root.Width != contentWidth || _root.Height != contentHeight;
        bool scrollSizeChanged = _viewport.AutoScrollMinSize != minimumContentSize;
        if (!force && !modeChanged && !boundsChanged && !scrollSizeChanged)
        {
            return;
        }

        _viewport.SuspendLayout();
        _root.SuspendLayout();
        if (boundsChanged)
        {
            _root.SetBounds(0, 0, contentWidth, contentHeight);
        }
        if (scrollSizeChanged)
        {
            _viewport.AutoScrollMinSize = minimumContentSize;
        }
        _root.ResumeLayout(true);
        _viewport.ResumeLayout(true);
    }

    private void QueueResponsiveLayout()
    {
        if (!_dpiLayoutReady)
        {
            return;
        }

        _responsiveLayoutTimer.Stop();
        _responsiveLayoutTimer.Start();
    }

    private void ConfigureCommandLayout(WorkspaceLayoutMode mode)
    {
        _commandLayout.SuspendLayout();
        _commandLayout.Controls.Clear();
        _commandLayout.ColumnStyles.Clear();
        _commandLayout.RowStyles.Clear();
        _commandDividerOne.Visible = mode == WorkspaceLayoutMode.Wide;
        _commandDividerTwo.Visible = mode == WorkspaceLayoutMode.Wide;
        _commandDividerThree.Visible = mode == WorkspaceLayoutMode.Wide;
        _commandLayout.SetColumnSpan(_homeGroup, 1);
        _commandLayout.SetColumnSpan(_providerGroup, 1);
        _commandLayout.SetColumnSpan(_backupGroup, 1);
        _commandLayout.SetColumnSpan(_optionsGroup, 1);
        _homeGroup.Margin = new Padding(0);
        _providerGroup.Margin = new Padding(0);
        _backupGroup.Margin = new Padding(0);
        _optionsGroup.Margin = new Padding(0);

        if (mode == WorkspaceLayoutMode.Wide)
        {
            _commandStrip.Padding = ScalePadding(16, 14, 16, 14);
            _commandLayout.ColumnCount = 7;
            _commandLayout.RowCount = 1;
            _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(28)));
            _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(28)));
            _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(28)));
            _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            _commandLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _commandLayout.Controls.Add(_homeGroup, 0, 0);
            _commandLayout.Controls.Add(_commandDividerOne, 1, 0);
            _commandLayout.Controls.Add(_providerGroup, 2, 0);
            _commandLayout.Controls.Add(_commandDividerTwo, 3, 0);
            _commandLayout.Controls.Add(_backupGroup, 4, 0);
            _commandLayout.Controls.Add(_commandDividerThree, 5, 0);
            _commandLayout.Controls.Add(_optionsGroup, 6, 0);
        }
        else if (mode == WorkspaceLayoutMode.Compact)
        {
            _commandStrip.Padding = ScalePadding(14, 12, 14, 12);
            _commandLayout.ColumnCount = 2;
            _commandLayout.RowCount = 2;
            _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _commandLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _commandLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _homeGroup.Margin = ScalePadding(0, 0, 8, 6);
            _providerGroup.Margin = ScalePadding(8, 0, 0, 6);
            _backupGroup.Margin = ScalePadding(0, 6, 8, 0);
            _optionsGroup.Margin = ScalePadding(8, 6, 0, 0);
            _commandLayout.Controls.Add(_homeGroup, 0, 0);
            _commandLayout.Controls.Add(_providerGroup, 1, 0);
            _commandLayout.Controls.Add(_backupGroup, 0, 1);
            _commandLayout.Controls.Add(_optionsGroup, 1, 1);
        }
        else
        {
            _commandStrip.Padding = ScalePadding(12, 10, 12, 10);
            _commandLayout.ColumnCount = 1;
            _commandLayout.RowCount = 4;
            _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _commandLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            _commandLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            _commandLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            _commandLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            _homeGroup.Margin = ScalePadding(0, 0, 0, 6);
            _providerGroup.Margin = ScalePadding(0, 6, 0, 6);
            _backupGroup.Margin = ScalePadding(0, 6, 0, 6);
            _optionsGroup.Margin = ScalePadding(0, 6, 0, 0);
            _commandLayout.Controls.Add(_homeGroup, 0, 0);
            _commandLayout.Controls.Add(_providerGroup, 0, 1);
            _commandLayout.Controls.Add(_backupGroup, 0, 2);
            _commandLayout.Controls.Add(_optionsGroup, 0, 3);
        }

        _commandLayout.ResumeLayout(true);
    }

    private void ConfigureStatusLayout(WorkspaceLayoutMode mode)
    {
        _statusLayout.SuspendLayout();
        _statusLayout.Controls.Clear();
        _statusLayout.ColumnStyles.Clear();
        _statusLayout.RowStyles.Clear();
        _statusLayout.SetColumnSpan(_statusIcon, 1);
        _statusLayout.SetColumnSpan(_statusText, 1);
        _statusLayout.SetColumnSpan(_syncButton, 1);
        _statusLayout.SetColumnSpan(_refreshButton, 1);
        _statusLayout.SetColumnSpan(_openBackupButton, 1);
        _statusLayout.SetColumnSpan(_statusProgress, 1);
        if (mode == WorkspaceLayoutMode.Wide)
        {
            _statusLayout.ColumnCount = 5;
            _statusLayout.RowCount = 2;
            _statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(68)));
            _statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(132)));
            _statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(130)));
            _statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(160)));
            _statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _statusLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(3)));
            _statusLayout.Controls.Add(_statusIcon, 0, 0);
            _statusLayout.Controls.Add(_statusText, 1, 0);
            _statusLayout.Controls.Add(_syncButton, 2, 0);
            _statusLayout.Controls.Add(_refreshButton, 3, 0);
            _statusLayout.Controls.Add(_openBackupButton, 4, 0);
            _statusLayout.Controls.Add(_statusProgress, 0, 1);
            _statusLayout.SetColumnSpan(_statusProgress, 5);
        }
        else
        {
            _statusLayout.ColumnCount = 4;
            _statusLayout.RowCount = 3;
            _statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(60)));
            _statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(130)));
            _statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(160)));
            _statusLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(56)));
            _statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _statusLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(3)));
            _statusLayout.Controls.Add(_statusIcon, 0, 0);
            _statusLayout.Controls.Add(_statusText, 1, 0);
            _statusLayout.SetColumnSpan(_statusText, 3);
            _statusLayout.Controls.Add(_syncButton, 1, 1);
            _statusLayout.Controls.Add(_refreshButton, 2, 1);
            _statusLayout.Controls.Add(_openBackupButton, 3, 1);
            _statusLayout.Controls.Add(_statusProgress, 0, 2);
            _statusLayout.SetColumnSpan(_statusProgress, 4);
        }

        _statusLayout.ResumeLayout(true);
    }

    private int ScaleLogical(int logicalPixels) =>
        Math.Max(
            0,
            (int)Math.Round(logicalPixels * (_dpiLayoutReady ? DeviceDpi / 96d : 1d)));

    internal static WorkspaceLayoutMode ResolveLayoutMode(int viewportWidth, int dpi)
    {
        int normalizedDpi = Math.Max(96, dpi);
        int wideThreshold = (int)Math.Round(1080 * normalizedDpi / 96d);
        int compactThreshold = (int)Math.Round(850 * normalizedDpi / 96d);
        return viewportWidth >= wideThreshold
            ? WorkspaceLayoutMode.Wide
            : viewportWidth >= compactThreshold
                ? WorkspaceLayoutMode.Compact
                : WorkspaceLayoutMode.Narrow;
    }

    private Padding ScalePadding(int left, int top, int right, int bottom) => new(
        ScaleLogical(left),
        ScaleLogical(top),
        ScaleLogical(right),
        ScaleLogical(bottom));

    private void FitWindowToWorkingArea()
    {
        Rectangle workingArea = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(
            Math.Min(ScaleLogical(760), workingArea.Width),
            Math.Min(ScaleLogical(500), workingArea.Height));
        Size = new Size(
            Math.Min(Width, workingArea.Width),
            Math.Min(Height, workingArea.Height));
        Location = new Point(
            Math.Clamp(Left, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - Width)),
            Math.Clamp(Top, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - Height)));
    }

    private Control BuildHeader()
    {
        TableLayoutPanel header = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 244));

        PictureBox icon = new()
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(6, 12, 10, 18),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = LoadApplicationImage(),
            AccessibleName = "Codex 会话热同步图标",
        };
        TableLayoutPanel text = new()
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Margin = new Padding(0, 8, 0, 10),
        };
        text.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        text.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Label title = new()
        {
            Text = "Codex 会话热同步",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 22f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            AutoEllipsis = true,
        };
        Label subtitle = new()
        {
            Text = "同步 JSONL 会话元数据、session_index.jsonl 与两个 SQLite 数据库",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10.5f),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
            Tag = "Muted",
        };
        text.Controls.Add(title, 0, 0);
        text.Controls.Add(subtitle, 0, 1);

        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 18, 0, 18),
            Margin = new Padding(0),
            BackColor = Color.Transparent,
        };
        _exportButton = CreateButton("\uE74E", "导出记录", false, 112);
        _importButton = CreateButton("\uE8E5", "导入记录", false, 112);
        _exportButton.Height = 42;
        _importButton.Height = 42;
        actions.Controls.Add(_exportButton);
        actions.Controls.Add(_importButton);

        header.Controls.Add(icon, 0, 0);
        header.Controls.Add(text, 1, 0);
        header.Controls.Add(actions, 2, 0);
        return header;
    }

    private Control BuildCommandStrip()
    {
        _commandStrip = NewSurfacePanel(new Padding(16, 14, 16, 14));
        _commandStrip.Margin = new Padding(0, 0, 0, 12);
        _commandLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 7,
            RowCount = 1,
            Margin = new Padding(0),
        };
        _commandLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        _commandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));

        TableLayoutPanel homeEditor = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };
        homeEditor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        homeEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        homeEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        _codexHomeBox.Font = new Font("Segoe UI", 11f);
        _codexHomeBox.Tag = "EmbeddedField";
        _codexHomeBox.Margin = new Padding(0);
        _codexHomeBox.AccessibleName = "Codex Home 路径";
        _browseButton = CreateIconButton("\uE8B7", "选择 Codex Home");
        _browseButton.Dock = DockStyle.None;
        _browseButton.Anchor = AnchorStyles.None;
        _browseButton.Margin = new Padding(0);
        _browseButton.Size = new Size(48, 48);
        homeEditor.Controls.Add(BuildEditorSurface(_codexHomeBox), 0, 0);
        homeEditor.Controls.Add(_browseButton, 1, 0);

        TableLayoutPanel backupEditor = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };
        backupEditor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        backupEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        backupEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        _backupRootBox.Font = new Font("Segoe UI", 11f);
        _backupRootBox.Tag = "EmbeddedField";
        _backupRootBox.Margin = new Padding(0);
        _backupRootBox.AccessibleName = "备份保存路径";
        _browseBackupButton = CreateIconButton("\uE8B7", "选择备份保存路径");
        _browseBackupButton.Dock = DockStyle.None;
        _browseBackupButton.Anchor = AnchorStyles.None;
        _browseBackupButton.Margin = new Padding(0);
        _browseBackupButton.Size = new Size(48, 48);
        backupEditor.Controls.Add(BuildEditorSurface(_backupRootBox), 0, 0);
        backupEditor.Controls.Add(_browseBackupButton, 1, 0);

        _providerCombo.Font = new Font("Segoe UI", 11f);
        _providerCombo.Tag = "EmbeddedField";
        _providerCombo.Margin = new Padding(0);
        _providerCombo.DropDownStyle = ComboBoxStyle.DropDown;
        _providerCombo.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _providerCombo.AutoCompleteSource = AutoCompleteSource.ListItems;
        _providerCombo.AccessibleName = "目标 Provider";

        _includeArchivedCheck.Text = "包含已归档会话";
        _includeArchivedCheck.Dock = DockStyle.Fill;
        _includeArchivedCheck.Margin = new Padding(0);
        _includeArchivedCheck.Font = new Font("Segoe UI", 10.5f);
        _includeArchivedCheck.AccessibleName = "包含已归档会话";

        TableLayoutPanel options = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
        };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        options.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        options.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        options.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        Control optionsLabel = CommandLabel("同步选项", 10.5f, FontStyle.Regular);
        optionsLabel.Tag = "Muted";
        options.Controls.Add(optionsLabel, 0, 0);
        options.Controls.Add(_includeArchivedCheck, 0, 1);
        options.Controls.Add(BuildBackupIndicator(), 0, 2);

        _homeGroup = BuildSettingGroup("Codex Home", homeEditor);
        _providerGroup = BuildSettingGroup("目标 Provider", BuildEditorSurface(_providerCombo));
        _backupGroup = BuildSettingGroup("备份保存路径", backupEditor);
        _optionsGroup = options;
        _commandDividerOne = BuildCommandDivider();
        _commandDividerTwo = BuildCommandDivider();
        _commandDividerThree = BuildCommandDivider();
        _commandLayout.Controls.Add(_homeGroup, 0, 0);
        _commandLayout.Controls.Add(_commandDividerOne, 1, 0);
        _commandLayout.Controls.Add(_providerGroup, 2, 0);
        _commandLayout.Controls.Add(_commandDividerTwo, 3, 0);
        _commandLayout.Controls.Add(_backupGroup, 4, 0);
        _commandLayout.Controls.Add(_commandDividerThree, 5, 0);
        _commandLayout.Controls.Add(_optionsGroup, 6, 0);
        _commandStrip.Controls.Add(_commandLayout);
        return _commandStrip;
    }

    private Control BuildSettingGroup(string labelText, Control editor)
    {
        TableLayoutPanel group = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
        };
        group.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        group.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        group.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Control label = CommandLabel(labelText, 10.5f, FontStyle.Regular);
        label.Tag = "Muted";
        label.AccessibleName = labelText;
        group.Controls.Add(label, 0, 0);
        group.Controls.Add(editor, 0, 1);
        return group;
    }

    private Control BuildEditorSurface(Control editor)
    {
        SurfacePanel host = NewSurfacePanel(new Padding(0));
        host.Margin = new Padding(0, 4, 0, 0);
        host.CornerRadius = 4;
        editor.Dock = DockStyle.None;
        editor.Anchor = AnchorStyles.None;
        host.Controls.Add(editor);
        host.Layout += (_, _) =>
        {
            Size preferred = editor.PreferredSize;
            int editorHeight = Math.Min(preferred.Height, Math.Max(1, host.ClientSize.Height - 10));
            editor.SetBounds(
                11,
                Math.Max(0, (host.ClientSize.Height - editorHeight) / 2),
                Math.Max(1, host.ClientSize.Width - 22),
                editorHeight);
        };
        return host;
    }

    private Control BuildCommandDivider()
    {
        return new Panel
        {
            Width = 1,
            Height = 82,
            Anchor = AnchorStyles.None,
            Margin = new Padding(13, 4, 13, 4),
            BackColor = _theme.Border,
        };
    }

    private Control BuildTopology()
    {
        _topology.Dock = DockStyle.Fill;
        _topology.Margin = new Padding(0, 0, 0, 8);
        return _topology;
    }

    private Control BuildStatusRail()
    {
        _statusRail = NewSurfacePanel(new Padding(14, 10, 14, 10));
        _statusRail.Margin = new Padding(0, 0, 0, 10);
        _statusLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 2,
            Margin = new Padding(0),
        };
        _statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        _statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        _statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        _statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        _statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _statusLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 3));

        _statusIcon.Dock = DockStyle.Fill;
        _statusIcon.Margin = new Padding(2, 0, 6, 0);
        TableLayoutPanel text = new()
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Margin = new Padding(0, 2, 12, 2),
        };
        text.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        text.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _statusTitle.Dock = DockStyle.Fill;
        _statusTitle.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
        _statusTitle.Margin = new Padding(0, 0, 0, 2);
        _statusDetail.Dock = DockStyle.Fill;
        _statusDetail.Font = new Font("Segoe UI", 9.5f);
        _statusDetail.Margin = new Padding(0, 2, 0, 0);
        _statusDetail.Tag = "Muted";
        text.Controls.Add(_statusTitle, 0, 0);
        text.Controls.Add(_statusDetail, 0, 1);
        _statusText = text;

        _syncButton = CreateButton(string.Empty, "立即同步", true, 118);
        _refreshButton = CreateButton(string.Empty, "刷新状态", false, 116);
        _openBackupButton = CreateButton(string.Empty, "打开备份目录", false, 146);
        _syncButton.Anchor = AnchorStyles.Right;
        _refreshButton.Anchor = AnchorStyles.Right;
        _openBackupButton.Anchor = AnchorStyles.Right;

        _statusProgress.Dock = DockStyle.Fill;
        _statusProgress.Style = ProgressBarStyle.Continuous;
        _statusProgress.Maximum = 100;
        _statusProgress.Visible = false;

        _statusLayout.Controls.Add(_statusIcon, 0, 0);
        _statusLayout.Controls.Add(text, 1, 0);
        _statusLayout.Controls.Add(_syncButton, 2, 0);
        _statusLayout.Controls.Add(_refreshButton, 3, 0);
        _statusLayout.Controls.Add(_openBackupButton, 4, 0);
        _statusLayout.Controls.Add(_statusProgress, 0, 1);
        _statusLayout.SetColumnSpan(_statusProgress, 5);
        _statusRail.Controls.Add(_statusLayout);
        return _statusRail;
    }

    private Control BuildLogHeader()
    {
        return new Label
        {
            Text = "执行日志",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            Margin = new Padding(0),
        };
    }

    private Control BuildLogPanel()
    {
        SurfacePanel panel = NewSurfacePanel(new Padding(10, 8, 10, 8));
        panel.Margin = new Padding(0, 4, 0, 8);
        _log.Dock = DockStyle.Fill;
        _log.BorderStyle = BorderStyle.None;
        _log.ReadOnly = true;
        _log.DetectUrls = false;
        _log.Font = new Font("Cascadia Mono", 10f, FontStyle.Regular);
        _log.AccessibleName = "同步执行日志";
        panel.Controls.Add(_log);
        return panel;
    }

    private Control BuildBackupIndicator()
    {
        TableLayoutPanel indicator = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };
        indicator.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        indicator.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        indicator.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Label icon = new()
        {
            Text = "\uE83D",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe Fluent Icons", 14f),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = _theme.Info,
            Tag = "Info",
        };
        CommandTextControl text = new()
        {
            Text = "每次同步前自动备份",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            Margin = new Padding(0),
            ForeColor = _theme.Info,
            Tag = "Info",
        };
        indicator.Controls.Add(icon, 0, 0);
        indicator.Controls.Add(text, 1, 0);
        return indicator;
    }

    private void ApplyTheme()
    {
        BackColor = _theme.Background;
        ForeColor = _theme.Text;
        NativeTheme.Apply(this, _theme.IsDark);
        ApplyThemeRecursive(this);
        _topology.ApplyPalette(_theme);
        _footerLabel.Dock = DockStyle.Fill;
        _footerLabel.TextAlign = ContentAlignment.MiddleLeft;
        _footerLabel.AutoEllipsis = true;
        _footerLabel.Font = new Font("Segoe UI", 10f);
        _footerLabel.ForeColor = _theme.MutedText;
        _footerLabel.LinkColor = _theme.Info;
        _footerLabel.ActiveLinkColor = _theme.Accent;
        _footerLabel.VisitedLinkColor = _theme.Info;
        _footerLabel.LinkBehavior = LinkBehavior.HoverUnderline;
        _statusTitle.BackColor = _theme.Surface;
        _statusDetail.BackColor = _theme.Surface;
    }

    private void ApplyThemeRecursive(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            control.ForeColor = (control.Tag as string) switch
            {
                "Info" => _theme.Info,
                "Muted" => _theme.MutedText,
                _ => _theme.Text,
            };
            switch (control)
            {
                case SurfacePanel surface:
                    surface.BackColor = _theme.Surface;
                    surface.BorderColor = _theme.Border;
                    break;
                case ModernCheckBox checkBox:
                    checkBox.AccentColor = _theme.Info;
                    checkBox.BoxBorderColor = _theme.MutedText;
                    checkBox.SurfaceColor = _theme.Surface;
                    break;
                case TextBox textBox:
                    textBox.BackColor = string.Equals(textBox.Tag as string, "EmbeddedField", StringComparison.Ordinal)
                        ? _theme.Surface
                        : _theme.SurfaceAlt;
                    textBox.ForeColor = _theme.Text;
                    textBox.BorderStyle = string.Equals(textBox.Tag as string, "EmbeddedField", StringComparison.Ordinal)
                        ? BorderStyle.None
                        : BorderStyle.FixedSingle;
                    break;
                case ComboBox comboBox:
                    comboBox.BackColor = string.Equals(comboBox.Tag as string, "EmbeddedField", StringComparison.Ordinal)
                        ? _theme.Surface
                        : _theme.SurfaceAlt;
                    comboBox.ForeColor = _theme.Text;
                    comboBox.FlatStyle = FlatStyle.Flat;
                    break;
                case RichTextBox richTextBox:
                    richTextBox.BackColor = _theme.IsDark ? Color.FromArgb(18, 23, 27) : Color.FromArgb(251, 252, 253);
                    richTextBox.ForeColor = _theme.Text;
                    break;
                case Button button when button == _syncButton:
                    button.BackColor = _theme.Accent;
                    button.ForeColor = _theme.AccentText;
                    break;
                case Button button:
                    button.BackColor = _theme.Surface;
                    button.ForeColor = _theme.Text;
                    break;
                case TableLayoutPanel or FlowLayoutPanel:
                    control.BackColor = Color.Transparent;
                    break;
            }

            ApplyThemeRecursive(control);
        }
    }

    private void LoadSettings()
    {
        _settings = _settingsStore.Load();
        _codexHomeBox.Text = string.IsNullOrWhiteSpace(_settings.LastCodexHome)
            ? CodexConfigService.DefaultCodexHome
            : _settings.LastCodexHome;
        try
        {
            _backupRootBox.Text = BackupService.ResolveRoot(
                CodexConfigService.NormalizeCodexHome(_codexHomeBox.Text),
                _settings.BackupRootDirectory);
        }
        catch
        {
            _backupRootBox.Text = BackupService.ResolveRoot(
                CodexConfigService.NormalizeCodexHome(_codexHomeBox.Text),
                null);
        }
        _includeArchivedCheck.Checked = _settings.IncludeArchived;
        if (!string.IsNullOrWhiteSpace(_settings.TargetProvider))
        {
            _providerCombo.Text = _settings.TargetProvider;
        }

        Rectangle workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        int currentDpi = Math.Max(96, DeviceDpi);
        int storedDpi = _settings.WindowDpi >= 96 ? _settings.WindowDpi : currentDpi;
        int requestedWidth = (int)Math.Round(_settings.WindowWidth * 96d / storedDpi);
        int requestedHeight = (int)Math.Round(_settings.WindowHeight * 96d / storedDpi);
        int workingWidth = (int)Math.Floor(workingArea.Width * 96d / currentDpi);
        int workingHeight = (int)Math.Floor(workingArea.Height * 96d / currentDpi);
        int width = Math.Min(
            Math.Max(MinimumSize.Width, requestedWidth),
            Math.Max(MinimumSize.Width, workingWidth));
        int height = Math.Min(
            Math.Max(MinimumSize.Height, requestedHeight),
            Math.Max(MinimumSize.Height, workingHeight));
        Size = new Size(width, height);
        ApplyResponsiveLayout(true);
        RefreshLatestBackupFooter();
    }

    private void WireEvents()
    {
        Shown += async (_, _) =>
        {
            await RefreshStatusAsync();
            _codexHomeBox.SelectionLength = 0;
            _providerCombo.SelectionLength = 0;
            ActiveControl = null;
        };
        FormClosing += (_, _) => SaveSettings();
        FormClosed += (_, _) => _responsiveLayoutTimer.Dispose();
        _browseButton.Click += async (_, _) => await BrowseAsync();
        _browseBackupButton.Click += (_, _) => BrowseBackupDirectory();
        _backupRootBox.Validated += (_, _) => RefreshLatestBackupFooter();
        _importButton.Click += async (_, _) => await ImportChatsAsync();
        _exportButton.Click += async (_, _) => await ExportChatsAsync();
        _refreshButton.Click += async (_, _) => await RefreshStatusAsync();
        _syncButton.Click += async (_, _) => await RunSyncAsync();
        _openBackupButton.Click += (_, _) => OpenBackupDirectory();
        _footerLabel.LinkClicked += (_, _) => OpenBackupDirectory();
        _includeArchivedCheck.CheckedChanged += async (_, _) =>
        {
            if (Visible && !_busy)
            {
                await RefreshStatusAsync(false);
            }
        };
        _providerCombo.SelectionChangeCommitted += async (_, _) =>
        {
            if (!_updatingProvider && !_busy)
            {
                _providerSelectionExplicit = true;
                await RefreshStatusAsync(false, false);
            }
        };
        _providerCombo.TextUpdate += (_, _) =>
        {
            if (!_updatingProvider)
            {
                _providerSelectionExplicit = true;
            }
        };
        KeyDown += async (_, args) =>
        {
            if (args.KeyCode == Keys.F5 && !_busy)
            {
                args.SuppressKeyPress = true;
                await RefreshStatusAsync();
            }
            else if (args.Control && args.KeyCode == Keys.Enter && !_busy)
            {
                args.SuppressKeyPress = true;
                await RunSyncAsync();
            }
        };
    }

    private async Task ExportChatsAsync()
    {
        if (_busy)
        {
            return;
        }

        ChatPackExportPreview preview;
        SetBusy(true);
        _topology.SetState(SyncTopologyState.Busy, "正在扫描聊天记录", progress: 6);
        SetRail(RailState.Busy, "正在整理可导出的项目…", "读取会话项目路径与归档状态", 6);
        try
        {
            preview = await _chatPackService.ReadExportPreviewAsync(
                _codexHomeBox.Text,
                _includeArchivedCheck.Checked);
        }
        catch (Exception error)
        {
            _topology.SetState(SyncTopologyState.Error, "无法准备导出", "未创建聊天包");
            SetRail(RailState.Error, "无法读取可导出的聊天记录", error.Message, 0);
            AppendLog("导出失败：" + error.Message);
            return;
        }
        finally
        {
            SetBusy(false);
        }

        int exportableSessions = preview.Projects.Sum(item => item.SessionCount);
        _topology.SetState(
            SyncTopologyState.Ready,
            "聊天记录已整理",
            $"{preview.Projects.Count:N0} 个项目 · {exportableSessions:N0} 个会话",
            0);
        SetRail(
            RailState.Ready,
            $"发现 {preview.Projects.Count:N0} 个可导出项目",
            $"共 {exportableSessions:N0} 个会话，请选择需要打包的项目",
            0);

        using ProjectExportDialog selectionDialog = new(
            _theme,
            preview.CodexHome,
            preview.Projects);
        if (selectionDialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        IReadOnlySet<string> selectedSessionIds = selectionDialog.SelectedSessionIds;

        string initialDirectory = Directory.Exists(_settings.LastExportDirectory)
            ? _settings.LastExportDirectory!
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        using SaveFileDialog dialog = new()
        {
            Title = "导出 Codex 聊天记录",
            Filter = "Codex 聊天包 (*.codex-chatpack)|*.codex-chatpack",
            DefaultExt = "codex-chatpack",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = initialDirectory,
            FileName = $"Codex聊天记录-{DateTime.Now:yyyyMMdd-HHmmss}.codex-chatpack",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SetBusy(true);
        _topology.SetState(SyncTopologyState.Busy, "正在导出聊天记录", progress: 8);
        SetRail(RailState.Busy, "正在创建聊天包…", "保留完整 JSONL 与引用附件，会话缓存不会写入聊天包", 8);
        try
        {
            Progress<string> progress = new(message =>
            {
                SetRail(RailState.Busy, message, "正在压缩完整会话数据", 55);
            });
            ChatPackExportResult result = await _chatPackService.ExportAsync(
                preview.CodexHome,
                preview.IncludeArchived,
                dialog.FileName,
                progress,
                selectedSessionIds: selectedSessionIds);
            _settings = _settings with { LastExportDirectory = Path.GetDirectoryName(result.PackagePath) };
            SaveSettings();
            _topology.SetState(SyncTopologyState.Success, "导出完成", $"{result.SessionCount:N0} 个会话已打包", 100);
            SetRail(
                RailState.Success,
                $"聊天记录导出完成：{result.SessionCount:N0} 个会话",
                $"{result.ProjectCount:N0} 个项目 · {result.AttachmentCount:N0} 个附件 · " +
                FormatFileSize(new FileInfo(result.PackagePath).Length),
                100);
            AppendLog($"聊天记录已导出：{result.PackagePath}");
        }
        catch (Exception error)
        {
            _topology.SetState(SyncTopologyState.Error, "导出失败", "未修改本地会话");
            SetRail(RailState.Error, "聊天记录导出失败", error.Message, 0);
            AppendLog("导出失败：" + error.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ImportChatsAsync()
    {
        if (_busy)
        {
            return;
        }

        string initialDirectory = Directory.Exists(_settings.LastExportDirectory)
            ? _settings.LastExportDirectory!
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        using OpenFileDialog fileDialog = new()
        {
            Title = "导入 Codex 聊天记录",
            Filter = "Codex 聊天包 (*.codex-chatpack)|*.codex-chatpack",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = initialDirectory,
        };
        if (fileDialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        ChatPackPreview preview;
        SetBusy(true);
        SetRail(RailState.Busy, "正在读取聊天包…", Path.GetFileName(fileDialog.FileName), 10);
        try
        {
            preview = await _chatPackService.ReadPreviewAsync(fileDialog.FileName, _codexHomeBox.Text);
        }
        catch (Exception error)
        {
            _topology.SetState(SyncTopologyState.Error, "聊天包不可用", "未修改本地会话");
            SetRail(RailState.Error, "无法读取聊天包", error.Message, 0);
            AppendLog("导入失败：" + error.Message);
            return;
        }
        finally
        {
            SetBusy(false);
        }

        using ProjectMappingDialog mappingDialog = new(_theme, preview.PackagePath, preview.Mappings);
        if (mappingDialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _settings = _settings with { LastExportDirectory = Path.GetDirectoryName(preview.PackagePath) };
        SaveSettings();
        ChatPackImportResult? imported = null;
        bool syncCompleted = false;
        SetBusy(true);
        _topology.SetState(SyncTopologyState.Busy, "正在导入聊天记录", progress: 12);
        SetRail(RailState.Busy, "正在导入聊天记录…", "已有同 ID 会话将自动跳过", 12);
        try
        {
            Progress<string> progress = new(message =>
            {
                _topology.SetProgress(45, "正在导入聊天记录");
                SetRail(RailState.Busy, message, "正在应用项目路径映射", 45);
            });
            imported = await _chatPackService.ImportAsync(
                preview,
                _codexHomeBox.Text,
                mappingDialog.Mappings,
                progress);
            if (imported.ImportedSessions == 0)
            {
                _topology.SetState(SyncTopologyState.Success, "无需导入", "聊天记录已经存在", 100);
                SetRail(
                    RailState.Success,
                    "聊天记录已经存在",
                    $"已跳过 {imported.SkippedExistingSessions:N0} 个同 ID 会话 · " +
                    $"未选择 {imported.ExcludedSessions:N0} 个会话",
                    100);
                AppendLog(
                    $"导入完成：没有新增会话，跳过 {imported.SkippedExistingSessions:N0} 个已有会话，" +
                    $"未选择 {imported.ExcludedSessions:N0} 个会话");
                return;
            }

            string codexHome = CodexConfigService.NormalizeCodexHome(_codexHomeBox.Text);
            string provider = _providerSelectionExplicit
                ? _providerCombo.Text.Trim()
                : CodexConfigService.ReadProviders(codexHome).CurrentProvider;
            if (string.IsNullOrWhiteSpace(provider))
            {
                throw new InvalidOperationException("目标 Provider 为空，无法重建会话索引。");
            }

            Progress<string> syncProgress = new(message =>
            {
                int percent = Math.Max(50, ProgressForMessage(message));
                _topology.SetProgress(percent, TopologyTitleForMessage(message));
                SetRail(RailState.Busy, message, "正在重建索引与 SQLite", percent);
            });
            SyncResult syncResult = await _syncService.SyncAsync(
                codexHome,
                true,
                provider,
                syncProgress,
                overrides: new SessionSyncOverrides
                {
                    PreferredTitles = imported.Titles,
                    WorkspaceRoots = imported.WorkspaceRoots,
                    ProjectlessThreadIds = imported.ProjectlessThreadIds,
                },
                backupRoot: CurrentBackupRoot());
            syncCompleted = true;
            _lastBackupDirectory = syncResult.BackupDirectory;
            InspectionSnapshot snapshot = await _syncService.InspectAsync(codexHome, true, provider);
            RenderSnapshot(snapshot);
            _topology.SetState(
                SyncTopologyState.Success,
                "导入完成",
                $"新增 {imported.ImportedSessions:N0} 个会话",
                100);
            SetRail(
                RailState.Success,
                $"聊天记录导入完成：新增 {imported.ImportedSessions:N0} 个会话",
                $"恢复 {imported.ImportedAttachments:N0} 个附件 · 跳过已有 {imported.SkippedExistingSessions:N0} · " +
                $"未选择 {imported.ExcludedSessions:N0} · 已应用 {imported.WorkspaceRoots.Count:N0} 个项目映射",
                100);
            SetFooterBackupPath(syncResult.BackupDirectory);
            AppendLog(
                $"聊天记录导入完成：新增 {imported.ImportedSessions:N0}，" +
                $"恢复附件 {imported.ImportedAttachments:N0}，跳过 {imported.SkippedExistingSessions:N0}，" +
                $"未选择 {imported.ExcludedSessions:N0}");
            AppendLog("提示：完全退出并重新打开 Codex 后，会话历史才会重新载入");
        }
        catch (Exception error)
        {
            if (!syncCompleted && imported is not null)
            {
                await _chatPackService.RollbackImportAsync(imported.AddedPaths);
            }

            _topology.SetState(SyncTopologyState.Error, "导入未完成", "已回滚本次新增文件");
            SetRail(RailState.Error, "聊天记录导入失败", error.Message, 0);
            AppendLog("导入失败：" + error.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task BrowseAsync()
    {
        string previousHome = CodexConfigService.NormalizeCodexHome(_codexHomeBox.Text);
        string previousDefaultBackup = BackupService.ResolveRoot(previousHome, null);
        bool backupFollowsCodexHome = string.IsNullOrWhiteSpace(_backupRootBox.Text);
        if (!backupFollowsCodexHome)
        {
            try
            {
                backupFollowsCodexHome = string.Equals(
                    BackupService.ResolveRoot(previousHome, _backupRootBox.Text),
                    previousDefaultBackup,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
            }
        }
        using FolderBrowserDialog dialog = new()
        {
            Description = "选择 Codex Home 目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_codexHomeBox.Text) ? _codexHomeBox.Text : CodexConfigService.DefaultCodexHome,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _codexHomeBox.Text = dialog.SelectedPath;
            if (backupFollowsCodexHome)
            {
                _backupRootBox.Text = BackupService.ResolveRoot(dialog.SelectedPath, null);
            }
            _lastBackupDirectory = null;
            await RefreshStatusAsync();
            RefreshLatestBackupFooter();
        }
    }

    private void BrowseBackupDirectory()
    {
        string current;
        try
        {
            current = CurrentBackupRoot();
        }
        catch
        {
            current = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        string selectedPath = current;
        while (!Directory.Exists(selectedPath) && !string.IsNullOrWhiteSpace(selectedPath))
        {
            selectedPath = Path.GetDirectoryName(selectedPath) ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            selectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        using FolderBrowserDialog dialog = new()
        {
            Description = "选择同步备份的保存目录",
            UseDescriptionForTitle = true,
            SelectedPath = selectedPath,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _backupRootBox.Text = Path.GetFullPath(dialog.SelectedPath);
            _lastBackupDirectory = null;
            SaveSettings();
            RefreshLatestBackupFooter();
        }
    }

    private string CurrentBackupRoot() => BackupService.ResolveRoot(
        CodexConfigService.NormalizeCodexHome(_codexHomeBox.Text),
        _backupRootBox.Text);

    private async Task RefreshStatusAsync(bool writeLog = true, bool followCurrentProvider = true)
    {
        if (_busy)
        {
            return;
        }

        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        SetBusy(true);
        _topology.SetState(SyncTopologyState.Busy, "正在扫描会话", progress: 8);
        SetRail(RailState.Busy, "正在扫描会话与数据库…", "只读取 session_meta 与 SQLite 线程索引", 8);
        try
        {
            InspectionSnapshot snapshot = await _syncService.InspectAsync(
                _codexHomeBox.Text,
                _includeArchivedCheck.Checked,
                followCurrentProvider ? null : _providerCombo.Text,
                _refreshCancellation.Token);
            if (followCurrentProvider)
            {
                _providerSelectionExplicit = false;
                _providerCombo.Text = snapshot.CurrentProvider;
            }
            RenderSnapshot(snapshot);
            if (writeLog)
            {
                AppendLog($"状态刷新：{snapshot.CanonicalSessions.Count} 个有效会话，{snapshot.TotalIssues} 项待处理");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            _topology.SetState(SyncTopologyState.Error, "状态读取失败", "未修改任何文件");
            SetRail(RailState.Error, "状态读取失败", error.Message, 0);
            AppendLog("刷新失败：" + error.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RunSyncAsync()
    {
        if (_busy)
        {
            return;
        }

        string provider = _providerCombo.Text.Trim();
        if (!_providerSelectionExplicit)
        {
            string codexHome = CodexConfigService.NormalizeCodexHome(_codexHomeBox.Text);
            string currentProvider = CodexConfigService.ReadProviders(codexHome).CurrentProvider;
            if (!string.IsNullOrWhiteSpace(currentProvider) &&
                !string.Equals(provider, currentProvider, StringComparison.Ordinal))
            {
                provider = currentProvider;
                _providerCombo.Text = currentProvider;
                AppendLog("检测到 Provider 已切换，目标已自动更新为 " + currentProvider);
            }
        }
        if (string.IsNullOrWhiteSpace(provider))
        {
            SetRail(RailState.Error, "缺少目标 Provider", "请选择或输入 Provider 后重试", 0);
            _providerCombo.Focus();
            return;
        }

        SetBusy(true);
        _topology.SetState(SyncTopologyState.Busy, "准备同步", progress: 5);
        SetRail(RailState.Busy, "正在准备同步…", "即将创建一致性备份", 5);
        try
        {
            Progress<string> progress = new(message =>
            {
                int percent = ProgressForMessage(message);
                _topology.SetProgress(percent, TopologyTitleForMessage(message));
                SetRail(RailState.Busy, message, $"已完成约 {percent}%", percent);
                AppendLog(message);
            });
            SyncResult result = await _syncService.SyncAsync(
                _codexHomeBox.Text,
                _includeArchivedCheck.Checked,
                provider,
                progress,
                backupRoot: CurrentBackupRoot());
            _providerSelectionExplicit = false;
            _lastBackupDirectory = result.BackupDirectory;
            string summary = ResultSummary(result);
            AppendLog(summary);
            int repairedOrphans = result.Databases.Sum(item => item.RepairedOrphans);
            if (repairedOrphans > 0)
            {
                AppendLog($"已修复 {repairedOrphans:N0} 条无对应 JSONL 的 SQLite Provider 记录");
            }
            InspectionSnapshot snapshot = await _syncService.InspectAsync(
                _codexHomeBox.Text,
                _includeArchivedCheck.Checked,
                _providerCombo.Text);
            RenderSnapshot(snapshot);
            _topology.SetState(
                SyncTopologyState.Success,
                "同步完成",
                $"{result.ValidSessions:N0} 个会话已一致",
                100);
            SetRail(
                RailState.Success,
                "同步完成：两份数据库与 JSONL 已一致",
                $"JSONL 更新 {result.Jsonl.ChangedPaths.Count:N0} · SQLite 新增 {result.Databases.Sum(item => item.InsertedRows):N0} · 索引补齐 {result.SessionIndex.Added:N0}",
                100);
            _syncButton.Text = "再次同步";
            _statusDetail.Text = "磁盘会话已修复；请完全退出并重新打开 Codex 以重新加载历史";
            SetFooterBackupPath(result.BackupDirectory);
            AppendLog("提示：完全退出并重新打开 Codex 后，会话历史才会重新载入");
            AppendLog("同步完成，备份已保存");
        }
        catch (Exception error)
        {
            bool sqliteBusy = error.Message.Contains("SQLite", StringComparison.OrdinalIgnoreCase) &&
                              (error.Message.Contains("占用", StringComparison.Ordinal) ||
                               error.Message.Contains("使用", StringComparison.Ordinal));
            _topology.SetState(
                SyncTopologyState.Error,
                sqliteBusy ? "SQLite 正在被占用" : "同步未完成",
                sqliteBusy ? "未写入数据库更改" : "已恢复可回滚文件");
            SetRail(
                RailState.Error,
                sqliteBusy ? "同步未完成：state_5.sqlite 正在使用中，请稍后重试" : "同步未完成：" + error.Message,
                "JSONL 与 session_index.jsonl 已从备份恢复",
                0);
            _syncButton.Text = "重试同步";
            AppendLog("同步失败：" + error.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RenderSnapshot(InspectionSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        _codexHomeBox.Text = snapshot.CodexHome;
        string selectedProvider = string.IsNullOrWhiteSpace(_providerCombo.Text)
            ? snapshot.CurrentProvider
            : _providerCombo.Text.Trim();
        _updatingProvider = true;
        try
        {
            _providerCombo.BeginUpdate();
            _providerCombo.Items.Clear();
            _providerCombo.Items.AddRange(snapshot.ProviderOptions.Cast<object>().ToArray());
            _providerCombo.Text = snapshot.ProviderOptions.Contains(selectedProvider, StringComparer.OrdinalIgnoreCase)
                ? snapshot.ProviderOptions.First(item => string.Equals(item, selectedProvider, StringComparison.OrdinalIgnoreCase))
                : selectedProvider;
            if (string.IsNullOrWhiteSpace(_providerCombo.Text))
            {
                _providerCombo.Text = snapshot.CurrentProvider;
            }
        }
        finally
        {
            _providerCombo.EndUpdate();
            _updatingProvider = false;
        }

        DatabaseStatus legacy = snapshot.Databases.First(item => item.Location.Key == "legacy");
        DatabaseStatus modern = snapshot.Databases.First(item => item.Location.Key == "modern");
        DatabaseVisual legacyVisual = ToVisual(legacy);
        DatabaseVisual modernVisual = ToVisual(modern);
        _topology.UpdateData(
            snapshot.CanonicalSessions.Count,
            snapshot.IndexEntryCount,
            snapshot.TotalIssues,
            _providerCombo.Text,
            legacyVisual,
            modernVisual);

        if (snapshot.TotalIssues == 0)
        {
            _topology.SetState(SyncTopologyState.Success, "同步完成", $"{snapshot.CanonicalSessions.Count:N0} 个会话已一致", 100);
            SetRail(RailState.Success, "同步完成：两份数据库与 JSONL 已一致", "无需处理；可以再次同步进行校验", 100);
            _syncButton.Text = "再次同步";
        }
        else
        {
            _topology.SetState(SyncTopologyState.Ready);
            SetRail(RailState.Ready, $"检测到 {snapshot.TotalIssues:N0} 项差异，可以立即同步",
                $"JSONL-first：{snapshot.CanonicalSessions.Count:N0} 个有效会话，孤儿 SQLite 行不会被复制", 0);
            _syncButton.Text = "立即同步";
        }
    }

    private static DatabaseVisual ToVisual(DatabaseStatus status)
    {
        string value = !status.Exists
            ? "未检测到"
            : !status.Readable
                ? "不可读"
                : $"{status.ValidRows:N0}/{status.TotalRows:N0}";
        return new DatabaseVisual(
            status.Location.Key == "legacy" ? "根目录 state_5.sqlite" : "sqlite/state_5.sqlite",
            value,
            status.MissingFromDatabase,
            status.WrongRolloutPathRows,
            status.WrongProviderRows,
            status.Exists,
            status.Readable);
    }

    private void SetRail(RailState state, string title, string detail, int progress)
    {
        Color color = state switch
        {
            RailState.Success => _theme.Success,
            RailState.Error => _theme.Warning,
            RailState.Busy => _theme.Accent,
            _ => _theme.Warning,
        };
        _statusRail.BorderColor = _theme.Border;
        _statusRail.Invalidate();
        _statusIcon.SetState(state, color);
        _statusIcon.AccessibleName = title;
        _statusTitle.Text = title;
        _statusDetail.Text = detail;
        _statusDetail.ForeColor = _theme.MutedText;
        _statusProgress.Visible = state == RailState.Busy;
        _statusProgress.Style = ProgressBarStyle.Continuous;
        _statusProgress.Value = Math.Clamp(progress, 0, 100);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _syncButton.Enabled = !busy;
        _refreshButton.Enabled = !busy;
        _browseButton.Enabled = !busy;
        _browseBackupButton.Enabled = !busy;
        _backupRootBox.Enabled = !busy;
        _providerCombo.Enabled = !busy;
        _includeArchivedCheck.Enabled = !busy;
        _openBackupButton.Enabled = !busy;
        _importButton.Enabled = !busy;
        _exportButton.Enabled = !busy;
        UseWaitCursor = busy;
        if (busy)
        {
            _syncButton.Text = "同步中…";
        }
        else if (_syncButton.Text == "同步中…")
        {
            _syncButton.Text = _lastSnapshot is not null && _lastSnapshot.TotalIssues == 0
                ? "再次同步"
                : "立即同步";
        }
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }

        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void RefreshLatestBackupFooter()
    {
        try
        {
            string directory = CurrentBackupRoot();
            DirectoryInfo? latest = Directory.Exists(directory)
                ? new DirectoryInfo(directory)
                    .EnumerateDirectories()
                    .OrderByDescending(item => item.LastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
            if (latest is not null)
            {
                _lastBackupDirectory = latest.FullName;
                SetFooterBackupPath(latest.FullName);
                return;
            }
        }
        catch
        {
        }

        SetFooterBackupPath(null);
    }

    private void SetFooterBackupPath(string? path)
    {
        const string prefix = "最近备份路径： ";
        string value = string.IsNullOrWhiteSpace(path) ? "尚无备份" : path;
        _footerLabel.Text = prefix + value;
        _footerLabel.LinkArea = string.IsNullOrWhiteSpace(path)
            ? new LinkArea(0, 0)
            : new LinkArea(prefix.Length, value.Length);
    }

    private void OpenBackupDirectory()
    {
        try
        {
            string directory = _lastBackupDirectory ?? CurrentBackupRoot();
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            SetRail(RailState.Error, "无法打开备份目录", error.Message, 0);
        }
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(new AppSettings
            {
                LastCodexHome = _codexHomeBox.Text,
                TargetProvider = _providerCombo.Text,
                IncludeArchived = _includeArchivedCheck.Checked,
                LastExportDirectory = _settings.LastExportDirectory,
                BackupRootDirectory = _backupRootBox.Text,
                WindowWidth = Width,
                WindowHeight = Height,
                WindowDpi = DeviceDpi,
            });
        }
        catch
        {
        }
    }

    private SurfacePanel NewSurfacePanel(Padding padding)
    {
        return new SurfacePanel
        {
            Dock = DockStyle.Fill,
            Padding = padding,
            BackColor = _theme.Surface,
            BorderColor = _theme.Border,
            CornerRadius = 6,
        };
    }

    private Control CommandLabel(string text, float fontSize = 9.2f, FontStyle style = FontStyle.Bold)
    {
        return new CommandTextControl
        {
            Text = text,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(1, 1),
            Font = new Font("Segoe UI", fontSize, style),
            Margin = new Padding(0),
        };
    }

    private Button CreateButton(string glyph, string text, bool primary, int width)
    {
        Button button = new()
        {
            Text = text,
            Width = width,
            Height = 48,
            Margin = new Padding(6, 0, 6, 0),
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleCenter,
            AccessibleName = text,
            Cursor = Cursors.Hand,
        };
        if (!string.IsNullOrWhiteSpace(glyph))
        {
            button.Image = FluentIconFactory.Create(glyph, primary ? _theme.AccentText : _theme.Text, 20);
            button.ImageAlign = ContentAlignment.MiddleCenter;
            button.TextImageRelation = TextImageRelation.ImageBeforeText;
        }
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = _theme.Border;
        button.FlatAppearance.MouseOverBackColor = primary ? _theme.AccentHover : _theme.SurfaceAlt;
        button.BackColor = primary ? _theme.Accent : _theme.Surface;
        button.ForeColor = primary ? _theme.AccentText : _theme.Text;
        return button;
    }

    private Button CreateIconButton(string glyph, string tooltip)
    {
        Button button = new()
        {
            Text = glyph,
            Width = 36,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe Fluent Icons", 14f, FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleCenter,
            AccessibleName = tooltip,
            Cursor = Cursors.Hand,
            TabStop = true,
        };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = _theme.Border;
        button.FlatAppearance.MouseOverBackColor = _theme.SurfaceAlt;
        button.BackColor = _theme.Surface;
        _toolTip.SetToolTip(button, tooltip);
        return button;
    }

    private Image? LoadApplicationImage()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.png");
            if (File.Exists(path))
            {
                using Image image = Image.FromFile(path);
                return new Bitmap(image);
            }

            return Icon?.ToBitmap();
        }
        catch
        {
            return null;
        }
    }

    private static int ProgressForMessage(string message)
    {
        if (message.Contains("扫描", StringComparison.Ordinal)) return 8;
        if (message.Contains("备份", StringComparison.Ordinal)) return 20;
        if (message.Contains("JSONL", StringComparison.Ordinal)) return 40;
        if (message.Contains("session_index", StringComparison.Ordinal)) return 55;
        if (message.Contains("用户消息", StringComparison.Ordinal)) return 68;
        if (message.Contains("SQLite", StringComparison.Ordinal)) return 86;
        if (message.Contains("完成", StringComparison.Ordinal)) return 100;
        return 12;
    }

    private static string TopologyTitleForMessage(string message)
    {
        if (message.Contains("SQLite", StringComparison.Ordinal)) return "正在事务同步 SQLite";
        if (message.Contains("JSONL", StringComparison.Ordinal)) return "正在更新 JSONL";
        if (message.Contains("session_index", StringComparison.Ordinal)) return "正在合并会话索引";
        if (message.Contains("备份", StringComparison.Ordinal)) return "正在创建一致性备份";
        return "正在同步";
    }

    private static string ResultSummary(SyncResult result)
    {
        string databaseSummary = string.Join("；", result.Databases.Select(item =>
            $"{item.Label} 新增 {item.InsertedRows}、更新 {item.UpdatedRows}、跳过孤儿 {item.SkippedOrphans}"));
        return $"结果：JSONL 更新 {result.Jsonl.ChangedPaths.Count}、跳过 {result.Jsonl.SkippedPaths.Count}；" +
               $"session_index 新增 {result.SessionIndex.Added}、去重 {result.SessionIndex.Deduplicated}；{databaseSummary}";
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
