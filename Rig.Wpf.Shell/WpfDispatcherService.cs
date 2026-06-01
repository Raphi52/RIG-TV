using System;
using System.Windows.Threading;
using Rig.Wpf.Mvvm;

namespace Rig.Wpf.Shell;

public sealed class WpfDispatcherService : IDispatcherService
{
    private readonly Dispatcher _dispatcher;

    public WpfDispatcherService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Invoke(Action action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.Invoke(action);
    }
}
