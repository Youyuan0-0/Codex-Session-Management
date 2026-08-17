using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace CodexSessionHotSync;

internal enum SyncTopologyState
{
    Ready,
    Busy,
    Success,
    Error,
}

internal sealed record DatabaseVisual(
    string Label,
    string Value,
    int Missing,
    int WrongPath,
    int WrongProvider,
    bool Exists,
    bool Readable);

internal sealed class SyncTopologyPanel : Control
{
    private readonly System.Windows.Forms.Timer _animationTimer;
    private ThemePalette _palette;
    private SyncTopologyState _state = SyncTopologyState.Ready;
    private DatabaseVisual _legacy = new("根目录 state_5.sqlite", "-", 0, 0, 0, false, false);
    private DatabaseVisual _modern = new("sqlite/state_5.sqlite", "-", 0, 0, 0, false, false);
    private int _sessionCount;
    private int _indexCount;
    private int _issueCount;
    private int _targetProgress;
    private float _displayProgress;
    private float _phase;
    private string _provider = "openai";
    private string _stateTitle = string.Empty;
    private string _stateSubtitle = string.Empty;

    public SyncTopologyPanel(ThemePalette palette)
    {
        _palette = palette;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        BackColor = palette.Surface;
        AccessibleName = "会话同步拓扑";
        _animationTimer = new System.Windows.Forms.Timer { Interval = 24 };
        _animationTimer.Tick += (_, _) => Animate();
    }

    public void ApplyPalette(ThemePalette palette)
    {
        _palette = palette;
        BackColor = palette.Surface;
        Invalidate();
    }

    public void UpdateData(
        int sessionCount,
        int indexCount,
        int issueCount,
        string provider,
        DatabaseVisual legacy,
        DatabaseVisual modern)
    {
        _sessionCount = sessionCount;
        _indexCount = indexCount;
        _issueCount = issueCount;
        _provider = provider;
        _legacy = legacy;
        _modern = modern;
        Invalidate();
    }

    public void SetState(
        SyncTopologyState state,
        string? title = null,
        string? subtitle = null,
        int progress = 0)
    {
        _state = state;
        _stateTitle = title ?? string.Empty;
        _stateSubtitle = subtitle ?? string.Empty;
        _targetProgress = Math.Clamp(progress, 0, 100);
        if (state == SyncTopologyState.Busy)
        {
            _animationTimer.Start();
        }
        else
        {
            _animationTimer.Stop();
            _displayProgress = state == SyncTopologyState.Success ? 100 : _targetProgress;
        }

        Invalidate();
    }

    public void SetProgress(int progress, string? title = null)
    {
        _targetProgress = Math.Clamp(progress, 0, 100);
        if (!string.IsNullOrWhiteSpace(title))
        {
            _stateTitle = title;
        }

        _state = SyncTopologyState.Busy;
        _animationTimer.Start();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(BackColor);

        float width = ClientSize.Width;
        float height = ClientSize.Height;
        if (width < 640f || height < 220f)
        {
            return;
        }

        RectangleF outer = new(0.5f, 0.5f, width - 1f, height - 1f);
        using GraphicsPath outerPath = RoundedRectangle(outer, 6f);
        using SolidBrush outerFill = new(_palette.Surface);
        using Pen outerBorder = new(_palette.Border, 1f);
        graphics.FillPath(outerFill, outerPath);
        graphics.DrawPath(outerBorder, outerPath);

        float contentLeft = 18f;
        float contentTop = 14f;
        float contentWidth = width - 36f;
        float contentHeight = height - 28f;
        float sourceX = contentLeft + contentWidth * 0.14f;
        float stateX = contentLeft + contentWidth * 0.455f;
        float cardsX = contentLeft + contentWidth * 0.65f;
        float cardsWidth = contentLeft + contentWidth - cardsX - 22f;
        float cardsGap = Math.Clamp(contentHeight * 0.065f, 12f, 18f);
        float cardHeight = Math.Min(98f, (contentHeight - cardsGap - 20f) / 2f);
        float cardsTop = contentTop + (contentHeight - cardHeight * 2f - cardsGap) / 2f;
        float topCardCenter = cardsTop + cardHeight / 2f;
        float bottomCardY = cardsTop + cardHeight + cardsGap;
        float bottomCardCenter = bottomCardY + cardHeight / 2f;
        float stateY = contentTop + Math.Min(contentHeight * 0.31f, 96f);
        float ringRadius = Math.Clamp(contentHeight * 0.19f, 45f, 57f);
        bool compact = width < 1060f || height < 270f;

        DrawConnectors(
            graphics,
            sourceX,
            stateX,
            stateY,
            ringRadius,
            cardsX,
            topCardCenter,
            bottomCardCenter);
        DrawSource(graphics, sourceX, stateY, contentHeight, compact);
        DrawState(graphics, stateX, stateY, ringRadius, compact);
        DrawDatabaseCard(graphics, _legacy, new RectangleF(cardsX, cardsTop, cardsWidth, cardHeight), compact);
        DrawDatabaseCard(graphics, _modern, new RectangleF(cardsX, bottomCardY, cardsWidth, cardHeight), compact);
    }

    private void Animate()
    {
        _phase = (_phase + 2.8f) % 24f;
        if (_displayProgress < _targetProgress)
        {
            _displayProgress = Math.Min(
                _targetProgress,
                _displayProgress + Math.Max(0.4f, (_targetProgress - _displayProgress) * 0.08f));
        }

        Invalidate();
    }

    private void DrawSource(Graphics graphics, float centerX, float centerY, float contentHeight, bool compact)
    {
        float iconHeight = Math.Clamp(contentHeight * 0.40f, 82f, 104f);
        float iconWidth = iconHeight * 0.72f;
        RectangleF document = new(centerX - iconWidth / 2f, centerY - iconHeight / 2f - 2f, iconWidth, iconHeight);
        DrawJsonlDocument(graphics, document);

        float labelTop = document.Bottom + (compact ? 8f : 12f);
        DrawCenteredText(
            graphics,
            "JSONL 会话",
            new Font("Segoe UI", compact ? 11.5f : 13f, FontStyle.Bold),
            _palette.Text,
            new RectangleF(centerX - 105f, labelTop, 210f, 28f));
        DrawCenteredText(
            graphics,
            _sessionCount.ToString("N0"),
            new Font("Segoe UI", compact ? 18f : 21f, FontStyle.Bold),
            _palette.Accent,
            new RectangleF(centerX - 100f, labelTop + 29f, 200f, 38f));
        DrawCenteredText(
            graphics,
            $"索引值：{_indexCount:N0}",
            new Font("Segoe UI", compact ? 9f : 10.5f),
            _palette.MutedText,
            new RectangleF(centerX - 110f, labelTop + 67f, 220f, 26f));
    }

    private void DrawConnectors(
        Graphics graphics,
        float sourceX,
        float stateX,
        float stateY,
        float radius,
        float cardsX,
        float topCardCenter,
        float bottomCardCenter)
    {
        Color color = _state == SyncTopologyState.Error ? _palette.Warning : _palette.Accent;
        using Pen line = new(color, 1.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        float sourceStartX = sourceX + 92f;
        float sourceStartY = stateY + 55f;
        float stateLeft = stateX - radius - 34f;
        float leftElbow = sourceStartX + Math.Max(42f, (stateLeft - sourceStartX) * 0.36f);
        using GraphicsPath leftPath = new();
        leftPath.AddLine(sourceStartX, sourceStartY, leftElbow, sourceStartY);
        leftPath.AddLine(leftElbow, sourceStartY, leftElbow, stateY);
        leftPath.AddLine(leftElbow, stateY, stateLeft, stateY);
        graphics.DrawPath(line, leftPath);

        float stateRight = stateX + radius + 34f;
        float junctionX = cardsX - 62f;
        graphics.DrawLine(line, stateRight, stateY, junctionX, stateY);
        graphics.DrawLine(line, junctionX, topCardCenter, junctionX, bottomCardCenter);
        graphics.DrawLine(line, junctionX, topCardCenter, cardsX - 4f, topCardCenter);
        graphics.DrawLine(line, junctionX, bottomCardCenter, cardsX - 4f, bottomCardCenter);
        DrawArrowHead(graphics, new PointF(cardsX - 3f, topCardCenter), color);
        DrawArrowHead(graphics, new PointF(cardsX - 3f, bottomCardCenter), color);

        if (_state == SyncTopologyState.Busy)
        {
            using Pen motion = new(_palette.Accent, 2.2f)
            {
                DashPattern = [8f, 10f],
                DashOffset = -_phase,
            };
            graphics.DrawLine(motion, leftElbow + 8f, stateY - 7f, stateLeft - 5f, stateY - 7f);
            graphics.DrawLine(motion, stateRight + 5f, stateY - 7f, junctionX - 7f, stateY - 7f);
        }
    }

    private void DrawState(Graphics graphics, float centerX, float centerY, float radius, bool compact)
    {
        RectangleF circle = new(centerX - radius, centerY - radius, radius * 2f, radius * 2f);
        Color color = _state switch
        {
            SyncTopologyState.Success => _palette.Success,
            SyncTopologyState.Error => _palette.Warning,
            _ => _palette.Accent,
        };
        using Pen faint = new(_palette.Border, 2f);
        using Pen strong = new(color, compact ? 6f : 8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawEllipse(faint, circle);

        switch (_state)
        {
            case SyncTopologyState.Success:
                graphics.DrawEllipse(strong, circle);
                DrawCheck(graphics, centerX, centerY, color, radius);
                DrawStateText(
                    graphics,
                    centerX,
                    circle.Bottom + 7f,
                    "0 项待处理",
                    string.IsNullOrWhiteSpace(_stateTitle) ? "同步完成" : _stateTitle,
                    string.IsNullOrWhiteSpace(_stateSubtitle) ? $"{_sessionCount:N0} 个会话已一致" : _stateSubtitle,
                    color,
                    compact);
                break;

            case SyncTopologyState.Busy:
                graphics.DrawArc(strong, circle, -90f, Math.Max(8f, _displayProgress * 3.6f));
                DrawCenteredText(
                    graphics,
                    $"{Math.Round(_displayProgress):0}%",
                    new Font("Segoe UI", compact ? 18f : 22f, FontStyle.Bold),
                    color,
                    circle);
                DrawStateText(
                    graphics,
                    centerX,
                    circle.Bottom + 9f,
                    "正在处理",
                    string.IsNullOrWhiteSpace(_stateTitle) ? "正在同步" : _stateTitle,
                    $"目标进度 {_targetProgress:N0}%",
                    color,
                    compact);
                break;

            case SyncTopologyState.Error:
                graphics.DrawEllipse(strong, circle);
                DrawPauseLock(graphics, centerX, centerY, color, radius);
                DrawStateText(
                    graphics,
                    centerX,
                    circle.Bottom + 9f,
                    "同步已暂停",
                    string.IsNullOrWhiteSpace(_stateTitle) ? "SQLite 正在被占用" : _stateTitle,
                    string.IsNullOrWhiteSpace(_stateSubtitle) ? "未写入数据库更改" : _stateSubtitle,
                    color,
                    compact);
                break;

            default:
                graphics.DrawEllipse(strong, circle);
                DrawSyncArrows(graphics, centerX, centerY, color, radius);
                DrawStateText(
                    graphics,
                    centerX,
                    circle.Bottom + 8f,
                    $"{_issueCount:N0} 项待处理",
                    "等待同步",
                    $"{_sessionCount:N0} 个会话以 JSONL 为准",
                    _palette.Warning,
                    compact);
                break;
        }
    }

    private void DrawStateText(
        Graphics graphics,
        float centerX,
        float top,
        string first,
        string second,
        string third,
        Color firstColor,
        bool compact)
    {
        DrawCenteredText(
            graphics,
            first,
            new Font("Segoe UI", compact ? 9.5f : 11f, FontStyle.Bold),
            firstColor,
            new RectangleF(centerX - 145f, top, 290f, 25f));
        DrawCenteredText(
            graphics,
            second,
            new Font("Segoe UI", compact ? 11f : 12.5f, FontStyle.Bold),
            _palette.Text,
            new RectangleF(centerX - 150f, top + 25f, 300f, 28f));
        DrawCenteredText(
            graphics,
            third,
            new Font("Segoe UI", compact ? 9.5f : 11f, FontStyle.Bold),
            _palette.Text,
            new RectangleF(centerX - 160f, top + 53f, 320f, 26f));
    }

    private void DrawDatabaseCard(Graphics graphics, DatabaseVisual database, RectangleF bounds, bool compact)
    {
        RectangleF shadowBounds = bounds;
        shadowBounds.Offset(0f, 2f);
        using GraphicsPath shadowPath = RoundedRectangle(shadowBounds, 5f);
        using SolidBrush shadow = new(Color.FromArgb(_palette.IsDark ? 35 : 18, Color.Black));
        graphics.FillPath(shadow, shadowPath);

        using GraphicsPath cardPath = RoundedRectangle(bounds, 5f);
        using SolidBrush fill = new(_palette.Surface);
        using Pen border = new(_palette.Border, 1f);
        graphics.FillPath(fill, cardPath);
        graphics.DrawPath(border, cardPath);

        float iconSize = Math.Clamp(bounds.Height * 0.5f, 44f, 56f);
        RectangleF iconBounds = new(
            bounds.Left + 24f,
            bounds.Top + (bounds.Height - iconSize) / 2f,
            iconSize * 0.72f,
            iconSize);
        DrawDatabaseIcon(graphics, iconBounds);

        float textX = iconBounds.Right + 26f;
        float textWidth = Math.Max(100f, bounds.Right - textX - 16f);
        DrawText(
            graphics,
            database.Label,
            new Font("Segoe UI", compact ? 11f : 12.5f, FontStyle.Bold),
            _palette.Text,
            new RectangleF(textX, bounds.Top + 10f, textWidth, 28f));
        Color valueColor = database.Exists && database.Readable ? _palette.Accent : _palette.MutedText;
        DrawText(
            graphics,
            database.Value,
            new Font("Segoe UI", compact ? 13.5f : 16f, FontStyle.Bold),
            valueColor,
            new RectangleF(textX, bounds.Top + 38f, textWidth, 31f));

        string detail = DatabaseDetail(database);
        DrawText(
            graphics,
            detail,
            new Font("Segoe UI", compact ? 8.5f : 9.5f),
            _palette.MutedText,
            new RectangleF(textX, bounds.Top + 69f, textWidth, 25f));
    }

    private string DatabaseDetail(DatabaseVisual database)
    {
        if (!database.Exists)
        {
            return "未检测到数据库";
        }

        if (!database.Readable)
        {
            return "数据库不可读 · 请稍后重试";
        }

        string path = database.WrongPath == 0 ? "路径正常" : $"路径错误 {database.WrongPath:N0}";
        string provider = database.WrongProvider == 0 ? _provider : $"差异 {database.WrongProvider:N0}";
        return $"缺 {database.Missing:N0} · {path} · 源 {provider}";
    }

    private void DrawJsonlDocument(Graphics graphics, RectangleF bounds)
    {
        using Pen pen = new(_palette.Accent, 3f)
        {
            LineJoin = LineJoin.Round,
        };
        using GraphicsPath path = new();
        float fold = bounds.Width * 0.34f;
        path.AddLine(bounds.Left, bounds.Top, bounds.Right - fold, bounds.Top);
        path.AddLine(bounds.Right - fold, bounds.Top, bounds.Right, bounds.Top + fold);
        path.AddLine(bounds.Right, bounds.Top + fold, bounds.Right, bounds.Bottom);
        path.AddLine(bounds.Right, bounds.Bottom, bounds.Left, bounds.Bottom);
        path.CloseFigure();
        graphics.DrawPath(pen, path);
        graphics.DrawLine(pen, bounds.Right - fold, bounds.Top, bounds.Right - fold, bounds.Top + fold);
        graphics.DrawLine(pen, bounds.Right - fold, bounds.Top + fold, bounds.Right, bounds.Top + fold);
        DrawCenteredText(
            graphics,
            "JSONL",
            new Font("Segoe UI", Math.Max(9f, bounds.Width * 0.15f), FontStyle.Bold),
            _palette.Accent,
            new RectangleF(bounds.Left + 2f, bounds.Top + bounds.Height * 0.42f, bounds.Width - 4f, 30f));
    }

    private void DrawDatabaseIcon(Graphics graphics, RectangleF bounds)
    {
        using Pen pen = new(_palette.Accent, 2.2f);
        float lip = Math.Max(11f, bounds.Height * 0.22f);
        graphics.DrawEllipse(pen, bounds.Left, bounds.Top, bounds.Width, lip);
        graphics.DrawLine(pen, bounds.Left, bounds.Top + lip / 2f, bounds.Left, bounds.Bottom - lip / 2f);
        graphics.DrawLine(pen, bounds.Right, bounds.Top + lip / 2f, bounds.Right, bounds.Bottom - lip / 2f);
        graphics.DrawArc(pen, bounds.Left, bounds.Bottom - lip, bounds.Width, lip, 0f, 180f);
        graphics.DrawArc(pen, bounds.Left, bounds.Top + bounds.Height * 0.31f, bounds.Width, lip, 0f, 180f);
        graphics.DrawArc(pen, bounds.Left, bounds.Top + bounds.Height * 0.59f, bounds.Width, lip, 0f, 180f);
    }

    private static void DrawCheck(Graphics graphics, float centerX, float centerY, Color color, float radius)
    {
        using Pen pen = new(color, Math.Max(6f, radius * 0.14f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLines(pen,
        [
            new PointF(centerX - radius * 0.42f, centerY + radius * 0.03f),
            new PointF(centerX - radius * 0.12f, centerY + radius * 0.33f),
            new PointF(centerX + radius * 0.45f, centerY - radius * 0.34f),
        ]);
    }

    private static void DrawSyncArrows(Graphics graphics, float centerX, float centerY, Color color, float radius)
    {
        using Pen pen = new(color, Math.Max(4f, radius * 0.09f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        float arcRadius = radius * 0.52f;
        RectangleF arc = new(centerX - arcRadius, centerY - arcRadius, arcRadius * 2f, arcRadius * 2f);
        graphics.DrawArc(pen, arc, 205f, 190f);
        graphics.DrawArc(pen, arc, 25f, 190f);
        using SolidBrush brush = new(color);
        float wing = radius * 0.28f;
        graphics.FillPolygon(brush,
        [
            new PointF(centerX + arcRadius + 2f, centerY - radius * 0.16f),
            new PointF(centerX + arcRadius - wing, centerY - radius * 0.20f),
            new PointF(centerX + arcRadius - radius * 0.05f, centerY + radius * 0.08f),
        ]);
        graphics.FillPolygon(brush,
        [
            new PointF(centerX - arcRadius - 2f, centerY + radius * 0.16f),
            new PointF(centerX - arcRadius + wing, centerY + radius * 0.20f),
            new PointF(centerX - arcRadius + radius * 0.05f, centerY - radius * 0.08f),
        ]);
    }

    private static void DrawPauseLock(Graphics graphics, float centerX, float centerY, Color color, float radius)
    {
        using SolidBrush brush = new(color);
        graphics.FillRectangle(brush, centerX - radius * 0.38f, centerY - radius * 0.30f, radius * 0.12f, radius * 0.60f);
        graphics.FillRectangle(brush, centerX - radius * 0.14f, centerY - radius * 0.30f, radius * 0.12f, radius * 0.60f);
        using Pen pen = new(color, Math.Max(2f, radius * 0.06f));
        RectangleF body = new(centerX + radius * 0.13f, centerY + radius * 0.03f, radius * 0.38f, radius * 0.34f);
        graphics.DrawRectangle(pen, body.X, body.Y, body.Width, body.Height);
        graphics.DrawArc(pen, centerX + radius * 0.19f, centerY - radius * 0.20f, radius * 0.26f, radius * 0.38f, 180f, 180f);
    }

    private static void DrawArrowHead(Graphics graphics, PointF tip, Color color)
    {
        using SolidBrush brush = new(color);
        graphics.FillPolygon(brush,
        [
            tip,
            new PointF(tip.X - 9f, tip.Y - 5f),
            new PointF(tip.X - 9f, tip.Y + 5f),
        ]);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = Math.Max(2f, radius * 2f);
        GraphicsPath path = new();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    private static void DrawText(Graphics graphics, string text, Font font, Color color, RectangleF bounds)
    {
        using (font)
        using (SolidBrush brush = new(color))
        using (StringFormat format = new()
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        })
        {
            graphics.DrawString(text, font, brush, bounds, format);
        }
    }

    private static void DrawCenteredText(Graphics graphics, string text, Font font, Color color, RectangleF bounds)
    {
        using (font)
        using (SolidBrush brush = new(color))
        using (StringFormat format = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        })
        {
            graphics.DrawString(text, font, brush, bounds, format);
        }
    }
}
