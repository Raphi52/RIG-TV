using System;
using System.IO;

namespace Rig.Wpf.Kbis.SmokeRunner
{
    /// <summary>
    /// Racine UNIQUE des artefacts d'audit du harnais (sentinel last-batch-end, JSON last-batch-result,
    /// screenshots-loop, ml-loop-*, LOOP_STATE, rig-menu-tree…).
    /// Défaut = <c>C:\Code RIG\RIG-TV\Audit</c> (gitignored dans le repo RIG-TV → présent mais jamais
    /// commité) ; surchargeable par la variable d'env <c>RIG_AUDIT_ROOT</c>.
    /// Centralisé dans l'assembly SmokeRunner (référencé par TestViewer) → les 2 projets partagent la
    /// MÊME source de vérité, plus aucun <c>C:\Code RIG\Audit</c> codé en dur.
    /// </summary>
    public static class AuditPaths
    {
        /// <summary>Racine des artefacts. Ordre : env <c>RIG_AUDIT_ROOT</c> &gt; défaut <c>RIG-TV\Audit</c>.</summary>
        public static string Root
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("RIG_AUDIT_ROOT");
                if (!string.IsNullOrWhiteSpace(env)) return env;
                return @"C:\Code RIG\RIG-TV\Audit";
            }
        }

        /// <summary>Combine un sous-chemin sous <see cref="Root"/> (ne crée pas le dossier).</summary>
        public static string Combine(params string[] parts)
        {
            var all = new string[parts.Length + 1];
            all[0] = Root;
            Array.Copy(parts, 0, all, 1, parts.Length);
            return Path.Combine(all);
        }

        /// <summary>Combine un sous-chemin et s'assure que le dossier <paramref name="parts"/> existe.</summary>
        public static string EnsureDir(params string[] parts)
        {
            var p = Combine(parts);
            if (!Directory.Exists(p)) Directory.CreateDirectory(p);
            return p;
        }
    }
}
