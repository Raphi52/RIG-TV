using System;

namespace Rig.Wpf.Mvvm;

/// <summary>
/// Abstraction du dispatcher UI. Permet aux ViewModels d'être testés sans
/// dépendre de <c>Application.Current.Dispatcher</c>.
/// </summary>
public interface IDispatcherService
{
    void Invoke(Action action);
}

/// <summary>
/// Dispatcher synchrone qui exécute immédiatement sur le thread courant.
/// Utilisé dans les tests unitaires.
/// </summary>
public sealed class SynchronousDispatcher : IDispatcherService
{
    public void Invoke(Action action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        action();
    }
}
