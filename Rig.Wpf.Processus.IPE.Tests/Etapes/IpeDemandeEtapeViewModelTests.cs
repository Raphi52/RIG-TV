using System;
using FluentAssertions;
using Rig.Wpf.Processus.Ipe.Etapes;
using Xunit;

namespace Rig.Wpf.Processus.Ipe.Tests.Etapes;

public class IpeDemandeEtapeViewModelTests
{
    [Fact]
    public void NewInstance_IsInvalid()
    {
        new IpeDemandeEtapeViewModel().IsValid.Should().BeFalse();
    }

    [Fact]
    public void IsValid_True_WhenTypeAndDateProvided()
    {
        var sut = new IpeDemandeEtapeViewModel
        {
            TypeIp = TypeIp.Standard,
            DateSaisineGreffe = new DateTime(2024, 6, 15)
        };
        sut.IsValid.Should().BeTrue();
    }

    [Fact]
    public void TypesIpDisponibles_ContainsStandardAndDigital()
    {
        var sut = new IpeDemandeEtapeViewModel();
        sut.TypesIpDisponibles.Should().Contain(new[] { TypeIp.Standard, TypeIp.TribunalDigital });
    }

    [Fact]
    public void SettingDateThenType_RaisesIsValidChanged_OnTransition()
    {
        var sut = new IpeDemandeEtapeViewModel();
        var raised = 0;
        sut.IsValidChanged += (_, _) => raised++;

        sut.DateSaisineGreffe = new DateTime(2024, 6, 15);
        sut.TypeIp = TypeIp.Standard;

        raised.Should().Be(1);
    }
}
