namespace PrintPal.Core;

/// <summary>
/// A paper size in IPP/PWG units: 1/100 mm, portrait orientation.
/// </summary>
public sealed record PaperSize
{
    public static readonly PaperSize Unknown = new("Unknown", 0, 0);
    public static readonly PaperSize A4 = new("A4", 21000, 29700);
    public static readonly PaperSize A3 = new("A3", 29700, 42000);
    public static readonly PaperSize A5 = new("A5", 14800, 21000);
    public static readonly PaperSize Letter = new("Letter", 21590, 27940);
    public static readonly PaperSize Legal = new("Legal", 21590, 35560);

    public static readonly IReadOnlyList<PaperSize> Standards = new[]
    {
        A4, A3, A5, Letter, Legal,
    };

    public string Name { get; }

    public int WidthHundredthMm { get; }

    public int HeightHundredthMm { get; }

    public bool IsKnown => WidthHundredthMm > 0 && HeightHundredthMm > 0;

    public bool IsStandard => Standards.Contains(this);

    public PaperSize(string name, int widthHundredthMm, int heightHundredthMm)
    {
        Name = name;
        WidthHundredthMm = widthHundredthMm;
        HeightHundredthMm = heightHundredthMm;
    }

    /// <summary>
    /// Matches a standard size by dimensions (1/100 mm), accepting either
    /// portrait or landscape input. Falls back to a Custom size.
    /// </summary>
    public static PaperSize FromHundredthMm(int width, int height, int tolerance = 300)
    {
        if (width <= 0 || height <= 0)
        {
            return Unknown;
        }

        var w = Math.Min(width, height);
        var h = Math.Max(width, height);

        foreach (var standard in Standards)
        {
            if (Math.Abs(w - standard.WidthHundredthMm) <= tolerance &&
                Math.Abs(h - standard.HeightHundredthMm) <= tolerance)
            {
                return standard;
            }
        }

        return new PaperSize("Custom", w, h);
    }

    public static PaperSize FromMillimeters(double widthMm, double heightMm)
    {
        var w = (int)Math.Round(widthMm * 100);
        var h = (int)Math.Round(heightMm * 100);
        return FromHundredthMm(w, h);
    }

    /// <summary>
    /// Maps the IPP/PWG media keyword. Standard keywords are portrait-oriented,
    /// e.g. iso_a3_297x420mm. The legacy landscape spelling is accepted too.
    /// </summary>
    public static PaperSize FromIppKeyword(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return Unknown;
        }

        var value = keyword.Trim().ToLowerInvariant();

        return value switch
        {
            "iso_a4_210x297mm" => A4,
            "iso_a3_297x420mm" or "iso_a3_420x297mm" => A3,
            "iso_a5_148x210mm" or "iso_a5_210x148mm" => A5,
            "na_letter_8.5x11in" => Letter,
            "na_legal_8.5x14in" => Legal,
            _ => TryParseDimensions(value),
        };
    }

    public static PaperSize FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Unknown;
        }

        return name.Trim().ToUpperInvariant() switch
        {
            "A4" or "ISOA4" or "PSK:ISOA4" => A4,
            "A3" or "ISOA3" or "PSK:ISOA3" => A3,
            "A5" or "ISOA5" or "PSK:ISOA5" => A5,
            "LETTER" or "NORTAMERICALETTER" or "PSK:NORTAMERICALETTER" => Letter,
            "LEGAL" or "NORTAMERICALEGAL" or "PSK:NORTAMERICALEGAL" => Legal,
            _ => Unknown,
        };
    }

    public static PaperSize FromPrintTicketName(string? name) => FromName(name);

    public string ToIppKeyword() => this switch
    {
        _ when this == A4 => "iso_a4_210x297mm",
        _ when this == A3 => "iso_a3_297x420mm",
        _ when this == A5 => "iso_a5_148x210mm",
        _ when this == Letter => "na_letter_8.5x11in",
        _ when this == Legal => "na_legal_8.5x14in",
        _ when IsKnown => $"custom_{WidthHundredthMm}x{HeightHundredthMm}mm",
        _ => "iso_a4_210x297mm",
    };

    public string ToPrintTicketName() => this switch
    {
        _ when this == A4 => "psk:ISOA4",
        _ when this == A3 => "psk:ISOA3",
        _ when this == A5 => "psk:ISOA5",
        _ when this == Letter => "psk:NorthAmericaLetter",
        _ when this == Legal => "psk:NorthAmericaLegal",
        _ => "psk:ISOA4",
    };

    public override string ToString() => IsKnown
        ? $"{Name} ({WidthHundredthMm}x{HeightHundredthMm}/100mm)"
        : Name;

    private static PaperSize TryParseDimensions(string keyword)
    {
        // custom_210x297mm
        if (!keyword.StartsWith("custom_", StringComparison.Ordinal) ||
            !keyword.EndsWith("mm", StringComparison.Ordinal))
        {
            return Unknown;
        }

        var core = keyword["custom_".Length..^"mm".Length];
        var parts = core.Split('x');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var width) ||
            !int.TryParse(parts[1], out var height))
        {
            return Unknown;
        }

        return FromHundredthMm(width, height);
    }
}
