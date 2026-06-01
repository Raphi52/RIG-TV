using System;

namespace Rig.Wpf.RigMetier.Dtos;

/// <summary>
/// Métadonnées d'un extrait K-bis stocké dans le cache <c>KbisXml</c>.
/// Utile pour afficher "généré le X" dans l'UI sans recharger le XML/PDF.
/// </summary>
public sealed record KbisDto(
    int IdKbis,
    int IdDossier,
    DateTime DateGeneration,
    bool IsLast);
