using System;
using System.IO;
using RIG.SQL;
using RIG.TOOLS;
using RIGKBISXML;

namespace Rig.Wpf.RigMetier.Legacy.Services;

/// <summary>
/// Implémentation POCO de <see cref="RIGKBISXML.IParamKbis"/> que <c>RigKbisXml</c>
/// consomme. Fournit codeGreffe, ids, signature et la connexion legacy.
/// <see cref="GetFileByFilename"/> mappe vers <see cref="TempDir"/> pour stocker
/// les PDFs générés localement (et non sur un partage réseau).
/// </summary>
public sealed class RigKbisParamAdapter : IParamKbis
{
    // CodeGreffe est getter-only dans l'interface ; on expose un setter public
    // pour permettre l'initialisation par l'appelant.
    private string _codeGreffe = "";
    public string CodeGreffe
    {
        get => _codeGreffe;
        set => _codeGreffe = value;
    }

    public int idKbis { get; set; }
    public int idDossier { get; set; }
    public string numDossier { get; set; } = "";
    public string numSiren { get; set; } = "";
    public string Signature { get; set; } = "true"; // sceau+signature officielle par défaut
    public string NomfichierEnCours { get; set; } = "";

    // CnxRead est getter-only dans l'interface ; on expose un setter public
    // pour permettre l'injection par l'appelant.
    private SqlRigConnection _cnxRead = null!;
    /// <summary>Connexion SqlRigConnection legacy ouverte (read-only).</summary>
    public SqlRigConnection CnxRead
    {
        get => _cnxRead;
        set => _cnxRead = value;
    }

    /// <summary>Dossier temp local où les PDFs sont écrits.</summary>
    public string TempDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "rig-wpf-kbis");

    public string GetFileByFilename(string filename)
    {
        Directory.CreateDirectory(TempDir);
        return Path.Combine(TempDir, filename);
    }

    public int LogWrite(eLogLevel errorType, string format, params object[] args)
    {
        // Best-effort : on logue dans la console debug si attaché.
        try { System.Diagnostics.Debug.WriteLine("[Kbis] " + string.Format(format, args)); }
        catch { /* ignoré volontairement */ }
        return 0;
    }
}
