namespace PrintPal.IppProbe.Capture;

public enum DocumentFormat
{
    Unknown = 0,
    Pdf,
    Pwg,
    Ps,
    Oxps,
    Pcl,
}

public static class DocumentSniffer
{
    public static DocumentFormat Sniff(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return DocumentFormat.Unknown;
        }

        var head = data[..Math.Min(data.Length, 1024)];

        if (head.StartsWith("%PDF-"u8))
        {
            return DocumentFormat.Pdf;
        }

        if (head.StartsWith("PwgRaster"u8))
        {
            return DocumentFormat.Pwg;
        }

        if (head.StartsWith("%!PS"u8))
        {
            return DocumentFormat.Ps;
        }

        // OXPS/XPS is an OPC zip package.
        if (head.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
        {
            return DocumentFormat.Oxps;
        }

        // PJL wrapper used by PCL/PS drivers: ESC %-12345X
        if (head.StartsWith(new byte[] { 0x1B, 0x25, 0x2D, 0x31, 0x32, 0x33, 0x34, 0x35, 0x58 }))
        {
            if (head.IndexOf("%!PS"u8) >= 0)
            {
                return DocumentFormat.Ps;
            }

            return DocumentFormat.Pcl;
        }

        if (head.IndexOf("%!PS"u8) >= 0)
        {
            return DocumentFormat.Ps;
        }

        return DocumentFormat.Unknown;
    }

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
        DocumentFormat.Pcl => "PCL/PJL",
        _ => "Unknown",
    };
}
