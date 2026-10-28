using System.IO.Compression;
using System.Text;
using PrintPal.Capture;
using PrintPal.Core;
using Xunit;

namespace PrintPal.Capture.Tests;

public sealed class CaptureTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Sniffer_identifies_pdf_pwg_ps_oxps_and_unknown()
    {
        Assert.Equal(DocumentFormat.Pdf, DocumentSniffer.Sniff("%PDF-1.7\n"u8));
        Assert.Equal(DocumentFormat.Pwg, DocumentSniffer.Sniff("RaS2..."u8));
        Assert.Equal(DocumentFormat.Ps, DocumentSniffer.Sniff("%!PS-Adobe-3.0\n"u8));
        Assert.Equal(DocumentFormat.Oxps, DocumentSniffer.Sniff(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0 }));
        Assert.Equal(DocumentFormat.Ps, DocumentSniffer.Sniff(
            new byte[] { 0x1B, 0x25, 0x2D, 0x31, 0x32, 0x33, 0x34, 0x35, 0x58 }
                .Concat("%!PS"u8.ToArray()).ToArray()));
        Assert.Equal(DocumentFormat.Unknown, DocumentSniffer.Sniff("hello"u8));
        Assert.Equal(DocumentFormat.Unknown, DocumentSniffer.Sniff(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData("wps-3pages.pdf", 3)]
    [InlineData("wps-5pages.pdf", 5)]
    public void Pdf_page_count_matches_fixture(string fixture, int expected)
    {
        var result = PageCounters.Count(Fixture(fixture), DocumentFormat.Pdf);

        Assert.Equal(expected, result.Pages);
        Assert.Contains("PdfPig", result.Details);
    }

    [Fact]
    public void Pwg_page_count_is_explicitly_unsupported_until_a_real_sample_is_added()
    {
        var path = WriteTempFile("RaS2..."u8.ToArray(), ".pwg");

        try
        {
            var result = PageCounters.Count(path, DocumentFormat.Pwg);

            Assert.Equal(0, result.Pages);
            Assert.Contains("not implemented", result.Details);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Ps_page_count_uses_dsc_header()
    {
        var path = WriteTempFile(Encoding.ASCII.GetBytes("%!PS-Adobe-3.0\n%%Pages: 4\nshowpage\nshowpage\n"), ".ps");

        try
        {
            var result = PageCounters.Count(path, DocumentFormat.Ps);
            Assert.Equal(4, result.Pages);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Ps_page_count_falls_back_to_showpage()
    {
        var path = WriteTempFile(Encoding.ASCII.GetBytes("%!PS\nshowpage\nshowpage\n"), ".ps");

        try
        {
            var result = PageCounters.Count(path, DocumentFormat.Ps);
            Assert.Equal(2, result.Pages);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Oxps_page_count_counts_fpage_parts()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".oxps");

        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                for (var i = 1; i <= 3; i++)
                {
                    var entry = archive.CreateEntry($"Documents/1/Pages/{i}.fpage");
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write("<FixedPage />");
                }
            }

            var result = PageCounters.Count(path, DocumentFormat.Oxps);
            Assert.Equal(3, result.Pages);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Document_store_saves_hashes_reads_and_deletes()
    {
        var root = Path.Combine(Path.GetTempPath(), "printpal-tests", Guid.NewGuid().ToString("N"));
        var store = new DocumentStore(root);
        var pdf = File.ReadAllBytes(Fixture("wps-3pages.pdf"));

        var stored = await store.SaveAsync(pdf, DocumentFormat.Pdf);

        Assert.True(File.Exists(stored.Path));
        Assert.Equal(DocumentFormat.Pdf, stored.Format);
        Assert.Equal(pdf.LongLength, stored.Size);
        Assert.Equal(3, stored.PageCount.Pages);
        Assert.Equal(64, stored.Sha256.Length);
        Assert.EndsWith(".pdf", stored.Path, StringComparison.OrdinalIgnoreCase);

        var read = await store.ReadAsync(stored.Path);
        Assert.Equal(pdf, read);

        store.Delete(stored.Path);
        Assert.False(File.Exists(stored.Path));
    }

    [Fact]
    public async Task Document_store_uses_declared_format_when_sniffing_fails()
    {
        var root = Path.Combine(Path.GetTempPath(), "printpal-tests", Guid.NewGuid().ToString("N"));
        var store = new DocumentStore(root);

        var stored = await store.SaveAsync("hello"u8.ToArray(), DocumentFormat.Pdf);

        Assert.Equal(DocumentFormat.Pdf, stored.Format);
        Assert.EndsWith(".pdf", stored.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, stored.PageCount.Pages);
    }

    [Fact]
    public void Document_store_rejects_paths_outside_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "printpal-tests", Guid.NewGuid().ToString("N"));
        var store = new DocumentStore(root);

        Assert.Throws<InvalidOperationException>(() =>
            store.Delete(Path.Combine(root, "..", "outside.txt")));
    }

    private static string WriteTempFile(byte[] bytes, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
