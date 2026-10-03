using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PyVSwitch.App.Controls;

/// <summary>Attached properties used by the control templates in Themes/Styles.xaml.</summary>
public static class Ui
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(Ui), new PropertyMetadata(null));

    public static string? GetIcon(DependencyObject element) => (string?)element.GetValue(IconProperty);

    public static void SetIcon(DependencyObject element, string? value) => element.SetValue(IconProperty, value);

    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(Ui), new PropertyMetadata(""));

    public static string GetPlaceholder(DependencyObject element) => (string)element.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject element, string value) => element.SetValue(PlaceholderProperty, value);

    /// <summary>Opens the button's ContextMenu on an ordinary left click.</summary>
    public static readonly DependencyProperty MenuOnClickProperty =
        DependencyProperty.RegisterAttached("MenuOnClick", typeof(bool), typeof(Ui), new PropertyMetadata(false, OnMenuOnClickChanged));

    public static bool GetMenuOnClick(DependencyObject element) => (bool)element.GetValue(MenuOnClickProperty);

    public static void SetMenuOnClick(DependencyObject element, bool value) => element.SetValue(MenuOnClickProperty, value);

    private static void OnMenuOnClickChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is Button button && e.NewValue is true)
        {
            button.Click += (_, _) =>
            {
                if (button.ContextMenu is { } menu)
                {
                    menu.PlacementTarget = button;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    // Right-align the menu with the button (the menu template is at least 210 wide plus a 10px shadow margin each side).
                    menu.HorizontalOffset = button.ActualWidth - 220;
                    menu.IsOpen = true;
                }
            };
        }
    }
}

/// <summary>A small status label. Tone: neutral, accent, success, warning, danger or brand.</summary>
public class Pill : ContentControl
{
    public static readonly DependencyProperty ToneProperty =
        DependencyProperty.Register(nameof(Tone), typeof(string), typeof(Pill), new PropertyMetadata("neutral"));

    public string Tone
    {
        get => (string)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }
}

/// <summary>
/// Maps a value to Visibility. Parameter picks the rule: true (default), false, null, notnull,
/// empty, notempty, zero, nonzero, =text or !=text.
/// </summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var rule = parameter as string ?? "true";
        if (rule.StartsWith('=') || rule.StartsWith("!=", StringComparison.Ordinal))
        {
            var negate = rule[0] == '!';
            var matches = string.Equals(value?.ToString(), rule[(negate ? 2 : 1)..], StringComparison.OrdinalIgnoreCase);
            return matches != negate ? Visibility.Visible : Visibility.Collapsed;
        }

        var visible = rule switch
        {
            "false" => value is false,
            "null" => value is null,
            "notnull" => value is not null,
            "empty" => string.IsNullOrEmpty(value as string),
            "notempty" => !string.IsNullOrEmpty(value as string),
            "zero" => value is 0,
            "nonzero" => value is int number && number != 0,
            _ => value is true
        };
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when the bound value's text equals the parameter. Lets RadioButtons bind to one property.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null)
        {
            return Binding.DoNothing;
        }

        var text = parameter.ToString()!;
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return type.IsEnum ? Enum.Parse(type, text, true) : text;
    }
}

/// <summary>Segoe Fluent Icons code points.</summary>
public static class Glyphs
{
    public const string Home = "";
    public const string Versions = "";
    public const string Download = "";
    public const string Package = "";
    public const string Health = "";
    public const string Robot = "";
    public const string Settings = "";
    public const string Refresh = "";
    public const string Check = "";
    public const string Info = "";
    public const string Warning = "";
    public const string Error = "";
    public const string Close = "";
    public const string Terminal = "";
    public const string Folder = "";
    public const string Copy = "";
    public const string Delete = "";
    public const string More = "";
    public const string Add = "";
    public const string Search = "";
    public const string Up = "";
    public const string Switch = "";
    public const string Undo = "";
    public const string Link = "";
    public const string Play = "";
}
