using System.Drawing;
using System.Drawing.Printing;

namespace PrintPal.PrintProbe;

/// <summary>
/// Generates a real Windows print job with controlled page count, copies, duplex,
/// colour and paper size, without needing UI automation.
///
/// This is the P0 driver for the IPP probe: it exercises exactly the same GDI ->
/// spooler -> IPP Class Driver path that Notepad/Word/WPS use, so the probe sees
/// the same kind of job a real application produces.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = Options.Parse(args);

        if (options.Help)
        {
            Console.WriteLine(Options.HelpText);
            return 0;
        }

        Console.WriteLine("PrintPal P0 print probe");
        Console.WriteLine($"  printer : {options.Printer}");
        Console.WriteLine($"  pages   : {options.Pages}");
        Console.WriteLine($"  copies  : {options.Copies}");
        Console.WriteLine($"  duplex  : {options.Duplex}");
        Console.WriteLine($"  color   : {options.Color}");
        Console.WriteLine($"  paper   : {options.Paper}");
        Console.WriteLine($"  name    : {options.DocumentName}");
        Console.WriteLine();

        try
        {
            using var document = new PrintDocument();
            document.PrinterSettings.PrinterName = options.Printer;

            if (!document.PrinterSettings.IsValid)
            {
                Console.Error.WriteLine($"Printer '{options.Printer}' is not installed or is not valid.");
                Console.Error.WriteLine("Installed printers:");
                foreach (var name in PrinterSettings.InstalledPrinters)
                {
                    Console.Error.WriteLine($"  - {name}");
                }
                return 2;
            }

            document.DocumentName = options.DocumentName;
            document.PrintController = new StandardPrintController();

            // Copies: MaximumCopies is driver-reported; clamp only when it is a real limit.
            var maxCopies = document.PrinterSettings.MaximumCopies;
            var copies = maxCopies > 0 ? Math.Min(options.Copies, maxCopies) : options.Copies;
            document.PrinterSettings.Copies = (short)copies;

            try
            {
                document.PrinterSettings.Duplex = options.Duplex switch
                {
                    "long" => Duplex.Vertical,
                    "short" => Duplex.Horizontal,
                    _ => Duplex.Simplex,
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WARN: driver rejected Duplex={options.Duplex}: {ex.Message}");
            }

            document.DefaultPageSettings.Color = options.Color == "color";

            var paper = PaperSizes.Resolve(options.Paper);
            document.DefaultPageSettings.PaperSize = paper;

            var pageIndex = 0;

            document.PrintPage += (_, e) =>
            {
                pageIndex++;

                if (e.Graphics is null)
                {
                    e.Cancel = true;
                    return;
                }

                using var titleFont = new Font("Segoe UI", 18, FontStyle.Bold);
                using var bodyFont = new Font("Segoe UI", 11);
                using var brush = new SolidBrush(Color.Black);
                using var pen = new Pen(Color.Gray, 1);

                var bounds = e.MarginBounds;
                e.Graphics.DrawRectangle(pen, bounds);

                var y = bounds.Top + 24;
                e.Graphics.DrawString("PrintPal P0 print probe", titleFont, brush, bounds.Left + 24, y);
                y += 48;

                void Line(string text)
                {
                    e.Graphics.DrawString(text, bodyFont, brush, bounds.Left + 24, y);
                    y += 24;
                }

                Line($"Page: {pageIndex} / {options.Pages}");
                Line($"Printer: {options.Printer}");
                Line($"Copies requested: {options.Copies} (driver max: {maxCopies})");
                Line($"Duplex requested: {options.Duplex}");
                Line($"Color requested: {options.Color}");
                Line($"Paper requested: {options.Paper} ({paper.Width} x {paper.Height} hundredths of inch)");
                Line($"Job name: {options.DocumentName}");
                Line($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

                y += 24;
                Line("If this page reached the IPP probe with the requested attributes,");
                Line("the Windows IPP Class Driver -> local IPP server path works.");

                e.HasMorePages = pageIndex < options.Pages;
            };

            Console.WriteLine("Calling PrintDocument.Print() ...");
            document.Print();
            Console.WriteLine("Print() returned. The spooler has accepted the job.");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PRINT FAILED: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static class PaperSizes
    {
        public static PaperSize Resolve(string name) => name.ToUpperInvariant() switch
        {
            "A3" => new PaperSize("A3", 1169, 1654),
            "A5" => new PaperSize("A5", 583, 827),
            "LETTER" => new PaperSize("Letter", 850, 1100),
            "LEGAL" => new PaperSize("Legal", 850, 1400),
            _ => new PaperSize("A4", 827, 1169),
        };
    }

    private sealed class Options
    {
        public string Printer { get; private set; } = "PrintPal 虚拟打印机";
        public int Pages { get; private set; } = 1;
        public int Copies { get; private set; } = 1;
        public string Duplex { get; private set; } = "simplex";
        public string Color { get; private set; } = "mono";
        public string Paper { get; private set; } = "A4";
        public string DocumentName { get; private set; } = $"PrintPal P0 {DateTime.Now:HHmmss}";
        public bool Help { get; private set; }

        public static string HelpText =>
            """
            Usage: PrintPal.PrintProbe.exe [options]

              --printer <name>    Printer queue name (default: PrintPal 虚拟打印机)
              --pages <n>         Pages per copy (default: 1)
              --copies <n>        Copies (default: 1)
              --duplex <mode>     simplex | long | short (default: simplex)
              --color <mode>      mono | color (default: mono)
              --paper <size>      A4 | A3 | A5 | Letter | Legal (default: A4)
              --name <text>       Document/job name
              --help              Show this text

            Examples:
              PrintPal.PrintProbe.exe --pages 5 --copies 3 --duplex long --color color --paper A4
              PrintPal.PrintProbe.exe --pages 4 --copies 2 --duplex long --color mono --paper A3
            """;

        public static Options Parse(string[] args)
        {
            var options = new Options();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (arg is "--help" or "-h" or "/?")
                {
                    options.Help = true;
                    continue;
                }

                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value for {arg}");
                }

                var value = args[++i];

                switch (arg)
                {
                    case "--printer":
                        options.Printer = value;
                        break;
                    case "--pages":
                        options.Pages = Math.Max(1, int.Parse(value));
                        break;
                    case "--copies":
                        options.Copies = Math.Max(1, int.Parse(value));
                        break;
                    case "--duplex":
                        options.Duplex = value.ToLowerInvariant() switch
                        {
                            "long" or "vertical" or "duplexlong" => "long",
                            "short" or "horizontal" or "duplexshort" => "short",
                            _ => "simplex",
                        };
                        break;
                    case "--color":
                        options.Color = value.Equals("color", StringComparison.OrdinalIgnoreCase) ? "color" : "mono";
                        break;
                    case "--paper":
                        options.Paper = value;
                        break;
                    case "--name":
                        options.DocumentName = value;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option: {arg}");
                }
            }

            return options;
        }
    }
}
