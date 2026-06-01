// Polyfill nécessaire sur .NET Framework 4.8 pour utiliser les `init` setters
// (records + ObjectInitializer setter-only) compilés avec C# 10.
namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
