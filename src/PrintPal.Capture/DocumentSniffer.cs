using PrintPal.Core;

namespace PrintPal.Capture;

public static class DocumentSniffer
{
    public static DocumentFormat Sniff(ReadOnlySpan<byte> document)
    {
        if (document.Length == 0)
        {
            return DocumentFormat.Unknown;
        }

        var head = document[..Math.Min(document.Length, 1024)];

        if (head.StartsWith("%PDF-"u8)) return DocumentFormat.Pdf;
        if (head.StartsWith("RaS2"u8) || head.StartsWith("PwgRaster"u8)) return DocumentFormat.Pwg;
        if (head.StartsWith("%!PS"u8)) return DocumentFormat.Ps;

        if (head.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
        {
            return DocumentFormat.Oxps;
        }

        if (head.StartsWith(new byte[] { 0x1B, 0x25, 0x2D, 0x31, 0x32, 0x33, 0x34, 0x35, 0x58 }))
        {
            return head.IndexOf("%!PS"u8) >= 0 ? DocumentFormat.Ps : DocumentFormat.Pcl;
        }

        if (head.IndexOf("%!PS"u8) >= 0)
        {
            return DocumentFormat.Ps;
        }

        return DocumentFormat.Unknown;
    }
}
