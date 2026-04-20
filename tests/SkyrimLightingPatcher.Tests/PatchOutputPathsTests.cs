using SkyrimLightingPatcher.Core.Utilities;

namespace SkyrimLightingPatcher.Tests;

public sealed class PatchOutputPathsTests
{
    [Fact]
    public void CreateStampedArchiveFileName_UsesExpectedPrefixAndSixHexSuffix()
    {
        var fileName = PatchOutputPaths.CreateStampedArchiveFileName("abc");

        Assert.Equal("GlowingMeshPatch_ba7816.zip", fileName);
    }

    [Fact]
    public void CreateStampedArchiveFileName_SameSeedIsStable()
    {
        var first = PatchOutputPaths.CreateStampedArchiveFileName("stable-seed");
        var second = PatchOutputPaths.CreateStampedArchiveFileName("stable-seed");

        Assert.Equal(first, second);
    }

    [Fact]
    public void CreateStampedArchiveFileName_ThrowsForBlankSeed()
    {
        Assert.Throws<ArgumentException>(() => PatchOutputPaths.CreateStampedArchiveFileName(string.Empty));
    }
}
