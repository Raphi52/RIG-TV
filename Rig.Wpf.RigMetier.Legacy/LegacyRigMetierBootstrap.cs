using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RIG.SQL;

namespace Rig.Wpf.RigMetier.Legacy;

/// <summary>
/// Initialise le runtime RigMetier legacy : ajoute <c>C:\rig\BinC</c> au DLL search
/// path (RigMetier dépend de <c>RIG_BinC_Common.dll</c> via P-invoke), puis appelle
/// <c>RIG.Common.Init(codeGreffe)</c>. Doit être appelé une seule fois, AVANT toute
/// utilisation des repositories legacy.
/// </summary>
public sealed class LegacyRigMetierBootstrap
{
    public const string DefaultBinCDirectory = @"C:\rig\BinC";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string? lpPathName);

    private readonly ILogger _logger;
    private bool _initialized;

    public LegacyRigMetierBootstrap(ILogger<LegacyRigMetierBootstrap>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger<LegacyRigMetierBootstrap>.Instance;
    }

    public string? CodeGreffe { get; private set; }

    /// <summary>
    /// Configure le runtime legacy pour le greffe demandé. Idempotent : sans effet
    /// si déjà initialisé pour le même code.
    /// </summary>
    /// <returns><c>true</c> si l'initialisation a réussi (legacy chargeable),
    /// <c>false</c> si le runtime native n'est pas accessible.</returns>
    public bool Initialize(string codeGreffe, string? binCDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(codeGreffe))
            throw new ArgumentException("Code greffe requis.", nameof(codeGreffe));

        if (_initialized && string.Equals(CodeGreffe, codeGreffe, StringComparison.OrdinalIgnoreCase))
            return true;

        var dir = binCDirectory ?? DefaultBinCDirectory;
        if (!Directory.Exists(dir))
        {
            _logger.LogWarning("Dossier {Dir} introuvable — le runtime legacy ne sera pas initialisé.", dir);
            return false;
        }
        if (!SetDllDirectory(dir))
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogError("SetDllDirectory({Dir}) a échoué (Win32 err {Err}).", dir, err);
            return false;
        }

        try
        {
            CallCommonInit(codeGreffe);
            CodeGreffe = codeGreffe;
            _initialized = true;
            _logger.LogInformation("RigMetier legacy initialisé pour le greffe {Code}.", codeGreffe);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Échec Common.Init({Code}).", codeGreffe);
            return false;
        }
    }

    /// <summary>
    /// Retourne une connexion SQL read-only vers le greffe demandé.
    /// Le runtime legacy doit avoir été initialisé via <see cref="Initialize"/> au préalable.
    /// </summary>
    public SqlRigConnection GetReadConnection(string codeGreffe)
    {
        if (string.IsNullOrWhiteSpace(codeGreffe))
            codeGreffe = CodeGreffe ?? "";
        if (string.IsNullOrWhiteSpace(codeGreffe))
            throw new InvalidOperationException(
                "Aucun greffe initialisé. Appelez Initialize() d'abord.");
        return new SqlRigConnectionReadOnly(codeGreffe);
    }

    // L'appel à RIG.Common.Init est isolé pour éviter le JIT eager de
    // l'assembly legacy si Initialize() n'est pas appelé.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void CallCommonInit(string codeGreffe)
        => RIG.Common.Init(codeGreffe);
}
