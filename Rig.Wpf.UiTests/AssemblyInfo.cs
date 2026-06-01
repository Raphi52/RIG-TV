// FlaUI ne supporte pas plusieurs Application.Launch en parallèle ;
// on force xUnit à sérialiser TOUTES les classes de tests UI.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
