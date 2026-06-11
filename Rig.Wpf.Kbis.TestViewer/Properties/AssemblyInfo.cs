using System.Runtime.CompilerServices;

// Expose les membres internal (ExtractFromFile, ClassifyByClassName de RegressionCatalog)
// au projet de tests xUnit, sans les rendre publics.
[assembly: InternalsVisibleTo("Rig.Rapture.Tests")]
