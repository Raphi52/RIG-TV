using FluentAssertions;
using Rig.Wpf.Core.Abstractions;
using Rig.Wpf.Core.Session;
using Rig.Wpf.Shell.ViewModels;
using Xunit;

namespace Rig.Wpf.Shell.Tests.ViewModels;

public class LoginViewModelTests
{
    [Fact]
    public void NewInstance_LoginCommand_CannotExecute()
    {
        var sut = new LoginViewModel(new InMemorySessionContext());

        sut.LoginCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void LoginCommand_CanExecute_WhenUserAndGreffeFilled()
    {
        var sut = new LoginViewModel(new InMemorySessionContext())
        {
            CodeUtilisateur = "USR1",
            CodeGreffe = "G001"
        };

        sut.LoginCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void LoginCommand_Execute_SignsInOnSession()
    {
        ISessionContext session = new InMemorySessionContext();
        var sut = new LoginViewModel(session)
        {
            CodeUtilisateur = "USR1",
            CodeGreffe = "G001"
        };

        sut.LoginCommand.Execute(null);

        session.IsAuthenticated.Should().BeTrue();
        session.CodeUtilisateur.Should().Be("USR1");
        session.CodeGreffe.Should().Be("G001");
    }

    [Fact]
    public void SettingFields_NotifiesCanExecuteChanged()
    {
        var sut = new LoginViewModel(new InMemorySessionContext());
        var notified = 0;
        sut.LoginCommand.CanExecuteChanged += (_, _) => notified++;

        sut.CodeUtilisateur = "USR1";
        sut.CodeGreffe = "G001";

        notified.Should().BeGreaterThan(0);
    }
}
