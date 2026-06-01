using Rig.Wpf.Core.Abstractions;

namespace Rig.Wpf.Core.Plugins;

/// <summary>
/// Bridge no-op : utilisé pour les tests et tant que le shell WPF n'est pas en place.
/// </summary>
public sealed class NullFormAccueilBridge : IFormAccueilBridge
{
    public void ChangeTabText(string codeProcessus, string libelle) { }
    public void CloseTab(string codeProcessus) { }
    public bool IsTabOpen(string codeProcessus) => false;
}
