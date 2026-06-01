// Source/Wpf/Rig.Wpf.RigMetier/Services/IKbisGenerator.cs
using System.Threading;
using System.Threading.Tasks;

namespace Rig.Wpf.RigMetier.Services;

/// <summary>
/// Génère un PDF K-bis pour un dossier RCS. Implémentation legacy = pipeline
/// XML → XSL → FO → Apache FOP via IKVM.
/// </summary>
public interface IKbisGenerator
{
    /// <summary>
    /// Génère (ou récupère du cache) le PDF K-bis et retourne son chemin local.
    /// </summary>
    /// <param name="idDossier"><c>DOSSIER_RCS.DSSRC_ID_DSRCS</c>.</param>
    /// <param name="codeGreffe">Code greffe (ex "7401"). Détermine la connexion legacy.</param>
    /// <param name="forceRefresh">Si true, invalide le cache <c>KbisXml</c> et régénère.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Chemin absolu du fichier PDF (dans un dossier temp local).</returns>
    Task<string> GeneratePdfAsync(int idDossier, string codeGreffe,
                                   bool forceRefresh = false,
                                   CancellationToken ct = default);
}
