using System;
using System.Collections.Generic;
using Rig.Wpf.Core.Abstractions;

namespace Rig.Wpf.Core.Plugins;

/// <summary>
/// Implémentation neutre de <see cref="IPluginLoader"/> pour les contextes
/// où aucun chargement réel ne doit avoir lieu (tests rapides, démarrage sans Bin Processus).
/// </summary>
public sealed class NullPluginLoader : IPluginLoader
{
    public IReadOnlyList<LegacyPluginDescriptor> Discover() => Array.Empty<LegacyPluginDescriptor>();

    public LegacyPluginDescriptor LoadPlugin(string codeProcessus)
        => throw new InvalidOperationException(
            $"Aucun plugin loader configuré ; impossible de charger '{codeProcessus}'.");
}
