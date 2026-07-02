using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Rig.Rapture.Tests {
	/// <summary>
	/// Résolution runtime des assemblies RIG transitifs pour les tests qui linkent du code wt-edilot3
	/// (RigMetier/RigTools/... copiés Private=true couvrent le direct ; les transitifs se résolvent
	/// depuis le dépôt runtime RIG). Mutualisé ici (avant : lambda identique triplé dans les 3 classes
	/// de test Rapture — judge cycle 1, Lean). Read-only, idempotent.
	/// </summary>
	internal static class RigAssemblyResolver {
		private static int _installed;

		public static void Ensure() {
			if (Interlocked.Exchange(ref _installed, 1) == 1) return;
			AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => {
				string dll = new AssemblyName(e.Name).Name + ".dll";
				string path = Path.Combine(@"C:\rig\Bin Dot Net Gac", dll);
				return File.Exists(path) ? Assembly.LoadFrom(path) : null;
			};
		}
	}
}
