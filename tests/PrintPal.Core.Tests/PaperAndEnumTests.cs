using PrintPal.Core;
using Xunit;

namespace PrintPal.Core.Tests;

public sealed class PaperAndEnumTests
{
    [Theory]
    [InlineData(21000, 29700, "A4")]
    [InlineData(20999, 29699, "A4")]
    [InlineData(29700, 42000, "A3")]
    [InlineData(42000, 29700, "A3")] // landscape input
    [InlineData(14800, 21000, "A5")]
    [InlineData(21590, 27940, "Letter")]
    [InlineData(21590, 35560, "Legal")]
    public void FromHundredthMm_matches_standard_sizes(int width, int height, string expected)
    {
        Assert.Equal(expected, PaperSize.FromHundredthMm(width, height).Name);
    }

    [Fact]
    public void FromHundredthMm_returns_custom_for_unknown_size()
    {
        var paper = PaperSize.FromHundredthMm(20000, 30000);

        Assert.Equal("Custom", paper.Name);
        Assert.False(paper.IsStandard);
        Assert.True(paper.IsKnown);
    }

    [Fact]
    public void Unknown_or_zero_dimensions_are_unknown()
    {
        Assert.Equal(PaperSize.Unknown, PaperSize.FromHundredthMm(0, 29700));
        Assert.Equal(PaperSize.Unknown, PaperSize.FromHundredthMm(21000, 0));
    }

    [Theory]
    [InlineData("iso_a4_210x297mm", "A4")]
    [InlineData("iso_a3_297x420mm", "A3")]
    [InlineData("iso_a3_420x297mm", "A3")] // legacy landscape spelling
    [InlineData("iso_a5_148x210mm", "A5")]
    [InlineData("na_letter_8.5x11in", "Letter")]
    [InlineData("na_legal_8.5x14in", "Legal")]
    public void FromIppKeyword_maps_standard_keywords(string keyword, string expected)
    {
        Assert.Equal(expected, PaperSize.FromIppKeyword(keyword).Name);
    }

    [Fact]
    public void A3_keyword_is_portrait()
    {
        Assert.Equal("iso_a3_297x420mm", PaperSize.A3.ToIppKeyword());
    }

    [Fact]
    public void FromMillimeters_rounds_to_standard()
    {
        Assert.Equal(PaperSize.A4, PaperSize.FromMillimeters(210, 297));
        Assert.Equal(PaperSize.A3, PaperSize.FromMillimeters(297, 420));
    }

    [Theory]
    [InlineData("color", ColorMode.Color)]
    [InlineData("COLOR", ColorMode.Color)]
    [InlineData("monochrome", ColorMode.Monochrome)]
    [InlineData("grayscale", ColorMode.Monochrome)]
    [InlineData("weird", ColorMode.Unknown)]
    public void ColorMode_maps_ipp_keywords(string value, ColorMode expected)
    {
        Assert.Equal(expected, ColorModeExtensions.FromIppKeyword(value));
    }

    [Theory]
    [InlineData("one-sided", DuplexMode.Simplex)]
    [InlineData("two-sided-long-edge", DuplexMode.TwoSidedLongEdge)]
    [InlineData("TWO-SIDED-SHORT-EDGE", DuplexMode.TwoSidedShortEdge)]
    [InlineData("weird", DuplexMode.Unknown)]
    public void DuplexMode_maps_ipp_keywords(string value, DuplexMode expected)
    {
        Assert.Equal(expected, DuplexModeExtensions.FromIppKeyword(value));
    }

    [Theory]
    [InlineData("application/pdf", DocumentFormat.Pdf)]
    [InlineData("image/pwg-raster", DocumentFormat.Pwg)]
    [InlineData("application/postscript", DocumentFormat.Ps)]
    [InlineData("application/oxps", DocumentFormat.Oxps)]
    [InlineData("application/vnd.hp-pcl", DocumentFormat.Pcl)]
    [InlineData("application/octet-stream", DocumentFormat.Unknown)]
    public void DocumentFormat_maps_mime_types(string value, DocumentFormat expected)
    {
        Assert.Equal(expected, DocumentFormatExtensions.FromMimeType(value));
    }

    [Fact]
    public void Color_and_duplex_roundtrip_ipp_keywords()
    {
        Assert.Equal("color", ColorMode.Color.ToIppKeyword());
        Assert.Equal("monochrome", ColorMode.Monochrome.ToIppKeyword());
        Assert.Equal("two-sided-long-edge", DuplexMode.TwoSidedLongEdge.ToIppKeyword());
        Assert.Equal("one-sided", DuplexMode.Simplex.ToIppKeyword());
    }
}
