using System;
using Rig.Wpf.Mvvm.Processus;

namespace Rig.Wpf.Shell.ViewModels;

/// <summary>
/// Onglet contenant un Processus WPF natif. Le <see cref="ProcessusViewModel"/>
/// est résolu via <see cref="INativeProcessusRegistry"/>.
/// </summary>
public sealed class NativeTabViewModel : TabViewModel
{
    public NativeTabViewModel(ProcessusViewModelBase processusViewModel)
        : base(
            (processusViewModel ?? throw new ArgumentNullException(nameof(processusViewModel))).Code,
            processusViewModel.Libelle ?? processusViewModel.Code)
    {
        ProcessusViewModel = processusViewModel;
    }

    public ProcessusViewModelBase ProcessusViewModel { get; }
}
