namespace PrintPal.Core;

public enum ColorMode
{
    Unknown = 0,
    Monochrome = 1,
    Color = 2,
}

public enum DuplexMode
{
    Unknown = 0,
    Simplex = 1,
    TwoSidedLongEdge = 2,
    TwoSidedShortEdge = 3,
}

public enum DocumentFormat
{
    Unknown = 0,
    Pdf = 1,
    Pwg = 2,
    Ps = 3,
    Oxps = 4,
    Pcl = 5,
}

public enum PrinterProtocol
{
    Ipp = 0,
    Raw = 1,
}

public enum JobStatus
{
    Captured = 0,
    Priced = 1,
    NeedsReview = 2,
    Unrouted = 3,
    Held = 4,
    Forwarding = 5,
    Succeeded = 6,
    Failed = 7,
    Unknown = 8,
    Canceled = 9,
}

public static class ColorModeExtensions
{
    public static bool IsColor(this ColorMode mode) => mode == ColorMode.Color;

    public static string ToIppKeyword(this ColorMode mode) => mode switch
    {
        ColorMode.Color => "color",
        _ => "monochrome",
    };

    public static ColorMode FromIppKeyword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ColorMode.Unknown;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "color" => ColorMode.Color,
            "monochrome" or "grayscale" or "grey" or "mono" => ColorMode.Monochrome,
            _ => ColorMode.Unknown,
        };
    }

    public static ColorMode FromPrintTicketName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ColorMode.Unknown;
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "PSK:COLOR" or "COLOR" => ColorMode.Color,
            "PSK:MONOCHROME" or "MONOCHROME" or "PSK:GRAYSCALE" or "GRAYSCALE" => ColorMode.Monochrome,
            _ => ColorMode.Unknown,
        };
    }

    public static ColorMode FromPwg(int bitsPerPixel, int colorSpace)
    {
        // PWG color space: 1 = black, 2 = RGB, 3 = CMYK (and >1 is generally color).
        if (colorSpace > 1 || bitsPerPixel > 8)
        {
            return ColorMode.Color;
        }

        return ColorMode.Monochrome;
    }

    public static string DisplayName(this ColorMode mode) => mode switch
    {
        ColorMode.Color => "彩色",
        ColorMode.Monochrome => "黑白",
        _ => "未知",
    };
}

public static class DuplexModeExtensions
{
    public static bool IsDuplex(this DuplexMode mode) =>
        mode is DuplexMode.TwoSidedLongEdge or DuplexMode.TwoSidedShortEdge;

    public static string ToIppKeyword(this DuplexMode mode) => mode switch
    {
        DuplexMode.TwoSidedLongEdge => "two-sided-long-edge",
        DuplexMode.TwoSidedShortEdge => "two-sided-short-edge",
        _ => "one-sided",
    };

    public static DuplexMode FromIppKeyword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DuplexMode.Unknown;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "one-sided" or "simplex" => DuplexMode.Simplex,
            "two-sided-long-edge" => DuplexMode.TwoSidedLongEdge,
            "two-sided-short-edge" => DuplexMode.TwoSidedShortEdge,
            _ => DuplexMode.Unknown,
        };
    }

    public static DuplexMode FromPrintTicketName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DuplexMode.Unknown;
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "PSK:ONESIDED" or "ONESIDED" => DuplexMode.Simplex,
            "PSK:TWOSIDEDLONGEDGE" or "TWOSIDEDLONGEDGE" => DuplexMode.TwoSidedLongEdge,
            "PSK:TWOSIDEDSHORTEDGE" or "TWOSIDEDSHORTEDGE" => DuplexMode.TwoSidedShortEdge,
            _ => DuplexMode.Unknown,
        };
    }

    public static string DisplayName(this DuplexMode mode) => mode switch
    {
        DuplexMode.Simplex => "单面",
        DuplexMode.TwoSidedLongEdge => "双面·长边",
        DuplexMode.TwoSidedShortEdge => "双面·短边",
        _ => "未知",
    };
}

public static class DocumentFormatExtensions
{
    public static string Extension(this DocumentFormat format) => format switch
    {
        DocumentFormat.Pdf => ".pdf",
        DocumentFormat.Pwg => ".pwg",
        DocumentFormat.Ps => ".ps",
        DocumentFormat.Oxps => ".oxps",
        DocumentFormat.Pcl => ".pcl",
        _ => ".bin",
    };

    public static string DisplayName(this DocumentFormat format) => format switch
    {
        DocumentFormat.Pdf => "PDF",
        DocumentFormat.Pwg => "PWG Raster",
        DocumentFormat.Ps => "PostScript",
        DocumentFormat.Oxps => "OXPS",
        DocumentFormat.Pcl => "PCL",
        _ => "Unknown",
    };

    public static string ToMimeType(this DocumentFormat format) => format switch
    {
        DocumentFormat.Pdf => "application/pdf",
        DocumentFormat.Pwg => "image/pwg-raster",
        DocumentFormat.Ps => "application/postscript",
        DocumentFormat.Oxps => "application/oxps",
        DocumentFormat.Pcl => "application/vnd.hp-pcl",
        _ => "application/octet-stream",
    };

    public static DocumentFormat FromMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return DocumentFormat.Unknown;
        }

        var value = mimeType.Trim().ToLowerInvariant();

        if (value.Contains("pdf", StringComparison.Ordinal)) return DocumentFormat.Pdf;
        if (value.Contains("pwg", StringComparison.Ordinal)) return DocumentFormat.Pwg;
        if (value.Contains("postscript", StringComparison.Ordinal)) return DocumentFormat.Ps;
        if (value.Contains("oxps", StringComparison.Ordinal) || value.Contains("xps", StringComparison.Ordinal)) return DocumentFormat.Oxps;
        if (value.Contains("pcl", StringComparison.Ordinal)) return DocumentFormat.Pcl;
        return DocumentFormat.Unknown;
    }
}
