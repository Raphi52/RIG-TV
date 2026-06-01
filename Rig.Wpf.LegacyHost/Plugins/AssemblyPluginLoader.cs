using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Options;
using Rig.Wpf.Core.Abstractions;

namespace Rig.Wpf.LegacyHost.Plugins;

/// <summary>
/// Implémentation réelle de <see cref="IPluginLoader"/> qui scanne le dossier
/// <see cref="PluginLoaderOptions.PluginDirectory"/> à la recherche de PROC_*.dll
/// et résout les types via la convention <see cref="PluginLoaderOptions.TypeNameFormat"/>.
/// Le chargement effectif passe par <c>Assembly.LoadFrom</c> ; tout le reste du code
/// dépend uniquement de <see cref="IPluginLoader"/>, ce qui rend la cohabitation testable.
/// </summary>
public sealed class AssemblyPluginLoader : IPluginLoader
{
    private readonly PluginLoaderOptions _options;

    public AssemblyPluginLoader(IOptions<PluginLoaderOptions> options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _options = options.Value ?? throw new ArgumentException("Options requises.", nameof(options));
    }

    public AssemblyPluginLoader(PluginLoaderOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public IReadOnlyList<LegacyPluginDescriptor> Discover()
    {
        if (!Directory.Exists(_options.PluginDirectory))
            return Array.Empty<LegacyPluginDescriptor>();

        var result = new List<LegacyPluginDescriptor>();
        var pattern = _options.FilePrefix + "*.dll";

        foreach (var dllPath in Directory.EnumerateFiles(_options.PluginDirectory, pattern))
        {
            var code = ExtractCode(dllPath);
            if (code is null) continue;

            try
            {
                var descriptor = LoadDescriptor(code, dllPath);
                result.Add(descriptor);
            }
            catch (PluginLoadException)
            {
                // On ignore les DLL qui ne respectent pas la convention,
                // pour ne pas bloquer la découverte sur un plugin mal formé.
            }
        }

        return result;
    }

    public LegacyPluginDescriptor LoadPlugin(string codeProcessus)
    {
        if (string.IsNullOrWhiteSpace(codeProcessus))
            throw new ArgumentException("Code processus requis.", nameof(codeProcessus));

        var dllPath = Path.Combine(_options.PluginDirectory, $"{_options.FilePrefix}{codeProcessus}.dll");
        if (!File.Exists(dllPath))
            throw new PluginLoadException(
                $"Plugin '{codeProcessus}' introuvable dans '{_options.PluginDirectory}'.");

        return LoadDescriptor(codeProcessus, dllPath);
    }

    private LegacyPluginDescriptor LoadDescriptor(string code, string dllPath)
    {
        Assembly assembly;
        try
        {
            assembly = Assembly.LoadFrom(dllPath);
        }
        catch (Exception ex)
        {
            throw new PluginLoadException(
                $"Impossible de charger l'assembly '{dllPath}'.", ex);
        }

        var typeName = string.Format(_options.TypeNameFormat, code);
        // Recherche case-insensitive : la convention legacy mélange les casings
        // (PROC_Mandataire.dll mais FORM_MANDATAIRE à l'intérieur).
        var type = assembly.GetType(typeName, throwOnError: false, ignoreCase: true);
        if (type is null)
            throw new PluginLoadException(
                $"Type '{typeName}' introuvable dans '{dllPath}'.");

        return new LegacyPluginDescriptor(code, dllPath, type, Libelle: null);
    }

    private string? ExtractCode(string dllPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(dllPath);
        if (string.IsNullOrEmpty(fileName)) return null;
        if (!fileName.StartsWith(_options.FilePrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var code = fileName.Substring(_options.FilePrefix.Length);
        return string.IsNullOrEmpty(code) ? null : code;
    }
}

public sealed class PluginLoadException : Exception
{
    public PluginLoadException(string message) : base(message) { }
    public PluginLoadException(string message, Exception inner) : base(message, inner) { }
}
