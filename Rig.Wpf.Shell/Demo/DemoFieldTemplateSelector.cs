using System.Windows;
using System.Windows.Controls;

namespace Rig.Wpf.Shell.Demo;

/// <summary>
/// Sélectionne le DataTemplate à appliquer pour rendre un <see cref="DemoField"/>
/// en fonction de son <see cref="DemoField.Kind"/>.
/// </summary>
public sealed class DemoFieldTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? NumberTemplate { get; set; }
    public DataTemplate? MoneyTemplate { get; set; }
    public DataTemplate? ComboTemplate { get; set; }
    public DataTemplate? DateTemplate { get; set; }
    public DataTemplate? CheckTemplate { get; set; }
    public DataTemplate? MemoTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not DemoField field) return base.SelectTemplate(item, container);
        return field.Kind switch
        {
            DemoFieldKind.Number => NumberTemplate,
            DemoFieldKind.Money => MoneyTemplate,
            DemoFieldKind.Combo => ComboTemplate,
            DemoFieldKind.Date => DateTemplate,
            DemoFieldKind.Check => CheckTemplate,
            DemoFieldKind.Memo => MemoTemplate,
            _ => TextTemplate,
        };
    }
}
