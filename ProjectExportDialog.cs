namespace CodexSessionHotSync;

internal sealed class ProjectExportDialog : Form
{
    private readonly ThemePalette _theme;
    private readonly List<ChatPackExportProject> _projects;
    private readonly SmoothDataGridView _grid = new();
    private readonly ModernCheckBox _selectAll = new();
    private readonly Label _selectionSummary = new();
    private readonly Label _validation = new();
    private readonly Button _exportButton;
    private bool _updatingRows;

    public ProjectExportDialog(
        ThemePalette theme,
        string codexHome,
        IReadOnlyList<ChatPackExportProject> projects)
    {
        _theme = theme;
        _projects = projects.Select(item => item with { }).ToList();
        _exportButton = CreateButton("继续导出", true, 118);
        InitializeWindow();
        BuildLayout(codexHome);
        PopulateRows();
    }

    public IReadOnlySet<string> SelectedSessionIds => _projects
        .Where(item => item.ExportSessions)
        .SelectMany(item => item.SessionIds)
        .ToHashSet(StringComparer.Ordinal);

    private void InitializeWindow()
    {
        Text = "选择导出项目";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(840, 520);
        MinimumSize = new Size(560, 360);
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
        int minimumWidth = (int)Math.Round(560 * DeviceDpi / 96d);
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

    private void BuildLayout(string codexHome)
    {
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 18),
            ColumnCount = 1,
            RowCount = 5,
            BackColor = _theme.Background,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
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
            Text = "\uE74E",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe Fluent Icons", 23f),
            ForeColor = _theme.Accent,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Label title = new()
        {
            Text = "选择要导出的项目",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 18f, FontStyle.Bold),
            ForeColor = _theme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };
        Label subtitle = new()
        {
            Text = codexHome,
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

        TableLayoutPanel selectionBar = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            BackColor = Color.Transparent,
        };
        selectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        selectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ConfigureSelectAll();
        _selectionSummary.Dock = DockStyle.Fill;
        _selectionSummary.Font = new Font("Segoe UI", 9.5f);
        _selectionSummary.ForeColor = _theme.MutedText;
        _selectionSummary.TextAlign = ContentAlignment.MiddleRight;
        _selectionSummary.AutoEllipsis = true;
        selectionBar.Controls.Add(_selectAll, 0, 0);
        selectionBar.Controls.Add(_selectionSummary, 1, 0);

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
        _exportButton.Click += (_, _) => AcceptSelection();
        actions.Controls.Add(_exportButton);
        actions.Controls.Add(cancel);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(selectionBar, 0, 1);
        root.Controls.Add(gridSurface, 0, 2);
        root.Controls.Add(_validation, 0, 3);
        root.Controls.Add(actions, 0, 4);
        Controls.Add(root);
        AcceptButton = _exportButton;
        CancelButton = cancel;
    }

    private void ConfigureSelectAll()
    {
        _selectAll.Text = "全选项目";
        _selectAll.Dock = DockStyle.Fill;
        _selectAll.AutoCheck = false;
        _selectAll.Checked = true;
        _selectAll.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _selectAll.ForeColor = _theme.Text;
        _selectAll.SurfaceColor = _theme.Background;
        _selectAll.BoxBorderColor = _theme.MutedText;
        _selectAll.AccentColor = _theme.Accent;
        _selectAll.Click += (_, _) => ToggleAllProjects();
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
            Width = 190,
            MinimumWidth = 130,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Source",
            HeaderText = "项目路径",
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 240,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Count",
            HeaderText = "会话",
            ReadOnly = true,
            Width = 78,
            MinimumWidth = 68,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
            },
        });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Export",
            HeaderText = "导出",
            Width = 78,
            MinimumWidth = 70,
            ThreeState = false,
            FlatStyle = FlatStyle.Standard,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                NullValue = true,
            },
        });
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell?.OwningColumn?.Name == "Export")
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.CellValueChanged += (_, args) =>
        {
            if (!_updatingRows && args.RowIndex >= 0 && args.ColumnIndex >= 0 &&
                _grid.Columns[args.ColumnIndex].Name == "Export")
            {
                RefreshSelectionState();
            }
        };
    }

    private void PopulateRows()
    {
        foreach (ChatPackExportProject project in _projects)
        {
            int rowIndex = _grid.Rows.Add(
                project.ProjectName,
                project.SourcePath.Length > 0 ? project.SourcePath : "-",
                project.SessionCount,
                project.ExportSessions);
            _grid.Rows[rowIndex].Tag = project;
        }

        RefreshSelectionState();
    }

    private void ToggleAllProjects()
    {
        _grid.EndEdit();
        bool select = _grid.Rows.Cast<DataGridViewRow>().Any(row => !IsRowIncluded(row));
        _updatingRows = true;
        try
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                row.Cells["Export"].Value = select;
                if (row.Tag is ChatPackExportProject project)
                {
                    project.ExportSessions = select;
                }
            }
        }
        finally
        {
            _updatingRows = false;
        }

        RefreshSelectionState();
    }

    private void AcceptSelection()
    {
        _grid.EndEdit();
        RefreshSelectionState();
        if (_projects.All(item => !item.ExportSessions))
        {
            _validation.Text = "请至少选择一个需要导出的项目。";
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void RefreshSelectionState()
    {
        int selectedProjects = 0;
        int selectedSessions = 0;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not ChatPackExportProject project)
            {
                continue;
            }

            bool included = IsRowIncluded(row);
            project.ExportSessions = included;
            row.DefaultCellStyle.ForeColor = included ? _theme.Text : _theme.MutedText;
            if (included)
            {
                selectedProjects++;
                selectedSessions += project.SessionCount;
            }
        }

        _selectAll.Checked = selectedProjects == _projects.Count && _projects.Count > 0;
        _selectionSummary.Text = $"已选择 {selectedProjects:N0}/{_projects.Count:N0} 个项目 · {selectedSessions:N0} 个会话";
        _validation.Text = selectedProjects == 0 ? "请至少选择一个需要导出的项目。" : string.Empty;
        _grid.Invalidate();
    }

    private static bool IsRowIncluded(DataGridViewRow row) =>
        row.Cells["Export"].Value is not false;

    private void ApplyGridDpiMetrics()
    {
        int Scale(int logical) => Math.Max(1, (int)Math.Round(logical * DeviceDpi / 96d));

        _grid.RowTemplate.Height = Scale(42);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Height = Scale(42);
        }

        _grid.ColumnHeadersHeight = Scale(44);
        SetColumnMetrics("Project", 190, 130);
        SetColumnMetrics("Source", null, 240);
        SetColumnMetrics("Count", 78, 68);
        SetColumnMetrics("Export", 78, 70);

        void SetColumnMetrics(string name, int? width, int minimumWidth)
        {
            DataGridViewColumn column = _grid.Columns[name]
                ?? throw new InvalidOperationException($"缺少导出表格列：{name}");
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
