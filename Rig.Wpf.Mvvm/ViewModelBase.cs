using CommunityToolkit.Mvvm.ComponentModel;

namespace Rig.Wpf.Mvvm;

/// <summary>
/// Base de tous les ViewModels WPF de RIG. Hérite de <see cref="ObservableObject"/>
/// (CommunityToolkit.Mvvm) pour fournir <c>SetProperty</c>, <c>OnPropertyChanged</c>
/// et l'intégration avec les source generators de la toolkit.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
}
