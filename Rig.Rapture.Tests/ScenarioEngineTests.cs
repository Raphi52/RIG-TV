using System;
using System.Collections.Generic;
using System.Linq;
using Rig.Wpf.Kbis.SmokeRunner.Scenarios;
using Xunit;

namespace Rig.Rapture.Tests
{
    /// <summary>
    /// Mécanique de l'engine de scénarios (composition + exécution + gate/skip/actionable), testée SANS RIG :
    /// les steps sont des lambdas no-op et le driver du contexte est null (jamais déréférencé).
    /// </summary>
    public class ScenarioEngineTests
    {
        private sealed class FakeReporter : IStepReporter
        {
            public readonly List<string> Passed = new();
            public readonly List<string> Skipped = new();
            public readonly List<string> Failed = new();
            public void Pass(string label) => Passed.Add(label);
            public void Fail(string label, Exception ex) => Failed.Add(label);
            public void Skip(string label) => Skipped.Add(label);
        }

        [Fact]
        public void Builder_accumulates_steps_in_order_with_metadata()
        {
            var def = ScenarioBuilder.New("id1", "ban").Module("kbis")
                .Step("s1", d => { })
                .Step("s2", d => { })
                .Build();

            Assert.Equal("id1", def.Id);
            Assert.Equal("ban", def.Banner);
            Assert.Equal("kbis", def.Module);
            Assert.Equal(new[] { "s1", "s2" }, def.Steps.Select(s => s.Label).ToArray());
        }

        [Fact]
        public void Executor_runs_plain_steps_in_order()
        {
            var ran = new List<string>();
            var r = new FakeReporter();
            var def = ScenarioBuilder.New("id", "b").Module("kbis")
                .Step("s1", d => ran.Add("a1"))
                .Step("s2", d => ran.Add("a2"))
                .Build();

            ScenarioExecutor.Execute(def, new ScenarioContext(null), r);

            Assert.Equal(new[] { "s1", "s2" }, r.Passed);
            Assert.Equal(new[] { "a1", "a2" }, ran);
            Assert.Empty(r.Failed);
            Assert.Empty(r.Skipped);
        }

        [Fact]
        public void StepCtx_shares_state_between_steps()
        {
            string captured = null;
            var def = ScenarioBuilder.New("id", "b").Module("kbis")
                .StepCtx("produce", ctx => ctx.Set("k", "v42"))
                .StepCtx("consume", ctx => captured = ctx.Get<string>("k"))
                .Build();

            ScenarioExecutor.Execute(def, new ScenarioContext(null), new FakeReporter());

            Assert.Equal("v42", captured);
        }

        [Fact]
        public void Throwing_step_is_reported_as_fail()
        {
            var r = new FakeReporter();
            var def = ScenarioBuilder.New("id", "b").Module("kbis")
                .Step("boom", d => throw new Exception("x"))
                .Build();

            ScenarioExecutor.Execute(def, new ScenarioContext(null), r);

            Assert.Equal(new[] { "boom" }, r.Failed);
            Assert.Empty(r.Passed);
        }

        [Fact]
        public void GateStep_then_StepIf_runs_when_gate_passed_skips_when_failed()
        {
            // gate réussit → action gatée s'exécute
            var rPass = new FakeReporter();
            ScenarioExecutor.Execute(
                ScenarioBuilder.New("id", "b").Module("dcademat")
                    .GateStep("open", "opened", d => { /* ok */ })
                    .StepIf("opened", "action", d => { }, "action SKIP — pas ouvert")
                    .Build(),
                new ScenarioContext(null), rPass);
            Assert.Equal(new[] { "open", "action" }, rPass.Passed);
            Assert.Empty(rPass.Skipped);

            // gate échoue (throw) → action gatée est SKIP, pas exécutée
            var rSkip = new FakeReporter();
            ScenarioExecutor.Execute(
                ScenarioBuilder.New("id", "b").Module("dcademat")
                    .GateStep("open", "opened", d => throw new Exception("no demande"))
                    .StepIf("opened", "action", d => throw new Exception("ne doit PAS tourner"), "action SKIP — pas ouvert")
                    .Build(),
                new ScenarioContext(null), rSkip);
            Assert.Equal(new[] { "open" }, rSkip.Failed);
            Assert.Equal(new[] { "action SKIP — pas ouvert" }, rSkip.Skipped);
            Assert.Empty(rSkip.Passed);
        }

        [Fact]
        public void ActionableStepIf_false_skips_with_enriched_label_true_passes()
        {
            // flag vrai + action false → Skip avec libellé ENRICHI (suffixe « SKIP » preservé, fidélité legacy) ;
            // flag vrai + action true → Pass.
            var r = new FakeReporter();
            ScenarioExecutor.Execute(
                ScenarioBuilder.New("id", "b").Module("dcademat")
                    .GateStep("open", "opened", d => { })
                    .ActionableStepIf("opened", "act-false", ctx => false, "skip-gate-false")
                    .ActionableStepIf("opened", "act-true", ctx => true, "skip-gate-true")
                    .Build(),
                new ScenarioContext(null), r);

            Assert.Contains("act-true", r.Passed);
            var skip = Assert.Single(r.Skipped);
            Assert.StartsWith("act-false", skip);          // préfixe = contrat adapter préservé
            Assert.Contains("SKIP", skip);                 // marqueur SKIP du legacy NON perdu
            Assert.NotEqual("skip-gate-false", skip);      // ≠ le libellé de gate-faux (cause distincte)
            Assert.Empty(r.Failed);
        }

        [Fact]
        public void ActionableStepIf_gate_false_skips_with_gate_skipLabel()
        {
            var r = new FakeReporter();
            ScenarioExecutor.Execute(
                ScenarioBuilder.New("id", "b").Module("dcademat")
                    .GateStep("open", "opened", d => throw new Exception("pas de demande"))   // flag opened = false
                    .ActionableStepIf("opened", "act", ctx => true, "act SKIP — pas ouvert")
                    .Build(),
                new ScenarioContext(null), r);

            Assert.Equal(new[] { "open" }, r.Failed);
            Assert.Equal(new[] { "act SKIP — pas ouvert" }, r.Skipped);   // gate-faux → skipLabel du gate
        }

        [Fact]
        public void Build_throws_when_requiresFlag_not_produced_upstream()
        {
            // Une faute de frappe sur un nom de flag doit jeter à la CONSTRUCTION, pas SKIP en silence.
            Assert.Throws<InvalidOperationException>(() =>
                ScenarioBuilder.New("id", "b").Module("dcademat")
                    .StepIf("ghost", "action", d => { }, "skip")   // 'ghost' jamais produit
                    .Build());
        }

        [Fact]
        public void Expect_false_reported_fail_true_reported_pass()
        {
            var rFalse = new FakeReporter();
            ScenarioExecutor.Execute(
                ScenarioBuilder.New("id", "b").Module("kbis").Expect("doit être vrai", ctx => false).Build(),
                new ScenarioContext(null), rFalse);
            Assert.Equal(new[] { "doit être vrai" }, rFalse.Failed);

            var rTrue = new FakeReporter();
            ScenarioExecutor.Execute(
                ScenarioBuilder.New("id", "b").Module("kbis").Expect("doit être vrai", ctx => true).Build(),
                new ScenarioContext(null), rTrue);
            Assert.Equal(new[] { "doit être vrai" }, rTrue.Passed);
        }

        [Theory]
        [InlineData("kbis-vk", "kbis", 5, "VK :")]
        [InlineData("kbis-xex", "kbis", 4, "XEX :")]
        [InlineData("alertes-int-form", "alertes", 2, "INT-FORM :")]
        [InlineData("alertes-int-dca", "alertes", 2, "INT-DCA :")]
        [InlineData("alertes-rec-form", "alertes", 3, "REC-FORM :")]
        [InlineData("alertes-rec-dca", "alertes", 3, "REC-DCA :")]
        // DCADEMAT (fabrique paramétrique) : 3 steps si action (validation/reclamation/refus), 2 sinon (interrompue/form-reclamation)
        [InlineData("dcademat-dca-validation", "dcademat", 3, "DCA-VALIDATION :")]
        [InlineData("dcademat-dca-reclamation", "dcademat", 3, "DCA-RECLAMATION :")]
        [InlineData("dcademat-dca-refus", "dcademat", 3, "DCA-REFUS :")]
        [InlineData("dcademat-dca-interrompue", "dcademat", 2, "DCA-INTERROMPUE :")]
        [InlineData("dcademat-form-validation", "dcademat", 3, "FORM-VALIDATION :")]
        [InlineData("dcademat-form-reclamation", "dcademat", 2, "FORM-RECLAMATION :")]
        [InlineData("dcademat-form-refus", "dcademat", 3, "FORM-REFUS :")]
        [InlineData("dcademat-form-interrompue", "dcademat", 2, "FORM-INTERROMPUE :")]
        public void Catalog_migrated_scenario_has_expected_shape(string id, string module, int stepCount, string prefix)
        {
            var def = LegacyScenarioCatalog.TryGet(id);

            Assert.NotNull(def);
            Assert.Equal(module, def.Module);
            Assert.Equal(stepCount, def.Steps.Count);            // même nombre de steps que l'original impératif
            Assert.All(def.Steps, s => Assert.StartsWith(prefix, s.Label));  // préfixe = contrat stdout adapter
        }
    }
}
