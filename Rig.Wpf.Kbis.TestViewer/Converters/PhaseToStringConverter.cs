using System;
using System.Globalization;
using System.Windows.Data;
using Rig.Wpf.Kbis.TestViewer.ViewModels;

namespace Rig.Wpf.Kbis.TestViewer.Converters;

/// <summary>
/// ScenarioPhase → string d'affichage compact pour DataGrid + tiles.
/// </summary>
public sealed class PhaseToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ScenarioPhase.Queued => "Queued",
        ScenarioPhase.Running => "Running",
        ScenarioPhase.Launching => "Launching",
        ScenarioPhase.Login => "Login",
        ScenarioPhase.OpenProcRetaud => "PROC_RETAUD",
        ScenarioPhase.SelectAudience => "Audience",
        ScenarioPhase.ClickImporter => "Click Import",
        ScenarioPhase.Recap => "Recap",
        ScenarioPhase.Apply => "Apply",
        ScenarioPhase.Done => "DONE",
        ScenarioPhase.Fail => "FAIL",
        _ => "?",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
