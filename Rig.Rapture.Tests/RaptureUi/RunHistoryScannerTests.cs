using System.IO;
using FluentAssertions;
using Rig.Wpf.Kbis.TestViewer.Services;
using Xunit;

namespace Rig.Rapture.Tests.RaptureUi;

public class RunHistoryScannerTests
{
    [Fact]
    public void Scan_EmptyDir_ReturnsEmpty()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "rwt-test-empty-" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmp);
        try
        {
            var runs = RunHistoryScanner.Scan(tmp);
            runs.Should().BeEmpty();
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Scan_2Dirs_ReturnsBothSortedDesc()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "rwt-test-" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmp);
        try
        {
            Directory.CreateDirectory(Path.Combine(tmp, "20260101-100000"));
            System.Threading.Thread.Sleep(50);
            Directory.CreateDirectory(Path.Combine(tmp, "20260102-100000"));
            var runs = RunHistoryScanner.Scan(tmp);
            runs.Should().HaveCount(2);
            runs[0].RunStamp.Should().Be("20260102-100000");
            runs[1].RunStamp.Should().Be("20260101-100000");
        }
        finally { Directory.Delete(tmp, true); }
    }
}
