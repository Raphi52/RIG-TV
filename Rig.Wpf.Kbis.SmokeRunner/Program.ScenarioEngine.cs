// SPDX-License-Identifier: Proprietary
// Pont entre l'engine de scénarios (Scenarios/*) et la plomberie d'exécution existante de Program.
using System;
using Rig.Wpf.Kbis.SmokeRunner.Scenarios;

namespace Rig.Wpf.Kbis.SmokeRunner;

internal static partial class Program
{
    /// <summary>Reporter qui branche l'exécuteur de scénarios sur les Pass/Fail/Skip existants de Program
    /// (compteurs + lignes ✓/✗/⊘ stdout — le contrat lu par les adapters UI).</summary>
    private sealed class ProgramStepReporter : IStepReporter
    {
        public void Pass(string label) => Program.Pass(label);
        public void Fail(string label, Exception ex) => Program.Fail(label, ex ?? new Exception("Échec"));
        public void Skip(string label) => Program.Skip(label);
    }

    /// <summary>
    /// Exécute un scénario défini via le builder. Réutilise TEL QUEL le préambule commun
    /// (Sanity→Launch→Login→self-snap, dans <see cref="RunLegacyKbisScenario"/>) et le reporting
    /// Pass/Fail/Skip — l'engine n'ajoute aucune plomberie, il COMPOSE l'existant.
    /// </summary>
    private static int RunScenarioDefinition(ScenarioDefinition def)
        => RunLegacyKbisScenario(def.Id, def.Banner,
               driver => ScenarioExecutor.Execute(def, new ScenarioContext(driver), new ProgramStepReporter()));
}
