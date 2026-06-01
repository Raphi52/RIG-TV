namespace Rig.Wpf.RigMetier.Dtos;

public sealed record GreffeDto(
    string Code,
    string Libelle,
    string? VilleSiege,
    bool EstActif);
