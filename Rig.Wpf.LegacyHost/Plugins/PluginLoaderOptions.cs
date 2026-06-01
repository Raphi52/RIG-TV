namespace Rig.Wpf.LegacyHost.Plugins;

public sealed class PluginLoaderOptions
{
    /// <summary>
    /// Répertoire contenant les PROC_*.dll (par défaut <c>C:\rig\Bin Processus</c>).
    /// </summary>
    public string PluginDirectory { get; set; } = @"C:\rig\Bin Processus";

    /// <summary>
    /// Préfixe attendu (utilisé pour Discover et pour extraire le code).
    /// </summary>
    public string FilePrefix { get; set; } = "PROC_";

    /// <summary>
    /// Format du nom complet du Type à résoudre dans l'assembly chargée.
    /// <c>{0}</c> est remplacé par le code du processus.
    /// </summary>
    public string TypeNameFormat { get; set; } = "RIG.PROCESSUS.FORM_{0}";
}
