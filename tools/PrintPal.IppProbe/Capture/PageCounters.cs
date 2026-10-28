using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace PrintPal.IppProbe.Capture;

public sealed record PwgPageInfo(
    int WidthPixels,
    int HeightPixels,
    int Xdpi,
    int Ydpi,
    int BitsPerPixel,
    int ColorSpace,
    int Duplex,
    int Compression,
    int FirstPageNumber)
{
    public bool LooksLikeColor => BitsPerPixel > 8 || ColorSpace is 2 or 3;
}

public sealed record PageCountResult(int Pages, string Details, PwgPageInfo? Pwg = null);

/// <summary>
/// Counts pages from the rendered print stream. Nothing here is read from IPP
/// metadata: the count comes from the document the print system actually sent.
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
                DocumentFormat.Pwg => CountPwg(path),
                DocumentFormat.Ps => CountPs(path),
                DocumentFormat.Oxps => CountOxps(path),
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

        // DSC header: %%Pages: 3 (or "(atend)" when the producer only knows at EOF)
        var match = Regex.Match(text, @"%%Pages:\s*(\d+)", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var declared))
        {
            return new PageCountResult(declared, $"DSC %%Pages={declared}");
        }

        // Fallback: every PostScript page ends with showpage.
        var showPages = Regex.Matches(text, @"\bshowpage\b", RegexOptions.IgnoreCase).Count;
        return new PageCountResult(showPages, $"counted showpage={showPages}");
    }

    private static PageCountResult CountOxps(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var pages = archive.Entries.Count(e =>
            e.FullName.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase));
        return new PageCountResult(pages, $"OXPS .fpage entries={pages}");
    }

    private static PageCountResult CountPwg(string path)
    {
        var data = File.ReadAllBytes(path);
        var (pages, info) = PwgPageCounter.Count(data);
        var details = info is null
            ? $"PWG page headers={pages}"
            : $"PWG page headers={pages}, first={info.WidthPixels}x{info.HeightPixels}px " +
              $"{info.Xdpi}x{info.Ydpi}dpi bpp={info.BitsPerPixel} colorSpace={info.ColorSpace} " +
              $"duplex={info.Duplex} compression={info.Compression}";
        return new PageCountResult(pages, details, info);
    }
}

/// <summary>
/// Minimal PWG Raster page counter.
///
/// Layout of the 64-byte page header (all integers big-endian):
///   0  "PwgRaster"
///   8  page number
///   12 width, 16 height
///   20 xdpi, 24 ydpi
///   28 bits per pixel
///   32 color order, 36 color space
///   40 duplex, 44 print quality
///   48 compression, 52 image data offset
///
/// Compression 0 = none, 1 = PackBits/RLE. Any other value falls back to
/// scanning for the next page magic, which is good enough for a diagnostic probe.
/// </summary>
public static class PwgPageCounter
{
    private static ReadOnlySpan<byte> Magic => "PwgRaster"u8;

    public static (int Pages, PwgPageInfo? Info) Count(byte[] data)
    {
        var offset = 0;
        var pages = 0;
        PwgPageInfo? first = null;

        while (offset >= 0 && offset + 64 <= data.Length && data.AsSpan(offset, 9).SequenceEqual(Magic))
        {
            var header = data.AsSpan(offset, 64);
            var pageNumber = ReadUInt32(header, 8);
            var width = ReadUInt32(header, 12);
            var height = ReadUInt32(header, 16);
            var xdpi = ReadUInt32(header, 20);
            var ydpi = ReadUInt32(header, 24);
            var bpp = ReadUInt32(header, 28);
            var colorSpace = ReadUInt32(header, 36);
            var duplex = ReadUInt32(header, 40);
            var compression = ReadUInt32(header, 48);
            var imageDataOffset = ReadUInt32(header, 52);

            first ??= new PwgPageInfo(
                checked((int)width), checked((int)height),
                checked((int)xdpi), checked((int)ydpi),
                checked((int)bpp), checked((int)colorSpace), checked((int)duplex),
                checked((int)compression), checked((int)pageNumber));

            pages++;

            var dataStart = offset + (int)Math.Max(64u, imageDataOffset);
            var bytesPerLine = (long)((width * (ulong)bpp + 7) / 8);
            int next;

            try
            {
                if (compression == 0)
                {
                    var length = bytesPerLine * height;
                    next = (int)Math.Min(data.Length, dataStart + length);
                }
                else if (compression == 1)
                {
                    var p = dataStart;
                    for (var y = 0; y < height && p < data.Length; y++)
                    {
                        p = SkipPackBitsLine(data, p, bytesPerLine);
                    }
                    next = p;
                }
                else
                {
                    next = FindNextMagic(data, dataStart);
                }
            }
            catch
            {
                next = FindNextMagic(data, dataStart);
            }

            if (next < 0)
            {
                break;
            }

            // Some producers pad between pages. Accept the next magic within 4 KiB.
            if (!StartsWithMagic(data, next))
            {
                var found = FindNextMagic(data, next);
                if (found >= 0 && found - next <= 4096)
                {
                    next = found;
                }
                else
                {
                    break;
                }
            }

            if (next <= offset)
            {
                break;
            }

            offset = next;

            if (pages > 100_000)
            {
                break;
            }
        }

        if (pages == 0)
        {
            pages = CountOccurrences(data, Magic);
        }

        return (pages, first);
    }

    private static int SkipPackBitsLine(byte[] data, int position, long outputBytes)
    {
        long output = 0;

        while (output < outputBytes)
        {
            if (position >= data.Length)
            {
                throw new EndOfStreamException();
            }

            var control = (sbyte)data[position++];

            if (control >= 0)
            {
                var count = control + 1;
                if (position + count > data.Length)
                {
                    throw new EndOfStreamException();
                }
                position += count;
                output += count;
            }
            else if (control != -128)
            {
                var count = 1 - control;
                if (position + 1 > data.Length)
                {
                    throw new EndOfStreamException();
                }
                position += 1;
                output += count;
            }
        }

        return position;
    }

    private static int FindNextMagic(byte[] data, int start)
    {
        if (start < 0)
        {
            return -1;
        }

        var index = data.AsSpan(start).IndexOf(Magic);
        return index < 0 ? -1 : start + index;
    }

    private static bool StartsWithMagic(byte[] data, int offset) =>
        offset >= 0 && offset + Magic.Length <= data.Length &&
        data.AsSpan(offset, Magic.Length).SequenceEqual(Magic);

    private static int CountOccurrences(byte[] data, ReadOnlySpan<byte> needle)
    {
        var count = 0;
        var position = 0;

        while (position < data.Length)
        {
            var index = data.AsSpan(position).IndexOf(needle);
            if (index < 0)
            {
                break;
            }

            count++;
            position += index + needle.Length;
        }

        return count;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
}
