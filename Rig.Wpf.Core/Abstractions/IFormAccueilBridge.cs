namespace Rig.Wpf.Core.Abstractions;

/// <summary>
/// Pont entre les plugins legacy (qui appellent <c>FormAccueil.FrmAccueil.ChangeTabText</c>
/// etc.) et le shell WPF qui héberge ces plugins via WindowsFormsHost.
/// </summary>
public interface IFormAccueilBridge
{
    void ChangeTabText(string codeProcessus, string libelle);
    void CloseTab(string codeProcessus);
    bool IsTabOpen(string codeProcessus);
}
