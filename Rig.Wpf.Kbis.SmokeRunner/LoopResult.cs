using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Rig.Wpf.Kbis.SmokeRunner;

/// <summary>Verdict d'un scénario. PASS/FAIL/FLAKY/SKIPPED.</summary>
[DataContract]
internal sealed class LoopScenarioResult
{
    [DataMember(Name = "id")]          public string Id { get; set; }
    [DataMember(Name = "verdict")]     public string Verdict { get; set; }   // PASS|FAIL|FLAKY|SKIPPED
    [DataMember(Name = "retried")]     public bool Retried { get; set; }
    [DataMember(Name = "durationMs")] public long DurationMs { get; set; }
    [DataMember(Name = "failReason")] public string FailReason { get; set; } // null si PASS
    [DataMember(Name = "artifacts")]  public List<string> Artifacts { get; set; } = new List<string>();
}

[DataContract]
internal sealed class LoopSummary
{
    [DataMember(Name = "passed")]     public int Passed { get; set; }
    [DataMember(Name = "failed")]     public int Failed { get; set; }
    [DataMember(Name = "flaky")]      public int Flaky { get; set; }
    [DataMember(Name = "skipped")]    public int Skipped { get; set; }
    [DataMember(Name = "durationMs")] public long DurationMs { get; set; }
}

[DataContract]
internal sealed class LoopRunResult
{
    [DataMember(Name = "runId")]      public string RunId { get; set; }
    [DataMember(Name = "startedAt")] public string StartedAt { get; set; } // ISO 8601
    [DataMember(Name = "summary")]   public LoopSummary Summary { get; set; }
    [DataMember(Name = "scenarios")] public List<LoopScenarioResult> Scenarios { get; set; } = new List<LoopScenarioResult>();

    /// <summary>Écrit le result.json (indenté, UTF-8 sans BOM).</summary>
    public void WriteTo(string path)
    {
        var ser = new DataContractJsonSerializer(typeof(LoopRunResult));
        using (var ms = new MemoryStream())
        {
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                ser.WriteObject(w, this);
            File.WriteAllBytes(path, ms.ToArray());
        }
    }

    public static LoopRunResult ReadFrom(string path)
    {
        var ser = new DataContractJsonSerializer(typeof(LoopRunResult));
        using (var fs = File.OpenRead(path))
            return (LoopRunResult)ser.ReadObject(fs);
    }
}
