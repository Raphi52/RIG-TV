using Rig.Wpf.Core.Abstractions;

namespace Rig.Wpf.Shell.ViewModels;

/// <summary>
/// Représente un onglet hébergeant un plugin legacy via WindowsFormsHost.
/// L'instance Form WinForms est créée par la View (LegacyPluginHostControl)
/// au moment de l'attachement, à partir du <see cref="Descriptor"/>.
/// </summary>
public sealed class LegacyPluginTabViewModel : TabViewModel
{
    public LegacyPluginTabViewModel(LegacyPluginDescriptor descriptor)
        : base(descriptor.CodeProcessus, descriptor.Libelle ?? descriptor.CodeProcessus)
    {
        Descriptor = descriptor;
    }

    public LegacyPluginDescriptor Descriptor { get; }
}
