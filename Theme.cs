using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.ComponentModel;
using Microsoft.Win32;

namespace CodexSessionHotSync;

internal sealed record ThemePalette
{
    public required bool IsDark { get; init; }
    public required Color Background { get; init; }
    public required Color Surface { get; init; }
    public required Color SurfaceAlt { get; init; }
    public required Color Border { get; init; }
    public required Color Text { get; init; }
    public required Color MutedText { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentHover { get; init; }
    public required Color AccentText { get; init; }
    public required Color Success { get; init; }
    public required Color Warning { get; init; }
    public required Color Danger { get; init; }
    public required Color Info { get; init; }

    public static ThemePalette Current()
    {
        bool dark = IsSystemDarkTheme();
        return dark
            ? new ThemePalette
            {
                IsDark = true,
                Background = Color.FromArgb(23, 27, 29),
                Surface = Color.FromArgb(32, 37, 40),
                SurfaceAlt = Color.FromArgb(42, 48, 51),
                Border = Color.FromArgb(69, 77, 81),
                Text = Color.FromArgb(240, 244, 246),
                MutedText = Color.FromArgb(168, 179, 186),
                Accent = Color.FromArgb(52, 184, 176),
                AccentHover = Color.FromArgb(66, 199, 190),
                AccentText = Color.White,
                Success = Color.FromArgb(67, 198, 112),
                Warning = Color.FromArgb(242, 177, 72),
                Danger = Color.FromArgb(238, 107, 103),
                Info = Color.FromArgb(91, 166, 239),
            }
            : new ThemePalette
            {
                IsDark = false,
                Background = Color.FromArgb(247, 249, 250),
                Surface = Color.White,
                SurfaceAlt = Color.FromArgb(249, 250, 251),
                Border = Color.FromArgb(215, 220, 223),
                Text = Color.FromArgb(25, 30, 34),
                MutedText = Color.FromArgb(91, 98, 104),
                Accent = Color.FromArgb(31, 157, 151),
                AccentHover = Color.FromArgb(22, 139, 133),
                AccentText = Color.White,
                Success = Color.FromArgb(46, 181, 91),
                Warning = Color.FromArgb(205, 126, 0),
                Danger = Color.FromArgb(190, 56, 55),
                Info = Color.FromArgb(43, 112, 204),
            };
    }

    private static bool IsSystemDarkTheme()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return Convert.ToInt32(value) == 0;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class SurfacePanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 8;

    public SurfacePanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        using SolidBrush background = new(BackColor);
        e.Graphics.FillRectangle(background, ClientRectangle);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = new(0, 0, Width - 1, Height - 1);
        int radius = Math.Min(CornerRadius, Math.Min(bounds.Width, bounds.Height) / 2);
        using GraphicsPath path = RoundedRectangle(bounds, Math.Max(1, radius));
        using Pen pen = new(BorderColor, 1);
        e.Graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        int diameter = Math.Max(2, radius * 2);
        GraphicsPath path = new();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class ModernCheckBox : CheckBox
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentColor { get; set; } = Color.Teal;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BoxBorderColor { get; set; } = Color.Gray;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SurfaceColor { get; set; } = SystemColors.Control;

    public ModernCheckBox()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);
        AutoSize = false;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size textSize = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        return new Size(textSize.Width + 30, Math.Max(26, textSize.Height + 8));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int boxSize = Math.Clamp((int)Math.Round(Font.Height * 0.9), 16, 20);
        Rectangle box = new(1, Math.Max(0, (ClientSize.Height - boxSize) / 2), boxSize, boxSize);
        Color border = Enabled ? BoxBorderColor : SystemColors.GrayText;
        Color textColor = Enabled ? ForeColor : SystemColors.GrayText;

        using SolidBrush surface = new(SurfaceColor);
        e.Graphics.FillRectangle(surface, ClientRectangle);
        if (Checked)
        {
            using SolidBrush fill = new(AccentColor);
            e.Graphics.FillRectangle(fill, box);
            using Pen check = new(Color.White, 2.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            e.Graphics.DrawLines(check,
            [
                new PointF(box.Left + box.Width * 0.22f, box.Top + box.Height * 0.53f),
                new PointF(box.Left + box.Width * 0.43f, box.Top + box.Height * 0.72f),
                new PointF(box.Left + box.Width * 0.79f, box.Top + box.Height * 0.29f),
            ]);
        }
        else
        {
            using SolidBrush fill = new(SurfaceColor);
            using Pen pen = new(border, 1.4f);
            e.Graphics.FillRectangle(fill, box);
            e.Graphics.DrawRectangle(pen, box);
        }

        Rectangle textBounds = new(box.Right + 9, 0, Math.Max(0, ClientSize.Width - box.Right - 9), ClientSize.Height);
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            textBounds,
            textColor,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix |
            TextFormatFlags.NoPadding);
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }
}

internal static class BrandLogoFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Bitmap CreateBitmap(int size)
    {
        Bitmap bitmap = new(size, size);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        RectangleF circle = new(1f, 1f, size - 2f, size - 2f);
        using LinearGradientBrush fill = new(
            circle,
            Color.FromArgb(80, 194, 188),
            Color.FromArgb(28, 139, 135),
            45f);
        graphics.FillEllipse(fill, circle);

        float center = size / 2f;
        float radius = size * 0.27f;
        using Pen pen = new(Color.White, Math.Max(2f, size * 0.075f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        RectangleF arc = new(center - radius, center - radius, radius * 2f, radius * 2f);
        graphics.DrawArc(pen, arc, 210f, 150f);
        graphics.DrawArc(pen, arc, 30f, 150f);
        using SolidBrush arrow = new(Color.White);
        float wing = size * 0.115f;
        graphics.FillPolygon(arrow,
        [
            new PointF(center + radius + size * 0.02f, center - size * 0.09f),
            new PointF(center + radius - wing, center - size * 0.11f),
            new PointF(center + radius - size * 0.02f, center + size * 0.045f),
        ]);
        graphics.FillPolygon(arrow,
        [
            new PointF(center - radius - size * 0.02f, center + size * 0.09f),
            new PointF(center - radius + wing, center + size * 0.11f),
            new PointF(center - radius + size * 0.02f, center - size * 0.045f),
        ]);
        return bitmap;
    }

    public static Icon CreateIcon(int size = 64)
    {
        using Bitmap bitmap = CreateBitmap(size);
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }
}

internal sealed class StatusGlyphControl : Control
{
    private RailState _state = RailState.Ready;
    private Color _stateColor = Color.Teal;

    public StatusGlyphControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);
        BackColor = Color.Transparent;
        TabStop = false;
    }

    public void SetState(RailState state, Color color)
    {
        _state = state;
        _stateColor = color;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float size = Math.Min(ClientSize.Width, ClientSize.Height) - 6f;
        RectangleF bounds = new(
            (ClientSize.Width - size) / 2f,
            (ClientSize.Height - size) / 2f,
            size,
            size);

        if (_state == RailState.Success || _state == RailState.Busy)
        {
            using SolidBrush fill = new(_stateColor);
            e.Graphics.FillEllipse(fill, bounds);
            if (_state == RailState.Success)
            {
                DrawCheck(e.Graphics, bounds);
            }
            else
            {
                DrawSync(e.Graphics, bounds);
            }
        }
        else
        {
            DrawWarning(e.Graphics, bounds);
        }
    }

    private static void DrawCheck(Graphics graphics, RectangleF bounds)
    {
        using Pen pen = new(Color.White, Math.Max(3f, bounds.Width * 0.09f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLines(pen,
        [
            new PointF(bounds.Left + bounds.Width * 0.25f, bounds.Top + bounds.Height * 0.52f),
            new PointF(bounds.Left + bounds.Width * 0.43f, bounds.Top + bounds.Height * 0.69f),
            new PointF(bounds.Left + bounds.Width * 0.76f, bounds.Top + bounds.Height * 0.32f),
        ]);
    }

    private static void DrawSync(Graphics graphics, RectangleF bounds)
    {
        using Pen pen = new(Color.White, Math.Max(2.5f, bounds.Width * 0.07f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        RectangleF arc = RectangleF.Inflate(bounds, -bounds.Width * 0.24f, -bounds.Height * 0.24f);
        graphics.DrawArc(pen, arc, 205f, 190f);
        graphics.DrawArc(pen, arc, 25f, 190f);
    }

    private void DrawWarning(Graphics graphics, RectangleF bounds)
    {
        PointF[] triangle =
        [
            new(bounds.Left + bounds.Width / 2f, bounds.Top + 2f),
            new(bounds.Right - 2f, bounds.Bottom - 3f),
            new(bounds.Left + 2f, bounds.Bottom - 3f),
        ];
        using Pen pen = new(_stateColor, Math.Max(2f, bounds.Width * 0.055f))
        {
            LineJoin = LineJoin.Round,
        };
        graphics.DrawPolygon(pen, triangle);
        using Pen mark = new(_stateColor, Math.Max(2f, bounds.Width * 0.055f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        float center = bounds.Left + bounds.Width / 2f;
        graphics.DrawLine(mark, center, bounds.Top + bounds.Height * 0.34f, center, bounds.Top + bounds.Height * 0.61f);
        graphics.DrawEllipse(mark, center - 0.5f, bounds.Top + bounds.Height * 0.75f, 1f, 1f);
    }
}

internal static class FluentIconFactory
{
    public static Bitmap Create(string glyph, Color color, int size = 20)
    {
        Bitmap bitmap = new(size, size);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using Font font = new("Segoe Fluent Icons", Math.Max(10, size - 4), FontStyle.Regular, GraphicsUnit.Pixel);
        using SolidBrush brush = new(color);
        StringFormat format = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        graphics.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
        return bitmap;
    }
}

internal static class NativeTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Form form, bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        int enabled = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int));
    }
}
