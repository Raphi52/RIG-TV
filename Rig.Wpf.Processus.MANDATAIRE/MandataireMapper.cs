using System;
using Rig.Wpf.Processus.Mandataire.Etapes;
using Rig.Wpf.RigMetier.Dtos;

namespace Rig.Wpf.Processus.Mandataire;

/// <summary>
/// Mapping entre <see cref="MandataireDto"/> (couche données) et
/// <see cref="MandataireSaisieEtapeViewModel"/> (couche présentation).
/// Pas d'héritage de <c>DbObjectMapper</c> ici parce qu'on a besoin d'un
/// <c>SaveAsNew</c> qui crée le DTO (les records sont immutables sur init).
/// </summary>
public sealed class MandataireMapper
{
    public void Load(MandataireDto source, MandataireSaisieEtapeViewModel target)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (target is null) throw new ArgumentNullException(nameof(target));
        target.Civilite = source.Civilite;
        target.Nom = source.Nom;
        target.Abrege = source.Abrege;
        target.NumeroCnbf = source.NumeroCnbf;
        target.EstActif = source.EstActif;
    }

    public void Save(MandataireSaisieEtapeViewModel source, ref MandataireDto target)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        target = target with
        {
            Civilite = source.Civilite ?? "",
            Nom = source.Nom ?? "",
            Abrege = source.Abrege,
            NumeroCnbf = source.NumeroCnbf,
            EstActif = source.EstActif
        };
    }

    public MandataireDto SaveAsNew(MandataireSaisieEtapeViewModel source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        return new MandataireDto(
            Id: 0,
            Civilite: source.Civilite ?? "",
            Nom: source.Nom ?? "",
            Abrege: source.Abrege,
            NumeroCnbf: source.NumeroCnbf,
            EstActif: source.EstActif);
    }
}
