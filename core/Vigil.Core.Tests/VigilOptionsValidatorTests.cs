using Vigil.Core.Configuration;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class VigilOptionsValidatorTests
{
    private static IEnumerable<string> Problems(VigilOptions options) =>
        new VigilOptionsValidator().Validate(null, options).Failures ?? [];

    [Fact]
    public void TheDefaultsAreValid()
    {
        Assert.Empty(Problems(new VigilOptions()));
    }

    [Fact]
    public void AWebRootWithNoBuildInItFailsStartupRatherThanEveryRequest()
    {
        var empty = Directory.CreateTempSubdirectory("vigil-empty-root-");
        try
        {
            var problem = Assert.Single(Problems(new VigilOptions { WebRoot = empty.FullName }));
            Assert.Contains("WEB_ROOT", problem, StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(empty.FullName, "index.html"), "<!doctype html>");
            Assert.Empty(Problems(new VigilOptions { WebRoot = empty.FullName }));
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("ntfy.sh/private-topic")]
    [InlineData("ftp://ntfy.sh/private-topic")]
    public void AnNtfyUrlThatIsNotHttpIsRefusedWithoutRepeatingIt(string url)
    {
        var problem = Assert.Single(Problems(new VigilOptions { NtfyUrl = url }));

        Assert.Contains("NTFY_URL", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("private-topic", problem, StringComparison.Ordinal);
    }
}
