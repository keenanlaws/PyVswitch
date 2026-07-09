using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using PythonVersionSwitch.Models;

namespace PythonVersionSwitch.Services;

public static partial class TrayIconFactory
{
    public static Icon Create(PythonInstall? activeInstall, AppTheme theme)
    {
        var label = activeInstall is null
            ? "PY"
            : string.IsNullOrWhiteSpace(activeInstall.Version)
                ? "PY"
                : string.Join('.', activeInstall.Version.Split('.').Take(2));

        if (label.Length > 4)
        {
            label = label[..4];
        }

        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.Clear(Color.Transparent);

        var accent = activeInstall?.Architecture == "x86" ? Color.FromArgb(255, 212, 59) : Color.FromArgb(55, 118, 171);
        using var path = RoundedRectangle(new RectangleF(6, 6, 52, 52), 14);
        using var brush = new LinearGradientBrush(new Point(6, 6), new Point(58, 58), Color.FromArgb(8, 10, 14), Color.FromArgb(24, 31, 42));
        graphics.FillPath(brush, path);

        using var logo = LoadLogo();
        if (logo is not null)
        {
            graphics.DrawImage(logo, new Rectangle(12, 11, 40, 40));
        }
        else
        {
            using var fallbackFont = new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fallbackBrush = new SolidBrush(Color.White);
            var fallbackFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString("PY", fallbackFont, fallbackBrush, new RectangleF(6, 8, 52, 46), fallbackFormat);
        }

        if (activeInstall is not null)
        {
            using var badgePath = RoundedRectangle(new RectangleF(25, 45, 34, 15), 6);
            using var badgeBrush = new SolidBrush(Color.FromArgb(238, 9, 12, 17));
            using var badgePen = new Pen(accent, 1.4f);
            graphics.FillPath(badgeBrush, badgePath);
            graphics.DrawPath(badgePen, badgePath);

            using var font = new Font("Segoe UI", label.Length <= 3 ? 9 : 8, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(label, font, textBrush, new RectangleF(25, 44, 34, 16), format);
        }

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        var arc = new RectangleF(bounds.Location, new SizeF(diameter, diameter));

        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Bitmap? LoadLogo()
    {
        try
        {
            var streamInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/pyvswitch-logo.png"));
            if (streamInfo?.Stream is null)
            {
                return null;
            }

            using var stream = streamInfo.Stream;
            using var loaded = new Bitmap(stream);
            return new Bitmap(loaded);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Failed to load Python tray logo asset.");
            return null;
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);
}
