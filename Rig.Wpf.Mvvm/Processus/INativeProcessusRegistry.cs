using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Rig.Wpf.Mvvm.Processus;

/// <summary>
/// Connaît la liste des Processus disponibles en version WPF native et sait
/// instancier leur ViewModel via le conteneur DI.
/// </summary>
public interface INativeProcessusRegistry
{
    bool IsNative(string codeProcessus);
    ProcessusViewModelBase CreateProcessus(string codeProcessus);
}

public sealed class NativeProcessusRegistry : INativeProcessusRegistry
{
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<string, Type> _byCode =
        new(StringComparer.OrdinalIgnoreCase);

    public NativeProcessusRegistry(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public void Register(string codeProcessus, Type viewModelType)
    {
        if (string.IsNullOrWhiteSpace(codeProcessus))
            throw new ArgumentException("Code requis.", nameof(codeProcessus));
        if (viewModelType is null) throw new ArgumentNullException(nameof(viewModelType));
        if (!typeof(ProcessusViewModelBase).IsAssignableFrom(viewModelType))
            throw new ArgumentException(
                $"Le type '{viewModelType.FullName}' doit hériter de ProcessusViewModelBase.",
                nameof(viewModelType));

        _byCode[codeProcessus] = viewModelType;
    }

    public bool IsNative(string codeProcessus)
        => !string.IsNullOrWhiteSpace(codeProcessus) && _byCode.ContainsKey(codeProcessus);

    public ProcessusViewModelBase CreateProcessus(string codeProcessus)
    {
        if (!_byCode.TryGetValue(codeProcessus, out var type))
            throw new InvalidOperationException(
                $"Aucun Processus natif enregistré pour le code '{codeProcessus}'.");

        var instance = _services.GetService(type)
            ?? ActivatorUtilities.CreateInstance(_services, type);
        return (ProcessusViewModelBase)instance;
    }
}
