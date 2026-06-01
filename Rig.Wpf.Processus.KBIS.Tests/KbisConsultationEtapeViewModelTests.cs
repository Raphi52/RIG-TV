using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Rig.Wpf.Processus.Kbis;
using Rig.Wpf.Processus.Kbis.Etapes;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.Repositories;
using Rig.Wpf.RigMetier.Services;
using Xunit;

namespace Rig.Wpf.Processus.Kbis.Tests;

public class KbisConsultationEtapeViewModelTests
{
    private static SocieteDto Sample(int id, string nom) => new(
        id, $"2024B{id:D5}", "123456789", nom, "SARL", null,
        new DateTime(2020,1,1), null, "A");

    private static KbisConsultationEtapeViewModel CreateSut(
        IEnumerable<SocieteDto>? rows = null,
        Mock<IKbisGenerator>? gen = null)
    {
        var repo = new Mock<ISocieteRepository>();
        repo.Setup(r => r.Search(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string f, int n) =>
            {
                var data = rows ?? new[] { Sample(1, "Acme SARL"), Sample(2, "Beta SAS") };
                var filtered = new List<SocieteDto>();
                foreach (var s in data)
                    if (string.IsNullOrEmpty(f) || s.Denomination.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                        filtered.Add(s);
                return filtered;
            });
        repo.Setup(r => r.GetById(It.IsAny<int>()))
            .Returns((int id) =>
            {
                foreach (var s in rows ?? new[] { Sample(1, "Acme SARL"), Sample(2, "Beta SAS") })
                    if (s.IdDossier == id) return s;
                return null;
            });
        var generator = gen ?? new Mock<IKbisGenerator>();
        return new KbisConsultationEtapeViewModel(repo.Object, generator.Object, "7401");
    }

    [Fact]
    public void NewInstance_HasCodeKBIS_AndOneEtape()
    {
        var repo = new Mock<ISocieteRepository>();
        repo.Setup(r => r.Search(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new List<SocieteDto>());
        var gen = new Mock<IKbisGenerator>();
        var sut = new KbisProcessusViewModel(repo.Object, gen.Object, "7401");
        sut.Code.Should().Be("KBIS");
        sut.Etapes.Should().HaveCount(1);
        sut.Etapes[0].Should().BeOfType<KbisConsultationEtapeViewModel>();
    }

    [Fact]
    public void Search_OnFiltreVide_RetourneRows()
    {
        var sut = CreateSut();
        sut.FiltreTexte = "";
        sut.Resultats.Count.Should().Be(2);
    }

    [Fact]
    public void Search_ParPrefixeDenomination_FiltreLesRows()
    {
        var sut = CreateSut();
        sut.FiltreTexte = "Acm";
        sut.Resultats.Count.Should().Be(1);
        sut.Resultats[0].Denomination.Should().Be("Acme SARL");
    }
}
