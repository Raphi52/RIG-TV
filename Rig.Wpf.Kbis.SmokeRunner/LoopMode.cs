using System;
using System.Linq;

namespace Rig.Wpf.Kbis.SmokeRunner;

/// <summary>
/// Mode --loop : CLI déterministe de la boucle rig-testing.
/// Verbes : run | build | bench. Émet du JSON, range les artefacts.
/// </summary>
internal static class LoopMode
{
    public static int RunLoop(string[] args)
    {
        // Le verbe est le 1er argument non-option après --loop.
        string verb = args.SkipWhile(a => !a.Equals("--loop", StringComparison.OrdinalIgnoreCase))
                          .Skip(1)
                          .FirstOrDefault(a => !a.StartsWith("-"));
        switch ((verb ?? "").ToLowerInvariant())
        {
            case "run":   return LoopRun.Execute(args);
            case "build": return LoopBuild.Execute(args);
            case "bench": return LoopBench.Execute(args);
            default:
                Console.WriteLine("Usage : SmokeRunner --loop {run|build|bench} [options]");
                Console.WriteLine("  run    [--scenarios all|<id,id>] [--apply]");
                Console.WriteLine("  build");
                Console.WriteLine("  bench  {acquire|release|status}");
                return string.IsNullOrEmpty(verb) ? 0 : 64; // 64 = EX_USAGE
        }
    }
}
