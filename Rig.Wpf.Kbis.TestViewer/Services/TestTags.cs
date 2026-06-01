namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Taxonomie des tags utilisés pour filtrer les tests par catégorie.
/// Auto-dérivés depuis le backend (xUnit), la catégorie (smoke) ou le filename
/// (scénarios). Pas de tag manuel, pas d'attribut C# à maintenir.
/// </summary>
public static class TestTags
{
    /// <summary>Touche le RIG actuel (WinForms / COM / KbisAmitelApi / RigMetier.Legacy).</summary>
    public const string Legacy = "legacy";

    /// <summary>Touche la nouvelle app WPF (Shell, Processus, ViewModels, smoke renders).</summary>
    public const string Wpf = "wpf";

    /// <summary>Touche la nouvelle API HTTP .NET 8 (Rig.Kbis.Api).</summary>
    public const string Api = "api";

    /// <summary>Vérifie la convergence legacy ↔ nouveau (pair de scénarios sur même Class.Method).</summary>
    public const string Transition = "transition";

    /// <summary>Adapter qui forwarde du legacy vers le format cible (composant transitoire).</summary>
    public const string Bridge = "bridge";

    /// <summary>Bout-en-bout, demande un display — FlaUI / Shell.exe / WebView2.</summary>
    public const string E2E = "e2e";

    /// <summary>Sanity check rapide en isolation, headless, signal binaire ✓/✗.</summary>
    public const string Smoke = "smoke";
}
