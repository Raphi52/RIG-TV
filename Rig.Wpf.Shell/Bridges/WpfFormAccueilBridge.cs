using System;
using Rig.Wpf.Core.Abstractions;
using Rig.Wpf.Mvvm;
using Rig.Wpf.Shell.ViewModels;

namespace Rig.Wpf.Shell.Bridges;

/// <summary>
/// Implémentation WPF de <see cref="IFormAccueilBridge"/> : les plugins legacy
/// (qui appellent ce bridge depuis leur thread WinForms) marshallent leurs appels
/// vers le thread UI WPF via <see cref="IDispatcherService"/>.
/// </summary>
public sealed class WpfFormAccueilBridge : IFormAccueilBridge
{
    private readonly ShellViewModel _shell;
    private readonly IDispatcherService _dispatcher;

    public WpfFormAccueilBridge(ShellViewModel shell, IDispatcherService dispatcher)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void ChangeTabText(string codeProcessus, string libelle)
    {
        _dispatcher.Invoke(() => _shell.UpdateTabCaption(codeProcessus, libelle));
    }

    public void CloseTab(string codeProcessus)
    {
        _dispatcher.Invoke(() => _shell.CloseTabCommand.Execute(codeProcessus));
    }

    public bool IsTabOpen(string codeProcessus)
        => _shell.IsTabOpen(codeProcessus);
}
