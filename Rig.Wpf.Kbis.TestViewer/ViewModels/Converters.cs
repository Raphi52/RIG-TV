using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels;

/// <summary>BrushKey (resource string) → Brush via Application.Resources.</summary>
public sealed class BrushKeyToBrushConverter : IValueConverter
{
    public static readonly BrushKeyToBrushConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string key && Application.Current?.TryFindResource(key) is Brush brush)
            return brush;
        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility (true=Visible, false=Collapsed) — pour les TabControls par module.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>!bool, pour griser un toggle pendant qu'un run est en cours.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : (object)true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : (object)true;
}

/// <summary>int Count → Visibility (>0 = Visible, sinon Collapsed). Pour cacher la liste
/// Steps quand le scenario n'en a pas (cas Rapture).</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public static readonly CountToVisibilityConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is int c && c > 0) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>SmokeOutcome → icône texte (✓ Passed / ✗ Failed / ⊘ Skipped / · Unknown).</summary>
public sealed class OutcomeToIconConverter : IValueConverter
{
    public static readonly OutcomeToIconConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Services.SmokeOutcome o
            ? o switch
            {
                Services.SmokeOutcome.Passed  => "✓",
                Services.SmokeOutcome.Failed  => "✗",
                Services.SmokeOutcome.Skipped => "⊘",
                _                             => "·",
            }
            : "·";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>SmokeOutcome → Brush (Passed vert, Failed rouge, Skipped gris, Unknown noir).</summary>
public sealed class OutcomeToBrushConverter : IValueConverter
{
    public static readonly OutcomeToBrushConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Services.SmokeOutcome o
            ? o switch
            {
                Services.SmokeOutcome.Passed  => new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45)),
                Services.SmokeOutcome.Failed  => new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45)),
                Services.SmokeOutcome.Skipped => new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                _                             => new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
            }
            : (object)Brushes.Black;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Mappe le nom de backend ("legacy" / "native") vers brush bg/fg via Application.Resources.</summary>
public sealed class BackendToBrushConverter : IValueConverter
{
    public static readonly BackendToBrushConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var backend = value as string ?? "";
        var role = parameter as string ?? "bg";
        var key = (backend, role) switch
        {
            ("legacy", "bg") => "BadgeLegacyBg",
            ("legacy", "fg") => "BadgeLegacyFg",
            ("native", "bg") => "BadgeNativeBg",
            ("native", "fg") => "BadgeNativeFg",
            (_,        "bg") => "BadgeNeutralBg",
            (_,        "fg") => "BadgeNeutralFg",
            _                => "BadgeNeutralBg",
        };
        if (Application.Current?.TryFindResource(key) is Brush brush) return brush;
        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
