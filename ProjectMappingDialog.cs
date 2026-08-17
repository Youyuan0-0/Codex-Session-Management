namespace CodexSessionHotSync;

internal sealed class SmoothDataGridView : DataGridView
{
    public SmoothDataGridView()
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
    }

    protected override void OnScroll(ScrollEventArgs e)
    {
        base.OnScroll(e);
        Invalidate();
    }
}

internal sealed class ProjectMappingDialog : Form
{
    private readonly ThemePalette _theme;
    private readonly List<ChatPackProjectMapping> _mappings;
    private readonly SmoothDataGridView _grid = new();
    private readonly Label _validation = new();
    private readonly Button _importButton;

    public ProjectMappingDialog(
        ThemePalette theme,
        string packagePath,
        IReadOnlyList<ChatPackProjectMapping> mappings)
    {
        _theme = theme;
        _mappings = mappings.Select(item => item with { }).ToList();
        _importButton = CreateButton("开始导入", true, 118);
        InitializeWindow();
        BuildLayout(packagePath);
        PopulateRows();
    }

    public IReadOnlyList<ChatPackProjectMapping> Mappings => _mappings;

    private void InitializeWindow()
    {
        Text = "导入聊天记录";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(900, 520);
        MinimumSize = new Size(620, 360);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        BackColor = _theme.Background;
        ForeColor = _theme.Text;
        ShowInTaskbar = false;
        MaximizeBox = true;
        MinimizeBox = false;
        NativeTheme.Apply(this, _theme.IsDark);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyGridDpiMetrics();
        FitWindowToWorkingArea();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BeginInvoke(new Action(() =>
        {
            ApplyGridDpiMetrics();
            FitWindowToWorkingArea();
            _grid.Invalidate();
        }));
    }

    private void FitWindowToWorkingArea()
    {
        Rectangle workingArea = Screen.FromControl(this).WorkingArea;
        int minimumWidth = (int)Math.Round(620 * DeviceDpi / 96d);
        int minimumHeight = (int)Math.Round(360 * DeviceDpi / 96d);
        MinimumSize = new Size(
            Math.Min(minimumWidth, workingArea.Width),
            Math.Min(minimumHeight, workingArea.Height));
        Size = new Size(
            Math.Min(Width, workingArea.Width),
            Math.Min(Height, workingArea.Height));
        Location = new Point(
            Math.Clamp(Left, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - Width)),
            Math.Clamp(Top, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - Height)));
    }

    private void BuildLayout(string packagePath)
    {
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 18),
            ColumnCount = 1,
            RowCount = 4,
            BackColor = _theme.Background,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

        TableLayoutPanel header = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0),
            BackColor = Color.Transparent,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Label icon = new()
        {
            Text = "\uE8E5",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe Fluent Icons", 23f),
            ForeColor = _theme.Accent,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Label title = new()
        {
            Text = "项目路径映射",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 18f, FontStyle.Bold),
            ForeColor = _theme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };
        Label subtitle = new()
        {
            Text = Path.GetFileName(packagePath),
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = _theme.MutedText,
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
        };
        header.Controls.Add(icon, 0, 0);
        header.SetRowSpan(icon, 2);
        header.Controls.Add(title, 1, 0);
        header.Controls.Add(subtitle, 1, 1);

        ConfigureGrid();
        SurfacePanel gridSurface = new()
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(1),
            BackColor = _theme.Surface,
            BorderColor = _theme.Border,
            CornerRadius = 6,
        };
        gridSurface.Controls.Add(_grid);

        _validation.Dock = DockStyle.Fill;
        _validation.Font = new Font("Segoe UI", 9.5f);
        _validation.ForeColor = _theme.Warning;
        _validation.TextAlign = ContentAlignment.MiddleLeft;
        _validation.AutoEllipsis = true;

        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0),
            BackColor = Color.Transparent,
        };
        Button cancel = CreateButton("取消", false, 100);
        cancel.DialogResult = DialogResult.Cancel;
        _importButton.Click += (_, _) => AcceptMappings();
        actions.Controls.Add(_importButton);
        actions.Controls.Add(cancel);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(gridSurface, 0, 1);
        root.Controls.Add(_validation, 0, 2);
        root.Controls.Add(actions, 0, 3);
        Controls.Add(root);
        AcceptButton = _importButton;
        CancelButton = cancel;
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.BorderStyle = BorderStyle.None;
        _grid.BackgroundColor = _theme.Surface;
        _grid.GridColor = _theme.Border;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = _theme.SurfaceAlt,
            ForeColor = _theme.Text,
            SelectionBackColor = _theme.SurfaceAlt,
            SelectionForeColor = _theme.Text,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Padding = new Padding(8, 6, 8, 6),
        };
        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = _theme.Surface,
            ForeColor = _theme.Text,
            SelectionBackColor = BlendOpaque(
                _theme.Surface,
                _theme.Accent,
                _theme.IsDark ? 0.38f : 0.16f),
            SelectionForeColor = _theme.Text,
            Font = new Font("Segoe UI", 10f),
            Padding = new Padding(8, 4, 8, 4),
            WrapMode = DataGridViewTriState.False,
        };
        _grid.RowHeadersVisible = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.AutoGenerateColumns = false;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.RowTemplate.Height = 42;
        _grid.ColumnHeadersHeight = 44;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Project",
            HeaderText = "项目",
            ReadOnly = true,
            Width = 150,
            MinimumWidth = 110,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Source",
            HeaderText = "原项目路径",
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 42,
            MinimumWidth = 180,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Count",
            HeaderText = "会话",
            ReadOnly = true,
            Width = 72,
            MinimumWidth = 64,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Target",
            HeaderText = "本地项目路径",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 48,
            MinimumWidth = 200,
        });
        _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Browse",
            HeaderText = string.Empty,
            Text = "浏览",
            UseColumnTextForButtonValue = true,
            Width = 78,
            MinimumWidth = 72,
            FlatStyle = FlatStyle.Flat,
        });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Import",
            HeaderText = "导入",
            Width = 72,
            MinimumWidth = 68,
            ThreeState = false,
            FlatStyle = FlatStyle.Standard,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                NullValue = true,
            },
        });
        _grid.CellContentClick += GridCellContentClick;
        _grid.CellEndEdit += (_, _) => RefreshValidationStyles();
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            DataGridViewCell? currentCell = _grid.CurrentCell;
            if (_grid.IsCurrentCellDirty && currentCell?.OwningColumn?.Name == "Import")
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.CellValueChanged += (_, args) =>
        {
            if (args.RowIndex >= 0 && args.ColumnIndex >= 0 &&
                _grid.Columns[args.ColumnIndex].Name == "Import")
            {
                RefreshValidationStyles();
            }
        };
    }

    private void PopulateRows()
    {
        foreach (ChatPackProjectMapping mapping in _mappings)
        {
            int rowIndex = _grid.Rows.Add(
                mapping.ProjectName,
                mapping.RequiresPathMapping ? mapping.SourcePath : "-",
                mapping.SessionCount,
                mapping.RequiresPathMapping ? mapping.TargetPath : "不映射",
                mapping.RequiresPathMapping ? "浏览" : string.Empty,
                mapping.ImportSessions);
            _grid.Rows[rowIndex].Tag = mapping;
            if (!mapping.RequiresPathMapping)
            {
                _grid.Rows[rowIndex].Cells["Target"].ReadOnly = true;
                _grid.Rows[rowIndex].Cells["Browse"].ReadOnly = true;
                _grid.Rows[rowIndex].DefaultCellStyle.ForeColor = _theme.MutedText;
            }
        }

        RefreshValidationStyles();
    }

    private void GridCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Browse" ||
            _grid.Rows[e.RowIndex].Tag is not ChatPackProjectMapping mapping || !mapping.RequiresPathMapping ||
            !IsRowIncluded(_grid.Rows[e.RowIndex]))
        {
            return;
        }

        string current = Convert.ToString(_grid.Rows[e.RowIndex].Cells["Target"].Value) ?? string.Empty;
        using FolderBrowserDialog dialog = new()
        {
            Description = $"选择 {mapping.ProjectName} 的本地项目目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _grid.Rows[e.RowIndex].Cells["Target"].Value = dialog.SelectedPath;
            RefreshValidationStyles();
        }
    }

    private void AcceptMappings()
    {
        _grid.EndEdit();
        List<string> invalid = [];
        int selectedProjects = 0;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not ChatPackProjectMapping mapping)
            {
                continue;
            }

            mapping.ImportSessions = IsRowIncluded(row);
            if (!mapping.ImportSessions)
            {
                continue;
            }

            selectedProjects++;
            if (!mapping.RequiresPathMapping)
            {
                continue;
            }

            mapping.TargetPath = Convert.ToString(row.Cells["Target"].Value)?.Trim() ?? string.Empty;
            if (!Directory.Exists(mapping.TargetPath))
            {
                invalid.Add(mapping.ProjectName);
            }
        }

        if (selectedProjects == 0)
        {
            _validation.Text = "请至少选择一个需要导入的项目。";
            return;
        }

        if (invalid.Count > 0)
        {
            _validation.Text = "请选择有效的本地项目目录：" + string.Join("、", invalid);
            RefreshValidationStyles();
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void RefreshValidationStyles()
    {
        bool anyInvalid = false;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not ChatPackProjectMapping mapping)
            {
                continue;
            }

            bool included = IsRowIncluded(row);
            mapping.ImportSessions = included;
            row.DefaultCellStyle.ForeColor = included ? _theme.Text : _theme.MutedText;
            row.DefaultCellStyle.BackColor = _theme.Surface;
            row.Cells["Target"].ReadOnly = !mapping.RequiresPathMapping || !included;
            row.Cells["Browse"].ReadOnly = !mapping.RequiresPathMapping || !included;
            row.Cells["Browse"].Value = !mapping.RequiresPathMapping || !included ? string.Empty : "浏览";
            if (!mapping.RequiresPathMapping || !included)
            {
                row.Cells["Target"].Style.ForeColor = _theme.MutedText;
                row.Cells["Target"].Style.BackColor = _theme.Surface;
                continue;
            }

            string path = Convert.ToString(row.Cells["Target"].Value)?.Trim() ?? string.Empty;
            bool valid = Directory.Exists(path);
            row.Cells["Target"].Style.ForeColor = valid ? _theme.Text : _theme.Warning;
            row.Cells["Target"].Style.BackColor = valid
                ? _theme.Surface
                : BlendOpaque(_theme.Surface, _theme.Warning, _theme.IsDark ? 0.18f : 0.09f);
            anyInvalid |= !valid;
        }

        if (!anyInvalid)
        {
            _validation.Text = string.Empty;
        }

        _grid.Invalidate();
    }

    private bool IsRowIncluded(DataGridViewRow row) =>
        row.Cells["Import"].Value is not false;

    private void ApplyGridDpiMetrics()
    {
        int Scale(int logical) => Math.Max(1, (int)Math.Round(logical * DeviceDpi / 96d));

        _grid.RowTemplate.Height = Scale(42);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Height = Scale(42);
        }

        _grid.ColumnHeadersHeight = Scale(44);
        SetColumnMetrics("Project", 150, 110);
        SetColumnMetrics("Source", null, 180);
        SetColumnMetrics("Count", 72, 64);
        SetColumnMetrics("Target", null, 200);
        SetColumnMetrics("Browse", 78, 72);
        SetColumnMetrics("Import", 72, 68);

        void SetColumnMetrics(string name, int? width, int minimumWidth)
        {
            DataGridViewColumn column = _grid.Columns[name]
                ?? throw new InvalidOperationException($"缺少导入表格列：{name}");
            column.MinimumWidth = Scale(minimumWidth);
            if (width.HasValue)
            {
                column.Width = Scale(width.Value);
            }
        }
    }

    private static Color BlendOpaque(Color background, Color foreground, float amount)
    {
        float clamped = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            255,
            (int)Math.Round(background.R + (foreground.R - background.R) * clamped),
            (int)Math.Round(background.G + (foreground.G - background.G) * clamped),
            (int)Math.Round(background.B + (foreground.B - background.B) * clamped));
    }

    private Button CreateButton(string text, bool primary, int width)
    {
        Button button = new()
        {
            Text = text,
            Width = width,
            Height = 42,
            Margin = new Padding(8, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            BackColor = primary ? _theme.Accent : _theme.Surface,
            ForeColor = primary ? _theme.AccentText : _theme.Text,
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = _theme.Border;
        button.FlatAppearance.MouseOverBackColor = primary ? _theme.AccentHover : _theme.SurfaceAlt;
        return button;
    }
}
