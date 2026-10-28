using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using PrintPal.Core;
using UglyToad.PdfPig;

namespace PrintPal.Capture;

public sealed record PageCountResult(int Pages, string Details);

/// <summary>
/// Counts pages from the rendered print stream. Windows 10 and Windows 11 both send
/// PDF on the IPP path, so PDF is the primary format. PWG page counting is not
/// implemented yet: neither target OS produced PWG in the P0 validation, and a
/// correct implementation needs a real PWG sample (the CUPS/PWG header is different
/// from the exploratory "PwgRaster" guess used by the P0 probe).
/// </summary>
public static class PageCounters
{
    public static PageCountResult Count(string path, DocumentFormat format)
    {
        try
        {
            return format switch
            {
                DocumentFormat.Pdf => CountPdf(path),
                DocumentFormat.Ps => CountPs(path),
                DocumentFormat.Oxps => CountOxps(path),
                DocumentFormat.Pwg => new PageCountResult(
                    0,
                    "PWG page count is not implemented; Windows 10/11 IPP sends PDF"),
                _ => new PageCountResult(0, $"no page counter for {format}"),
            };
        }
        catch (Exception ex)
        {
            return new PageCountResult(0, $"{format} page count failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static PageCountResult CountPdf(string path)
    {
        using var document = PdfDocument.Open(path);
        return new PageCountResult(document.NumberOfPages, $"PdfPig NumberOfPages={document.NumberOfPages}");
    }

    private static PageCountResult CountPs(string path)
    {
        var text = File.ReadAllText(path, Encoding.Latin1);

        var match = Regex.Match(text, @"%%Pages:\s*(\d+)", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var declared))
        {
            return new PageCountResult(declared, $"DSC %%Pages={declared}");
        }

        var showPages = Regex.Matches(text, @"\bshowpage\b", RegexOptions.IgnoreCase).Count;
        return new PageCountResult(showPages, $"counted showpage={showPages}");
    }

    private static PageCountResult CountOxps(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var pages = archive.Entries.Count(entry =>
            entry.FullName.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase));
        return new PageCountResult(pages, $"OXPS .fpage entries={pages}");
    }
}
