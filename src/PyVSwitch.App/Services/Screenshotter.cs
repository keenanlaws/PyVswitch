using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using PyVSwitch.App.ViewModels;

namespace PyVSwitch.App.Services;

/// <summary>
/// `pyvswitchw --demo --screenshots &lt;folder&gt; [--light]` renders every page to a PNG for the
/// documentation, then exits. The window is laid out off-screen, so nothing flashes on the desktop.
/// </summary>
public static class Screenshotter
{
    private const double Scale = 2;
    private const double Margin = 36;

    public static async Task RunAsync(MainWindow window, MainViewModel viewModel, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var suffix = ThemeService.IsDark ? "dark" : "light";

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = 0;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Show();
            await viewModel.RefreshAsync();

            foreach (var page in new[] { "overview", "versions", "download", "packages", "doctor", "ai", "settings" })
            {
                viewModel.Page = page;
                if (page == "packages")
                {
                    await Task.Delay(700);
                    viewModel.NewPackage = "polars";
                }

                // Let lazy loads, the PyPI lookup and the page transition settle.
                await Task.Delay(page == "download" ? 4500 : 1600);
                Save((FrameworkElement)window.Content, Path.Combine(directory, $"{page}-{suffix}.png"), 9);
                AuditCommands(window, page);
            }

            var popup = new TrayPopupWindow(viewModel) { Left = -20000, Top = 0, ShowActivated = false, AnchorBottom = 700 };
            popup.Show();
            var card = (FrameworkElement)popup.Content;
            // The live drop shadow widens the visual's bounds; the capture draws its own instead.
            card.Effect = null;
            await Task.Delay(700);
            Save(card, Path.Combine(directory, $"tray-{suffix}.png"), 14);
            popup.CloseSafely();
        }
        catch (Exception ex)
        {
            PyVSwitch.Services.AppLog.Error(ex, "Screenshot capture failed.");
            File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString());
        }
        finally
        {
            window.ExitApplication();
        }
    }

    /// <summary>
    /// Logs every button whose Command binding did not resolve. A screenshot cannot show a dead
    /// button, so capture runs double as a wiring check.
    /// </summary>
    private static void AuditCommands(DependencyObject root, string page)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        var checkedCount = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is System.Windows.Controls.Primitives.ButtonBase button)
            {
                var expression = System.Windows.Data.BindingOperations.GetBindingExpression(button, System.Windows.Controls.Primitives.ButtonBase.CommandProperty);
                if (expression is not null)
                {
                    checkedCount++;
                    if (button.Command is null)
                    {
                        PyVSwitch.Services.AppLog.Info($"BINDING dead command on page '{page}': {button.Content ?? button.ToolTip ?? button.Name} ({expression.ParentBinding.Path?.Path}, status {expression.Status})");
                    }
                }
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
            {
                pending.Push(VisualTreeHelper.GetChild(current, i));
            }
        }

        PyVSwitch.Services.AppLog.Info($"BINDING audit page '{page}': {checkedCount} command bindings checked");
    }

    private static void Save(FrameworkElement element, string path, double cornerRadius)
    {
        var width = element.ActualWidth;
        var height = element.ActualHeight;
        var frame = new Rect(Margin, Margin, width, height);
        var root = new ContainerVisual();

        {
            var shadow = new DrawingVisual
            {
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 34, ShadowDepth = 12, Direction = 270, Opacity = ThemeService.IsDark ? 0.55 : 0.28 }
            };
            using (var context = shadow.RenderOpen())
            {
                context.DrawRoundedRectangle(Brushes.Black, null, frame, cornerRadius, cornerRadius);
            }

            root.Children.Add(shadow);
        }

        var content = new DrawingVisual();
        using (var context = content.RenderOpen())
        {
            context.PushClip(new RectangleGeometry(frame, cornerRadius, cornerRadius));
            context.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, frame);
            context.Pop();
            var edge = new Pen(new SolidColorBrush(ThemeService.IsDark ? Color.FromRgb(0x32, 0x3C, 0x50) : Color.FromRgb(0xC2, 0xCA, 0xD8)), 1);
            context.DrawRoundedRectangle(null, edge, frame, cornerRadius, cornerRadius);
        }

        root.Children.Add(content);

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling((width + Margin * 2) * Scale),
            (int)Math.Ceiling((height + Margin * 2) * Scale),
            96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
