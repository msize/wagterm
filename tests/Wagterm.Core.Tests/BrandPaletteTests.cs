using Wagterm.Core;

namespace Wagterm.Core.Tests;

public class BrandPaletteTests
{
    [Fact]
    public void SharedPaletteMatchesTheReferenceIcon()
    {
        Assert.Equal(0xF97316u, BrandPalette.Background);
        Assert.Equal(0x1F2937u, BrandPalette.Ink);
        Assert.Equal(0xFFFFFFu, BrandPalette.Accent);
    }
}
