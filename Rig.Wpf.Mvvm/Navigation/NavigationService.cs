using System;
using System.Collections.Generic;

namespace Rig.Wpf.Mvvm.Navigation;

public sealed class NavigationService : INavigationService
{
    private readonly Stack<object> _backStack = new();

    public object? Current { get; private set; }

    public bool CanGoBack => _backStack.Count > 0;

    public event EventHandler<NavigatedEventArgs>? Navigated;

    public void NavigateTo(object viewModel)
    {
        if (viewModel is null) throw new ArgumentNullException(nameof(viewModel));

        var previous = Current;
        if (previous is not null) _backStack.Push(previous);
        Current = viewModel;
        Navigated?.Invoke(this, new NavigatedEventArgs(previous, viewModel));
    }

    public void GoBack()
    {
        if (!CanGoBack) return;
        var previous = Current;
        Current = _backStack.Pop();
        Navigated?.Invoke(this, new NavigatedEventArgs(previous, Current));
    }
}
