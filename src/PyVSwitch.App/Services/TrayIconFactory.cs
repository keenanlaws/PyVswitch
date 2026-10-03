using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using PyVSwitch.Models;
using PyVSwitch.Services;

namespace PyVSwitch.App.Services;

/// <summary>Draws the tray icon: the app logo with the active version as a small badge.</summary>
public static partial class TrayIconFactory
{
    public static Icon Create(PythonInstall? activeInstall)
    {
        var label = string.IsNullOrWhiteSpace(activeInstall?.Series) ? "" : activeInstall.Series;

        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);

        using var logo = LoadLogo();
        if (logo is not null)
        {
            graphics.DrawImage(logo, new Rectangle(2, 0, 60, 60));
        }

        if (label.Length > 0)
        {
            var accent = activeInstall?.Architecture == "x86" ? Color.FromArgb(255, 212, 59) : Color.FromArgb(76, 155, 245);
            using var badgePath = RoundedRectangle(new RectangleF(20, 38, 44, 26), 9);
            using var badgeBrush = new SolidBrush(Color.FromArgb(245, 11, 13, 18));
            using var badgePen = new Pen(accent, 2.4f);
            graphics.FillPath(badgeBrush, badgePath);
            graphics.DrawPath(badgePen, badgePath);

            using var font = new Font("Segoe UI", label.Length <= 3 ? 20 : 17, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(label, font, textBrush, new RectangleF(20, 38, 44, 26), format);
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
            AppLog.Error(ex, "Failed to load the tray logo asset.");
            return null;
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);
}
