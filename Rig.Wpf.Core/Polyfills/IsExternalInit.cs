// Polyfill requis pour utiliser les records et les init-only setters sur net48.
// Sur .NET 5+ ce type est défini par le runtime ; cette version internal sert
// uniquement à la compilation côté Roslyn.

namespace System.Runtime.CompilerServices;

using System.ComponentModel;

[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}
