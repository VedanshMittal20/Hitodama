using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Companion.Shell.Resources;

[ValueConversion(typeof(bool), typeof(string))]
public sealed class RoutineStatusTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Active" : "Paused";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(bool), typeof(string))]
public sealed class RoutineToggleTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Pause" : "Enable";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(bool), typeof(Brush))]
public sealed class RoutineStatusBgConverter : IValueConverter
{
    private static readonly Brush ActiveBg = new SolidColorBrush(Color.FromArgb(0x20, 0x34, 0xD3, 0x99));
    private static readonly Brush PausedBg = new SolidColorBrush(Color.FromArgb(0x18, 0x64, 0x74, 0x8B));

    static RoutineStatusBgConverter()
    {
        ActiveBg.Freeze();
        PausedBg.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ActiveBg : PausedBg;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(bool), typeof(Brush))]
public sealed class RoutineStatusBorderConverter : IValueConverter
{
    private static readonly Brush ActiveBorder = new SolidColorBrush(Color.FromArgb(0x44, 0x34, 0xD3, 0x99));
    private static readonly Brush PausedBorder = new SolidColorBrush(Color.FromArgb(0x30, 0x64, 0x74, 0x8B));

    static RoutineStatusBorderConverter()
    {
        ActiveBorder.Freeze();
        PausedBorder.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ActiveBorder : PausedBorder;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(bool), typeof(Brush))]
public sealed class RoutineStatusFgConverter : IValueConverter
{
    private static readonly Brush ActiveFg = new SolidColorBrush(Color.FromArgb(0xFF, 0x34, 0xD3, 0x99));
    private static readonly Brush PausedFg = new SolidColorBrush(Color.FromArgb(0xFF, 0x94, 0xA3, 0xB8));

    static RoutineStatusFgConverter()
    {
        ActiveFg.Freeze();
        PausedFg.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ActiveFg : PausedFg;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(string), typeof(string))]
public sealed class ClipTypeTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString()?.Contains("image", StringComparison.OrdinalIgnoreCase) == true ? "PNG" : "TEXT";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(string), typeof(Brush))]
public sealed class ClipTypeBgConverter : IValueConverter
{
    private static readonly Brush ImageBg = new SolidColorBrush(Color.FromArgb(0x22, 0x81, 0x8C, 0xF8));
    private static readonly Brush TextBg = new SolidColorBrush(Color.FromArgb(0x20, 0x38, 0xBD, 0xF8));

    static ClipTypeBgConverter()
    {
        ImageBg.Freeze();
        TextBg.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString()?.Contains("image", StringComparison.OrdinalIgnoreCase) == true ? ImageBg : TextBg;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(string), typeof(Brush))]
public sealed class ClipTypeBorderConverter : IValueConverter
{
    private static readonly Brush ImageBorder = new SolidColorBrush(Color.FromArgb(0x44, 0x81, 0x8C, 0xF8));
    private static readonly Brush TextBorder = new SolidColorBrush(Color.FromArgb(0x40, 0x38, 0xBD, 0xF8));

    static ClipTypeBorderConverter()
    {
        ImageBorder.Freeze();
        TextBorder.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString()?.Contains("image", StringComparison.OrdinalIgnoreCase) == true ? ImageBorder : TextBorder;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(string), typeof(Brush))]
public sealed class ClipTypeFgConverter : IValueConverter
{
    private static readonly Brush ImageFg = new SolidColorBrush(Color.FromArgb(0xFF, 0x81, 0x8C, 0xF8));
    private static readonly Brush TextFg = new SolidColorBrush(Color.FromArgb(0xFF, 0x38, 0xBD, 0xF8));

    static ClipTypeFgConverter()
    {
        ImageFg.Freeze();
        TextFg.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString()?.Contains("image", StringComparison.OrdinalIgnoreCase) == true ? ImageFg : TextFg;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(bool), typeof(string))]
public sealed class ClipboardPinTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "📌" : "Pin";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(bool), typeof(Brush))]
public sealed class ClipboardPinBorderConverter : IValueConverter
{
    private static readonly Brush PinnedBorder = CreateFrozenBrush(Color.FromArgb(0x88, 0x38, 0xBD, 0xF8));
    private static readonly Brush UnpinnedBorder = CreateFrozenBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? PinnedBorder : UnpinnedBorder;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class ClipIsUrlConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class ClipIsJsonConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s)
        {
            string trimmed = s.Trim();
            if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) || (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
            {
                return Visibility.Visible;
            }
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class ClipIsHexColorConverter : IValueConverter
{
    private static readonly System.Text.RegularExpressions.Regex HexRegex =
        new(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && HexRegex.IsMatch(s.Trim()) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(string), typeof(Brush))]
public sealed class ClipHexBrushConverter : IValueConverter
{
    private static readonly System.Text.RegularExpressions.Regex HexRegex =
        new(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && HexRegex.IsMatch(s.Trim()))
        {
            try
            {
                var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(s.Trim());
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine($"Invalid color binding value ({exception.GetType().Name}); using transparent fallback.");
            }
        }

        return System.Windows.Media.Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}


