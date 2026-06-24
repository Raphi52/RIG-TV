// SPDX-License-Identifier: Proprietary
// Engine de scénarios de pilotage RIG : composer un scénario à partir des primitives LegacyDriver
// EXISTANTES, de façon déclarative (builder fluent), sans réécrire de plomberie ni dupliquer d'adapter.
// Le builder produit une ScenarioDefinition immuable ; l'exécuteur la déroule en réutilisant le préambule
// commun (Sanity→Launch→Login) et le reporting Pass/Fail/Skip de Program.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

// Le sous-système est `internal` (cohérent avec les DTO voisins Loop* du même projet non packagé) ;
// le projet de test y accède via InternalsVisibleTo.
[assembly: InternalsVisibleTo("Rig.Rapture.Tests")]

namespace Rig.Wpf.Kbis.SmokeRunner.Scenarios;

/// <summary>Issue d'un step. L'ÉCHEC passe exclusivement par une exception (captée par l'exécuteur) —
/// d'où seulement deux valeurs ici : un step réussit (Pass) ou ne s'applique pas (Skip).</summary>
internal enum StepOutcome { Pass, Skip }

/// <summary>Échec d'une assertion sémantique (<see cref="ScenarioBuilder.Expect"/>) — distinct d'un crash
/// driver dans un stack-trace.</summary>
internal sealed class ScenarioAssertionException : Exception
{
    public ScenarioAssertionException(string message) : base(message) { }
}

/// <summary>Reporter de plomberie : en prod = les Pass/Fail/Skip de Program ; en test = un faux.</summary>
internal interface IStepReporter
{
    void Pass(string label);
    void Fail(string label, Exception ex);
    void Skip(string label);
}

/// <summary>
/// État partagé entre les steps d'UN run (créé frais à chaque exécution → safe en parallèle ; la
/// ScenarioDefinition reste immuable et partagée). Porte le driver + un scratch typé (valeur d'un step
/// au suivant) + des flags booléens (résultat d'un step PRODUCTEUR, lu par les steps CONDITIONNELS).
/// </summary>
internal sealed class ScenarioContext
{
    public LegacyDriver Driver { get; }
    private readonly Dictionary<string, object> _bag = new();
    private readonly Dictionary<string, bool> _flags = new();

    public ScenarioContext(LegacyDriver driver) { Driver = driver; }

    public void Set(string key, object value) => _bag[key] = value;
    public T Get<T>(string key) => _bag.TryGetValue(key, out var v) && v is T t ? t : default;

    public void SetFlag(string key, bool value) => _flags[key] = value;
    public bool Flag(string key) => _flags.TryGetValue(key, out var v) && v;
}

/// <summary>
/// Une étape. <see cref="Label"/> = LE contrat stdout (préfixe matché par les adapters UI → verbatim).
/// <see cref="RequiresFlag"/> (optionnel) : si présent et faux dans le contexte → step SKIP (non exécuté),
/// reporté sous <see cref="SkipLabel"/>. <see cref="ProducesFlag"/> (optionnel) : le résultat (Pass→true /
/// échec ou skip→false) est stocké dans le contexte sous ce nom, pour gater des steps suivants.
/// <see cref="RunSkipLabel"/> : libellé reporté quand le Run lui-même renvoie Skip (action 3-états false).
/// </summary>
internal sealed class ScenarioStep
{
    public string Label { get; }
    public Func<ScenarioContext, StepOutcome> Run { get; }
    public string ProducesFlag { get; }
    public string RequiresFlag { get; }
    public string SkipLabel { get; }
    public string RunSkipLabel { get; }

    public ScenarioStep(string label, Func<ScenarioContext, StepOutcome> run,
        string producesFlag = null, string requiresFlag = null,
        string skipLabel = null, string runSkipLabel = null)
    {
        Label = label ?? throw new ArgumentNullException(nameof(label));
        Run = run ?? throw new ArgumentNullException(nameof(run));
        ProducesFlag = producesFlag;
        RequiresFlag = requiresFlag;
        SkipLabel = skipLabel;
        RunSkipLabel = runSkipLabel;
    }
}

/// <summary>Un scénario = identité + séquence ordonnée de steps. Immuable après <c>Build()</c>.</summary>
internal sealed class ScenarioDefinition
{
    public string Id { get; }
    public string Banner { get; }
    public string Module { get; }
    public IReadOnlyList<ScenarioStep> Steps { get; }

    public ScenarioDefinition(string id, string banner, string module, IReadOnlyList<ScenarioStep> steps)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Banner = banner ?? string.Empty;
        Module = module ?? string.Empty;
        Steps = steps ?? throw new ArgumentNullException(nameof(steps));
    }
}

/// <summary>
/// Builder fluent. Ajouter un scénario = un bloc <c>ScenarioBuilder.New(id, banner).Module("kbis").Step(...).Build()</c>.
///
/// Quel verbe choisir :
/// <list type="bullet">
/// <item><c>Step(label, d =&gt; ...)</c> — geste de pilotage simple (driver seul). LE cas courant.</item>
/// <item><c>StepCtx(label, ctx =&gt; ...)</c> — geste qui partage un état entre steps (ctx.Set/Get).</item>
/// <item><c>GateStep(label, producesFlag, ...)</c> — step dont la réussite PRODUIT un flag (≈ TryStepBool).</item>
/// <item><c>StepIf(requiresFlag, label, ..., skipLabel)</c> — step exécuté SEULEMENT si le flag est vrai (sinon SKIP).</item>
/// <item><c>ActionableStepIf(requiresFlag, label, ..., skipLabel)</c> — gaté + action 3-états (true=Pass/false=Skip/throw=Fail).</item>
/// <item><c>Expect(label, predicate)</c> — assertion sémantique (faux → échec).</item>
/// </list>
/// Note : tout <c>requiresFlag</c> DOIT être produit par un <c>GateStep</c> ANTÉRIEUR — sinon <c>Build()</c> jette.
/// </summary>
internal sealed class ScenarioBuilder
{
    private const string ActionableSkipSuffix = " — SKIP (non actionnable / cas de données, voir log driver)";

    private readonly string _id;
    private readonly string _banner;
    private string _module = string.Empty;
    private readonly List<ScenarioStep> _steps = new();

    private ScenarioBuilder(string id, string banner) { _id = id; _banner = banner; }

    public static ScenarioBuilder New(string id, string banner) => new(id, banner);

    public ScenarioBuilder Module(string module) { _module = module; return this; }

    /// <summary>Geste de pilotage pur (driver seul). throw = Fail, sinon Pass.</summary>
    public ScenarioBuilder Step(string label, Action<LegacyDriver> run)
        => StepCtx(label, ctx => run(ctx.Driver));

    /// <summary>Geste qui produit/consomme un état partagé entre steps (via le contexte).</summary>
    public ScenarioBuilder StepCtx(string label, Action<ScenarioContext> run)
    {
        _steps.Add(new ScenarioStep(label, ctx => { run(ctx); return StepOutcome.Pass; }));
        return this;
    }

    /// <summary>Step plain dont le résultat (Pass / échec-throw) est ENREGISTRÉ sous <paramref name="producesFlag"/>
    /// pour gater des steps suivants (équivalent TryStepBool).</summary>
    public ScenarioBuilder GateStep(string label, string producesFlag, Action<LegacyDriver> run)
    {
        _steps.Add(new ScenarioStep(label, ctx => { run(ctx.Driver); return StepOutcome.Pass; },
            producesFlag: producesFlag));
        return this;
    }

    /// <summary>Step CONDITIONNEL : exécuté seulement si <paramref name="requiresFlag"/> est vrai ; sinon SKIP (sous <paramref name="skipLabel"/>).</summary>
    public ScenarioBuilder StepIf(string requiresFlag, string label, Action<LegacyDriver> run, string skipLabel)
    {
        _steps.Add(new ScenarioStep(label, ctx => { run(ctx.Driver); return StepOutcome.Pass; },
            requiresFlag: requiresFlag, skipLabel: skipLabel));
        return this;
    }

    /// <summary>Step CONDITIONNEL + ACTIONABLE : si <paramref name="requiresFlag"/> est faux → SKIP(skipLabel) ;
    /// sinon l'action renvoie true=Pass / false=Skip(label + suffixe « non actionnable ») / throw=Fail
    /// (équivalent TryStepActionable gaté — le suffixe préserve la distinction des causes de SKIP côté stdout).</summary>
    public ScenarioBuilder ActionableStepIf(string requiresFlag, string label, Func<ScenarioContext, bool> run, string skipLabel)
    {
        _steps.Add(new ScenarioStep(label, ctx => run(ctx) ? StepOutcome.Pass : StepOutcome.Skip,
            requiresFlag: requiresFlag, skipLabel: skipLabel, runSkipLabel: label + ActionableSkipSuffix));
        return this;
    }

    /// <summary>Assertion sémantique : passe si le prédicat est vrai, échoue (throw dédié) sinon.</summary>
    public ScenarioBuilder Expect(string label, Func<ScenarioContext, bool> predicate)
    {
        _steps.Add(new ScenarioStep(label, ctx =>
        {
            if (!predicate(ctx)) throw new ScenarioAssertionException($"Assertion échouée : {label}");
            return StepOutcome.Pass;
        }));
        return this;
    }

    /// <summary>Construit le scénario immuable. VALIDE que tout <c>requiresFlag</c> est produit par un
    /// <c>GateStep</c> ANTÉRIEUR — une faute de frappe sur un nom de flag jette ICI (à la construction)
    /// plutôt que de SKIPper silencieusement à l'exécution (divergence de verdict invisible).</summary>
    public ScenarioDefinition Build()
    {
        var produced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in _steps)
        {
            if (s.RequiresFlag != null && !produced.Contains(s.RequiresFlag))
                throw new InvalidOperationException(
                    $"Scénario '{_id}', step '{s.Label}' : requiresFlag '{s.RequiresFlag}' n'est produit par aucun GateStep antérieur.");
            if (s.ProducesFlag != null) produced.Add(s.ProducesFlag);
        }
        return new ScenarioDefinition(_id, _banner, _module, _steps.AsReadOnly());
    }
}

/// <summary>
/// Exécute une <see cref="ScenarioDefinition"/> : déroule les steps DANS L'ORDRE. Pour chaque step :
/// requiresFlag faux → Skip(skipLabel) ; sinon exécute (exception → Fail) et reporte Pass / Skip(runSkipLabel) ;
/// enregistre le producesFlag éventuel. Découplé de Program (reporter injecté) → testable sans lancer RIG.
/// Contrat : appel SINGLE-THREAD par process (le reporting Pass/Fail/Skip de Program est process-global ;
/// le parallélisme du harnais se fait entre PROCESSUS workers, pas in-process).
/// </summary>
internal static class ScenarioExecutor
{
    public static void Execute(ScenarioDefinition def, ScenarioContext ctx, IStepReporter reporter)
    {
        if (def == null) throw new ArgumentNullException(nameof(def));
        if (reporter == null) throw new ArgumentNullException(nameof(reporter));

        foreach (var step in def.Steps)
        {
            // Step conditionnel non satisfait : SKIP, pas exécuté. (ctx.Flag ne lève jamais → pas de gate-throw.)
            if (step.RequiresFlag != null && !ctx.Flag(step.RequiresFlag))
            {
                reporter.Skip(step.SkipLabel ?? step.Label);
                if (step.ProducesFlag != null) ctx.SetFlag(step.ProducesFlag, false);
                continue;
            }

            StepOutcome outcome;
            try { outcome = step.Run(ctx); }
            catch (Exception ex)
            {
                reporter.Fail(step.Label, ex);
                if (step.ProducesFlag != null) ctx.SetFlag(step.ProducesFlag, false);
                continue;
            }

            if (outcome == StepOutcome.Pass) reporter.Pass(step.Label);
            else reporter.Skip(step.RunSkipLabel ?? step.Label);   // action 3-états → false

            if (step.ProducesFlag != null) ctx.SetFlag(step.ProducesFlag, outcome == StepOutcome.Pass);
        }
    }
}
