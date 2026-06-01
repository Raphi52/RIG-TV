using FluentAssertions;
using Rig.Wpf.Processus.Testnlh.Etapes;
using Xunit;

namespace Rig.Wpf.Processus.Testnlh.Tests.Etapes;

public class TestnlhEtape1ViewModelTests
{
    [Fact]
    public void NewInstance_HasEmptyMessage_AndIsInvalid()
    {
        var sut = new TestnlhEtape1ViewModel();

        sut.Message.Should().BeNullOrEmpty();
        sut.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Message_WhenSet_BecomesValid()
    {
        var sut = new TestnlhEtape1ViewModel();

        sut.Message = "Bonjour";

        sut.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Message_WhenClearedBack_BecomesInvalidAgain()
    {
        var sut = new TestnlhEtape1ViewModel { Message = "X" };
        sut.IsValid.Should().BeTrue();

        sut.Message = "   ";

        sut.IsValid.Should().BeFalse();
    }

    [Fact]
    public void SettingMessage_RaisesIsValidChanged_OnTransition()
    {
        var sut = new TestnlhEtape1ViewModel();
        var raised = 0;
        sut.IsValidChanged += (_, _) => raised++;

        sut.Message = "X";
        sut.Message = "Y";        // toujours valide → pas de transition
        sut.Message = "";         // transition vers invalide

        raised.Should().Be(2);
    }

    [Fact]
    public void Code_AndLibelle_AreStable()
    {
        var sut = new TestnlhEtape1ViewModel();

        sut.Code.Should().Be("TESTNLH_E1");
        sut.Libelle.Should().NotBeNullOrEmpty();
    }
}
