using System.Linq;
using FluentAssertions;
using Rig.Wpf.Shell.ViewModels;
using Xunit;

namespace Rig.Wpf.Shell.Tests.ViewModels;

public class DashboardViewModelTests
{
    [Fact]
    public void NewInstance_HasActionCardsForCoreDomains()
    {
        var sut = new DashboardViewModel(_ => { });

        sut.QuickActions.Select(t => t.Domain)
            .Should().Contain(new[] { "RCS", "JUDICIAIRE" });
    }

    [Fact]
    public void ActionCardCommand_InvokesOpenAction_WithCode()
    {
        string? captured = null;
        var sut = new DashboardViewModel(code => captured = code);
        var rcsCard = sut.QuickActions.First(t => t.Domain == "RCS");

        rcsCard.OpenCommand.Execute(null);

        captured.Should().NotBeNull();
        captured.Should().StartWith("DEMO_RCS");
    }
}
