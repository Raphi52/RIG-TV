using System;
using System.Collections.Generic;

namespace Rig.Wpf.Core.Abstractions;

/// <summary>
/// Encapsule la découverte et le chargement des plugins legacy PROC_*.dll.
/// Cette abstraction casse la dépendance directe à <c>Assembly.LoadFrom</c>
/// et à la convention <c>RIG.PROCESSUS.FORM_&lt;CODE&gt;</c>.
/// </summary>
public interface IPluginLoader
{
    IReadOnlyList<LegacyPluginDescriptor> Discover();
    LegacyPluginDescriptor LoadPlugin(string codeProcessus);
}

/// <summary>
/// Description d'un plugin legacy résolue sans instancier la Form.
/// </summary>
public sealed record LegacyPluginDescriptor(
    string CodeProcessus,
    string DllPath,
    Type FormType,
    string? Libelle);
