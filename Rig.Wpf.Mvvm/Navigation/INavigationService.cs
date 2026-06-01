using System;

namespace Rig.Wpf.Mvvm.Navigation;

/// <summary>
/// Navigation entre ViewModels. L'historique permet un GoBack simple.
/// L'implémentation par défaut est en mémoire ; les Views ajoutent des
/// abonnements pour réagir à <see cref="Navigated"/>.
/// </summary>
public interface INavigationService
{
    object? Current { get; }
    bool CanGoBack { get; }

    event EventHandler<NavigatedEventArgs>? Navigated;

    void NavigateTo(object viewModel);
    void GoBack();
}

public sealed class NavigatedEventArgs : EventArgs
{
    public NavigatedEventArgs(object? from, object? to)
    {
        From = from;
        To = to;
    }

    public object? From { get; }
    public object? To { get; }
}
