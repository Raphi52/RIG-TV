using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

namespace Rig.Wpf.TestHelpers;

/// <summary>
/// Génère sur disque des assemblies factices imitant la convention legacy
/// <c>PROC_&lt;CODE&gt;.dll</c> contenant un type <c>RIG.PROCESSUS.FORM_&lt;CODE&gt;</c>.
/// Utile pour tester <c>IPluginLoader</c> sans dépendre du déploiement RIG complet.
/// </summary>
public static class StubPluginFactory
{
    /// <summary>
    /// Crée une PROC_&lt;codeProcessus&gt;.dll dans <paramref name="directory"/>
    /// et retourne son chemin absolu.
    /// </summary>
    public static string CreatePluginAssembly(string directory, string codeProcessus)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Répertoire requis.", nameof(directory));
        if (string.IsNullOrWhiteSpace(codeProcessus))
            throw new ArgumentException("Code processus requis.", nameof(codeProcessus));

        Directory.CreateDirectory(directory);

        var dllName = $"PROC_{codeProcessus}.dll";
        var asmName = new AssemblyName($"PROC_{codeProcessus}")
        {
            Version = new Version(1, 0, 0, 0)
        };

        // RunAndSave n'existe que sur .NET Framework — c'est pour ça qu'on cible net48.
        var asmBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(
            asmName,
            AssemblyBuilderAccess.RunAndSave,
            directory);

        var moduleBuilder = asmBuilder.DefineDynamicModule(asmName.Name!, dllName);
        var typeBuilder = moduleBuilder.DefineType(
            $"RIG.PROCESSUS.FORM_{codeProcessus}",
            TypeAttributes.Public | TypeAttributes.Class);
        typeBuilder.CreateType();

        asmBuilder.Save(dllName);

        return Path.Combine(directory, dllName);
    }

    /// <summary>
    /// Crée un répertoire isolé contenant N stubs avec les codes fournis,
    /// et retourne le chemin du répertoire.
    /// </summary>
    public static string CreatePluginDirectoryWith(params string[] codes)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "Rig.Wpf.Stubs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var code in codes)
        {
            CreatePluginAssembly(dir, code);
        }
        return dir;
    }
}
