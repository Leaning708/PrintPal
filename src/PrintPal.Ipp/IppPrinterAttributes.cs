using PrintPal.Core;

namespace PrintPal.Ipp;

/// <summary>
/// Builds the printer-attributes response. The attribute set and value types mirror
/// the Windows-compatible P0 probe; several attributes are type-sensitive and will
/// stall the IPP Class Driver if encoded differently.
/// </summary>
internal static class IppPrinterAttributes
{
    public static byte[] Build(IppRequest request, IppServerOptions options)
    {
        var capabilities = options.Capabilities;
        var media = capabilities.MediaSizes.Count > 0
            ? capabilities.MediaSizes
            : PaperSize.Standards.ToList();
        var formats = capabilities.DocumentFormats.Count > 0
            ? capabilities.DocumentFormats
            : new List<DocumentFormat> { DocumentFormat.Pdf, DocumentFormat.Pwg };
        var colors = capabilities.ColorModes.Count > 0
            ? capabilities.ColorModes
            : new List<ColorMode> { ColorMode.Monochrome, ColorMode.Color };
        var sides = capabilities.DuplexModes.Count > 0
            ? capabilities.DuplexModes
            : new List<DuplexMode>
            {
                DuplexMode.Simplex,
                DuplexMode.TwoSidedLongEdge,
                DuplexMode.TwoSidedShortEdge,
            };

        var defaultMedia = media.Contains(PaperSize.A4) ? PaperSize.A4 : media[0];
        var defaultFormat = formats.Contains(DocumentFormat.Pdf) ? DocumentFormat.Pdf : formats[0];
        var defaultColor = colors.Contains(ColorMode.Monochrome) ? ColorMode.Monochrome : colors[0];
        var defaultSide = sides.Contains(DuplexMode.Simplex) ? DuplexMode.Simplex : sides[0];

        var builder = new IppMessageBuilder(
            request.VersionMajor,
            request.VersionMinor,
            IppStatus.SuccessfulOk,
            request.RequestId);

        builder.BeginOperationAttributes();
        builder.BeginGroup(IppTag.PrinterAttributes);

        AddStrings(builder, IppTag.Uri, "printer-uri-supported", options.IppUri, options.HttpUri);
        AddStrings(builder, IppTag.Keyword, "uri-authentication-supported", "none", "none");
        AddStrings(builder, IppTag.Keyword, "uri-security-supported", "none", "none");

        builder.AddString(IppTag.NameWithoutLanguage, "printer-name", options.Name);
        builder.AddString(IppTag.TextWithoutLanguage, "printer-info", options.Name);
        builder.AddString(IppTag.TextWithoutLanguage, "printer-location", options.PrinterLocation ?? "Local");
        builder.AddString(IppTag.TextWithoutLanguage, "printer-make-and-model",
            options.MakeAndModel ?? "PrintPal Virtual Printer");
        builder.AddString(IppTag.TextWithoutLanguage, "printer-state-message", "Ready");

        builder.AddInt(IppTag.Enum, "printer-state", 3);
        builder.AddString(IppTag.Keyword, "printer-state-reasons", "none");
        builder.AddBool(IppTag.Boolean, "printer-is-accepting-jobs", true);
        builder.AddInt(IppTag.Integer, "queued-job-count", 0);

        AddStrings(builder, IppTag.Keyword, "ipp-versions-supported", "1.1", "2.0");
        AddInts(builder, IppTag.Enum, "operations-supported",
            IppOperation.PrintJob,
            IppOperation.ValidateJob,
            IppOperation.CreateJob,
            IppOperation.SendDocument,
            IppOperation.CancelJob,
            IppOperation.GetJobAttributes,
            IppOperation.GetPrinterAttributes);

        builder.AddString(IppTag.Charset, "charset-configured", "utf-8");
        builder.AddString(IppTag.Charset, "charset-supported", "utf-8");
        builder.AddString(IppTag.NaturalLanguage, "natural-language-configured", "en");
        AddStrings(builder, IppTag.NaturalLanguage, "generated-natural-language-supported", "en", "zh-cn");
        AddStrings(builder, IppTag.NaturalLanguage, "printer-strings-languages-supported", "en", "zh-cn");

        builder.AddString(IppTag.MimeMediaType, "document-format-default", defaultFormat.ToMimeType());
        builder.AddString(IppTag.MimeMediaType, "document-format-preferred", defaultFormat.ToMimeType());
        var formatIndex = 0;
        foreach (var format in formats.Distinct())
        {
            builder.AddString(IppTag.MimeMediaType, formatIndex++ == 0 ? "document-format-supported" : string.Empty,
                format.ToMimeType());
        }

        builder.AddString(IppTag.Keyword, "media-default", defaultMedia.ToIppKeyword());
        var mediaIndex = 0;
        foreach (var paper in media)
        {
            builder.AddString(IppTag.Keyword, mediaIndex++ == 0 ? "media-supported" : string.Empty,
                paper.ToIppKeyword());
        }

        mediaIndex = 0;
        foreach (var paper in media)
        {
            builder.AddString(IppTag.Keyword, mediaIndex++ == 0 ? "media-ready" : string.Empty,
                paper.ToIppKeyword());
        }

        AddMediaSizeSupported(builder, media);

        AddStrings(builder, IppTag.Keyword, "media-type-supported", "stationery");
        AddStrings(builder, IppTag.Keyword, "media-source-supported", "auto", "main", "tray-1");
        AddStrings(builder, IppTag.Keyword, "media-col-supported",
            "media-size",
            "media-type",
            "media-source",
            "media-top-margin",
            "media-bottom-margin",
            "media-left-margin",
            "media-right-margin");

        // Windows' IPP parser expects these as plain integers, in 1/100 mm.
        // 423 = 4.23 mm (a normal hardware margin). The previous value 4233 was
        // 42.33 mm, which made the IPP Class Driver clip everything inside the
        // page margin; WPS lays text out at 31.75 mm and lost the left edge.
        const int supportedMarginHundredthMm = 423;
        builder.AddInt(IppTag.Integer, "media-left-margin-supported", supportedMarginHundredthMm);
        builder.AddInt(IppTag.Integer, "media-right-margin-supported", supportedMarginHundredthMm);
        builder.AddInt(IppTag.Integer, "media-top-margin-supported", supportedMarginHundredthMm);
        builder.AddInt(IppTag.Integer, "media-bottom-margin-supported", supportedMarginHundredthMm);

        builder.AddString(IppTag.Keyword, "print-color-mode-default", defaultColor.ToIppKeyword());
        var colorIndex = 0;
        foreach (var color in colors.Distinct())
        {
            builder.AddString(IppTag.Keyword, colorIndex++ == 0 ? "print-color-mode-supported" : string.Empty,
                color.ToIppKeyword());
        }
        builder.AddBool(IppTag.Boolean, "color-supported", colors.Contains(ColorMode.Color));

        builder.AddString(IppTag.Keyword, "sides-default", defaultSide.ToIppKeyword());
        var sideIndex = 0;
        foreach (var side in sides.Distinct())
        {
            builder.AddString(IppTag.Keyword, sideIndex++ == 0 ? "sides-supported" : string.Empty,
                side.ToIppKeyword());
        }

        builder.AddInt(IppTag.Integer, "copies-default", 1);
        builder.AddRange(IppTag.RangeOfInteger, "copies-supported", capabilities.MinCopies, capabilities.MaxCopies);

        builder.AddInt(IppTag.Enum, "orientation-requested-default", 3);
        AddInts(builder, IppTag.Enum, "orientation-requested-supported", 3, 4, 5, 6);

        builder.AddInt(IppTag.Enum, "print-quality-default", 4);
        AddInts(builder, IppTag.Enum, "print-quality-supported", 3, 4, 5);

        builder.AddResolution(IppTag.Resolution, "printer-resolution-default", 600, 600, 3);
        builder.AddResolution(IppTag.Resolution, "printer-resolution-supported", 600, 600, 3);

        builder.AddResolution(IppTag.Resolution, "pwg-raster-document-resolution-supported", 600, 600, 3);
        AddStrings(builder, IppTag.Keyword, "pwg-raster-document-type-supported", "sgray_8", "srgb_8");
        builder.AddString(IppTag.Keyword, "pwg-raster-document-sheet-back", "normal");

        builder.AddString(IppTag.Keyword, "pclm-raster-back-side", "normal");
        builder.AddResolution(IppTag.Resolution, "pclm-source-resolution-supported", 600, 600, 3);

        builder.AddBool(IppTag.Boolean, "pdf-fit-to-page-default", false);
        AddBools(builder, IppTag.Boolean, "pdf-fit-to-page-supported", false, true);
        builder.AddRange(IppTag.RangeOfInteger, "pdf-k-octets-supported", 0, 500_000);

        builder.AddString(IppTag.Keyword, "print-scaling-default", "auto");
        AddStrings(builder, IppTag.Keyword, "print-scaling-supported", "auto", "auto-fit", "fit", "fill", "none");

        builder.AddString(IppTag.Keyword, "multiple-document-handling-default",
            "separate-documents-uncollated-copies");
        AddStrings(builder, IppTag.Keyword, "multiple-document-handling-supported",
            "separate-documents-collated-copies",
            "separate-documents-uncollated-copies",
            "single-document");

        builder.AddInt(IppTag.Integer, "number-up-default", 1);
        AddInts(builder, IppTag.Integer, "number-up-supported", 1, 2, 4, 6, 9, 16);
        builder.AddString(IppTag.Keyword, "presentation-direction-number-up-default", "to-bottom-to-right");
        AddStrings(builder, IppTag.Keyword, "presentation-direction-number-up-supported",
            "to-bottom-to-right",
            "to-top-to-right");

        builder.AddInt(IppTag.Integer, "pages-per-minute", 20);
        builder.AddInt(IppTag.Integer, "pages-per-minute-color", 10);

        builder.AddString(IppTag.Keyword, "output-bin-default", "auto");
        AddStrings(builder, IppTag.Keyword, "output-bin-supported", "auto", "face-down");
        builder.AddString(IppTag.Keyword, "media-source-default", "auto");
        builder.AddInt(IppTag.Enum, "finishings-default", 3);
        AddInts(builder, IppTag.Enum, "finishings-supported", 3);

        builder.AddString(IppTag.Keyword, "pdl-override-supported", "attempted");
        AddStrings(builder, IppTag.Keyword, "compression-supported", "none", "gzip");

        AddStrings(builder, IppTag.Keyword, "job-creation-attributes-supported",
            "copies",
            "media",
            "sides",
            "print-color-mode",
            "print-quality",
            "printer-resolution",
            "job-name",
            "requesting-user-name",
            "document-format");

        builder.AddBool(IppTag.Boolean, "multiple-document-jobs-supported", false);
        builder.AddBool(IppTag.Boolean, "job-ids-supported", true);
        AddStrings(builder, IppTag.Keyword, "which-jobs-supported", "completed", "not-completed", "all");

        builder.AddRange(IppTag.RangeOfInteger, "job-impressions-supported", 1, 9999);
        builder.AddRange(IppTag.RangeOfInteger, "job-media-sheets-supported", 1, 9999);
        builder.AddRange(IppTag.RangeOfInteger, "job-pages-per-set-supported", 1, 9999);
        builder.AddInt(IppTag.Integer, "job-password-supported", 0);
        AddStrings(builder, IppTag.Keyword, "job-password-encryption-supported", "none");

        AddStrings(builder, IppTag.Keyword, "printer-mandatory-job-attributes",
            "attributes-charset",
            "attributes-natural-language");
        builder.AddBool(IppTag.Boolean, "client-info-supported", false);

        // Windows asks for this as a keyword/string list, not a boolean.
        AddStrings(builder, IppTag.Keyword, "document-format-details-supported",
            "document-format",
            "document-format-version",
            "document-format-device-id",
            "document-natural-language",
            "compression");

        builder.AddString(IppTag.Keyword, "mopria-certified", "1.3");
        builder.AddString(IppTag.NameWithoutLanguage, "printer-dns-sd-name", options.Name);
        builder.AddString(IppTag.TextWithoutLanguage, "printer-firmware-name", "PrintPal");
        builder.AddString(IppTag.TextWithoutLanguage, "printer-firmware-string-version", "0.1");
        builder.AddString(IppTag.Uri, "printer-more-info", options.BaseHttpUri);
        builder.AddString(IppTag.TextWithoutLanguage, "printer-device-id",
            "MFG:PrintPal;MDL:Virtual Printer;CMD:PDF,PWG;");

        builder.AddString(IppTag.Uri, "printer-uuid", BuildPrinterUuid(options));

        return builder.End();
    }

    private static string BuildPrinterUuid(IppServerOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PrinterUuid))
        {
            return options.PrinterUuid.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)
                ? options.PrinterUuid
                : "urn:uuid:" + options.PrinterUuid;
        }

        return "urn:uuid:4f7f2e6a-7d7b-4c1e-9f0a-0e6b1c2d3e4f";
    }

    private static void AddMediaSizeSupported(IppMessageBuilder builder, IReadOnlyList<PaperSize> media)
    {
        for (var i = 0; i < media.Count; i++)
        {
            builder.BeginCollection(IppTag.BegCollection, i == 0 ? "media-size-supported" : string.Empty);
            builder.AddCollectionMemberInt("x-dimension", media[i].WidthHundredthMm);
            builder.AddCollectionMemberInt("y-dimension", media[i].HeightHundredthMm);
            builder.EndCollection();
        }
    }

    private static void AddStrings(IppMessageBuilder builder, IppTag tag, string name, params string[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            builder.AddString(tag, i == 0 ? name : string.Empty, values[i]);
        }
    }

    private static void AddInts(IppMessageBuilder builder, IppTag tag, string name, params int[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            builder.AddInt(tag, i == 0 ? name : string.Empty, values[i]);
        }
    }

    private static void AddBools(IppMessageBuilder builder, IppTag tag, string name, params bool[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            builder.AddBool(tag, i == 0 ? name : string.Empty, values[i]);
        }
    }
}
