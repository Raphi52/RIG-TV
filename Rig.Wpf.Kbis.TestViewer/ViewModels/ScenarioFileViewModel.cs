using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Rig.Wpf.Kbis.TestViewer.Services;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels;

/// <summary>
/// Wrap un <see cref="ScenarioFileInfo"/> (un fichier .verified.json) pour binding XAML.
/// Côté UI ces fichiers sont présentés comme « scénarios » — c'est ce qu'ils capturent
/// fonctionnellement (la réponse attendue d'un scénario xUnit).
/// </summary>
public sealed partial class ScenarioFileViewModel : ObservableObject
{
    public ScenarioFileViewModel(ScenarioFileInfo info)
    {
        Info = info;
        Tags = new List<string>(DeriveInitialTags(info));
    }

    public ScenarioFileInfo Info { get; }
    public string FileName   => Info.FileName;
    public string PrettyName => Info.PrettyName;
    public string SizeLabel  => Info.SizeLabel;

    /// <summary>Tags du scénario (mutable — <c>transition</c> ajouté par MainVM si paire détectée).</summary>
    public List<string> Tags { get; }

    private static IEnumerable<string> DeriveInitialTags(ScenarioFileInfo info)
    {
        if (string.Equals(info.Metadata.Backend, "legacy", StringComparison.OrdinalIgnoreCase))
            yield return TestTags.Legacy;
        else if (string.Equals(info.Metadata.Backend, "native", StringComparison.OrdinalIgnoreCase))
            yield return TestTags.Api;
    }

    public void EnsureTag(string tag)
    {
        if (!Tags.Contains(tag, StringComparer.Ordinal)) Tags.Add(tag);
    }
}
