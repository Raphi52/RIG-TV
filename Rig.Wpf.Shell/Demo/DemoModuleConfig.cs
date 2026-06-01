using System.Collections.Generic;

namespace Rig.Wpf.Shell.Demo;

/// <summary>
/// Configuration d'un module de démonstration : titre, icône, colonnes de liste,
/// lignes factices, champs du formulaire de saisie. Sert à matérialiser la
/// vision d'agencement final RIG sans brancher la base.
/// </summary>
public sealed record DemoModuleConfig(
    string Code,
    string Domain,
    string Title,
    string Subtitle,
    string IconKey,
    string Eyebrow,
    IReadOnlyList<DemoColumn> ListColumns,
    IReadOnlyList<DemoRow> SampleRows,
    IReadOnlyList<DemoField> SaisieFields,
    IReadOnlyList<string> ActionsBarre,
    string SaveLabel = "Enregistrer");

public sealed record DemoColumn(string Header, string PropertyKey, double Width = 160);

public sealed class DemoRow
{
    public Dictionary<string, object?> Cells { get; } = new();
    public string StatusKind { get; init; } = "";
    public string? StatusLabel { get; init; }
}

public sealed record DemoField(
    string Label,
    DemoFieldKind Kind,
    string? Placeholder = null,
    IReadOnlyList<string>? Options = null,
    int ColumnSpan = 1,
    string? HelperText = null);

public enum DemoFieldKind
{
    Text,
    Combo,
    Check,
    Date,
    Money,
    Memo,
    Number,
}
