using Jellyfin.Hls.Worker.Jobs;
using Jellyfin.Hls.Worker.Protocol;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

public sealed class KeptOutputsTests : IDisposable
{
    private const string OtherAttempt = "00000000000000000000000000000001";
    private const string PlaylistFile = "index.m3u8";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hls-worker-kept-" + Guid.NewGuid().ToString("N"));

    public KeptOutputsTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void AFirstPlan_KeepsNothing_AndRemovesWhatIsThere()
    {
        Write("000", "stale");

        Assert.Empty(KeptOutputs.Load(_directory, TestPlans.Plan(3)));

        Assert.False(Directory.Exists(Path.Combine(_directory, "output")));
        Assert.True(File.Exists(Path.Combine(_directory, "plan.json")));
    }

    [Fact]
    public void TheSamePlan_KeepsTheRenditionsEncodedToTheirEnd()
    {
        var plan = TestPlans.Plan(3);
        KeptOutputs.Load(_directory, plan);
        Write("000", "v240");
        Write("001", "v144, not finished");
        KeptOutputs.MarkEncoded(_directory, ["v240"]);

        var kept = KeptOutputs.Load(_directory, plan);

        Assert.Equal(["v240"], kept);
        Assert.Equal("v240", Read("000"));
        Assert.False(Directory.Exists(Path.Combine(_directory, "output", "001")));
    }

    [Fact]
    public void APlanOfAnotherJob_KeepsTheRenditionsOfTheSameSpec_ByTheirKey()
    {
        KeptOutputs.Load(_directory, TestPlans.Plan(3));
        Write("000", "v240");
        Write("002", "a1");
        KeptOutputs.MarkEncoded(_directory, ["v240", "a1"]);

        // The new job drops 144p and puts the audio first, in directories of its own; its audio has another source track.
        var old = TestPlans.Plan(3);
        var plan = old with
        {
            Renditions =
            [
                old.Renditions[2] with { RelativeDirectory = old.Renditions[2].RelativeDirectory.Replace(TestPlans.Attempt, OtherAttempt, StringComparison.Ordinal) },
                old.Renditions[0] with { RelativeDirectory = old.Renditions[0].RelativeDirectory.Replace(TestPlans.Attempt, OtherAttempt, StringComparison.Ordinal) }
            ]
        };

        Assert.Equal(["a1", "v240"], KeptOutputs.Load(_directory, plan).Order(StringComparer.Ordinal));
        Assert.Equal("a1", Read("000"));
        Assert.Equal("v240", Read("001"));

        var changed = plan with { Renditions = [plan.Renditions[0] with { Channels = 6 }, plan.Renditions[1]] };
        KeptOutputs.MarkEncoded(_directory, ["a1", "v240"]);
        Assert.Equal(["v240"], KeptOutputs.Load(_directory, changed));
        Assert.False(Directory.Exists(Path.Combine(_directory, "output", "000")));
        Assert.Equal("v240", Read("001"));
    }

    [Fact]
    public void APlanOfOtherSegments_OrAPlanFileThatCannotBeRead_KeepsNothing()
    {
        var plan = TestPlans.Plan(3);
        KeptOutputs.Load(_directory, plan);
        Write("000", "v240");
        KeptOutputs.MarkEncoded(_directory, ["v240"]);

        Assert.Empty(KeptOutputs.Load(_directory, plan with { ExpectedSegments = 4 }));
        Assert.False(Directory.Exists(Path.Combine(_directory, "output", "000")));

        Write("000", "v240");
        KeptOutputs.MarkEncoded(_directory, ["v240"]);
        File.WriteAllText(Path.Combine(_directory, "plan.json"), "{ not json");
        Assert.Empty(KeptOutputs.Load(_directory, plan));
    }

    private void Write(string index, string content)
    {
        var directory = Path.Combine(_directory, "output", index);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PlaylistFile), content);
    }

    private string Read(string index) => File.ReadAllText(Path.Combine(_directory, "output", index, PlaylistFile));
}
