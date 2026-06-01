using FluentAssertions;
using Rig.Wpf.Processus.Ipe.Etapes;
using Xunit;

namespace Rig.Wpf.Processus.Ipe.Tests.Etapes;

public class IpeFactureEtapeViewModelTests
{
    [Fact]
    public void NewInstance_IsInvalid()
    {
        new IpeFactureEtapeViewModel().IsValid.Should().BeFalse();
    }

    [Fact]
    public void IsValid_True_WhenMontantPositive()
    {
        var sut = new IpeFactureEtapeViewModel { Montant = 250m };
        sut.IsValid.Should().BeTrue();
    }

    [Fact]
    public void IsValid_False_WhenMontantZero()
    {
        new IpeFactureEtapeViewModel { Montant = 0m }.IsValid.Should().BeFalse();
    }

    [Fact]
    public void IsValid_False_WhenMontantNegative()
    {
        new IpeFactureEtapeViewModel { Montant = -10m }.IsValid.Should().BeFalse();
    }
}
