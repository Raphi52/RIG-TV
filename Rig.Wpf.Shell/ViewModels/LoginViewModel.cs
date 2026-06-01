using System;
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Core.Abstractions;
using Rig.Wpf.Mvvm;

namespace Rig.Wpf.Shell.ViewModels;

public sealed class LoginViewModel : ViewModelBase
{
    private readonly ISessionContext _session;
    private string? _codeUtilisateur;
    private string? _codeGreffe;

    public LoginViewModel(ISessionContext session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        LoginCommand = new RelayCommand(Login, CanLogin);
    }

    public string? CodeUtilisateur
    {
        get => _codeUtilisateur;
        set
        {
            if (SetProperty(ref _codeUtilisateur, value))
                LoginCommand.NotifyCanExecuteChanged();
        }
    }

    public string? CodeGreffe
    {
        get => _codeGreffe;
        set
        {
            if (SetProperty(ref _codeGreffe, value))
                LoginCommand.NotifyCanExecuteChanged();
        }
    }

    public RelayCommand LoginCommand { get; }

    private bool CanLogin()
        => !string.IsNullOrWhiteSpace(_codeUtilisateur)
        && !string.IsNullOrWhiteSpace(_codeGreffe);

    private void Login()
    {
        _session.SignIn(_codeUtilisateur!, _codeGreffe!);
    }
}
