using PrintPal.Core;
using Xunit;

namespace PrintPal.Core.Tests;

public sealed class PrinterTests
{
    private static Printer Printer(bool enabled = true, bool? lastTestOk = null, DateTimeOffset? testedAt = null,
        DateTimeOffset? successAt = null) => new()
    {
        Name = "P1",
        Enabled = enabled,
        LastTestOk = lastTestOk,
        LastTestedAtUtc = testedAt,
        LastSuccessAtUtc = successAt,
    };

    // ------------------------------------------------------------ capability validation

    [Fact]
    public void Capability_without_papers_is_invalid()
    {
        var capability = new PrinterRoutingCapability
        {
            ColorModes = { ColorMode.Monochrome },
            DuplexModes = { DuplexMode.Simplex },
        };

        var errors = PrinterCapabilityValidation.Validate(capability);

        Assert.Single(errors);
        Assert.Contains("纸张", errors[0]);
    }

    [Fact]
    public void Capability_without_colors_is_invalid()
    {
        var capability = new PrinterRoutingCapability
        {
            PaperSizes = { "A4" },
            DuplexModes = { DuplexMode.Simplex },
        };

        var errors = PrinterCapabilityValidation.Validate(capability);

        Assert.Single(errors);
        Assert.Contains("颜色", errors[0]);
    }

    [Fact]
    public void Capability_without_duplex_is_invalid()
    {
        var capability = new PrinterRoutingCapability
        {
            PaperSizes = { "A4" },
            ColorModes = { ColorMode.Monochrome },
        };

        var errors = PrinterCapabilityValidation.Validate(capability);

        Assert.Single(errors);
        Assert.Contains("单双面", errors[0]);
    }

    [Fact]
    public void Complete_capability_is_valid()
    {
        var capability = new PrinterRoutingCapability
        {
            PaperSizes = { "A4" },
            ColorModes = { ColorMode.Monochrome },
            DuplexModes = { DuplexMode.Simplex },
        };

        Assert.Empty(PrinterCapabilityValidation.Validate(capability));
    }

    // ------------------------------------------------------------ status

    [Fact]
    public void Disabled_printer_wins_over_any_test_state()
    {
        var printer = Printer(enabled: false, lastTestOk: true, testedAt: DateTimeOffset.UtcNow,
            successAt: DateTimeOffset.UtcNow);

        var status = PrinterStatus.Describe(printer);

        Assert.Equal(PrinterStatusKind.Disabled, status.Kind);
        Assert.Equal("已停用", status.PillText);
        Assert.Null(status.DetailText);
    }

    [Fact]
    public void Never_tested_printer_shows_未检测()
    {
        var status = PrinterStatus.Describe(Printer());

        Assert.Equal(PrinterStatusKind.Untested, status.Kind);
        Assert.Equal("未检测", status.PillText);
        Assert.Null(status.DetailText);
    }

    [Fact]
    public void Online_printer_shows_test_time()
    {
        var testedAt = new DateTimeOffset(2026, 11, 11, 6, 5, 22, TimeSpan.Zero);
        var printer = Printer(lastTestOk: true, testedAt: testedAt);
        var local = testedAt.ToLocalTime();

        var status = PrinterStatus.Describe(printer);

        Assert.Equal(PrinterStatusKind.Online, status.Kind);
        Assert.Equal("在线", status.PillText);
        Assert.Equal(local.ToString("yyyy-MM-dd HH:mm"), status.DetailText);
        Assert.Contains(local.ToString("yyyy-MM-dd HH:mm:ss"), status.TooltipText);
    }

    [Fact]
    public void Failed_printer_shows_last_success_time()
    {
        var failedAt = new DateTimeOffset(2026, 11, 11, 6, 5, 22, TimeSpan.Zero);
        var successAt = new DateTimeOffset(2026, 11, 10, 2, 22, 7, TimeSpan.Zero);
        var printer = Printer(lastTestOk: false, testedAt: failedAt, successAt: successAt);
        var localSuccess = successAt.ToLocalTime();

        var status = PrinterStatus.Describe(printer);

        Assert.Equal(PrinterStatusKind.Failed, status.Kind);
        Assert.Equal("连接失败", status.PillText);
        Assert.Equal($"上次成功 {localSuccess:yyyy-MM-dd HH:mm}", status.DetailText);
    }

    [Fact]
    public void Failed_printer_without_success_has_no_detail()
    {
        var printer = Printer(lastTestOk: false, testedAt: new DateTimeOffset(2026, 11, 11, 6, 5, 0, TimeSpan.Zero));

        var status = PrinterStatus.Describe(printer);

        Assert.Equal(PrinterStatusKind.Failed, status.Kind);
        Assert.Null(status.DetailText);
        Assert.Contains("从未成功", status.TooltipText);
    }

    // ------------------------------------------------------------ test result text

    [Fact]
    public void Success_result_includes_full_time()
    {
        var testedAt = new DateTimeOffset(2026, 11, 11, 6, 5, 22, TimeSpan.Zero);
        var local = testedAt.ToLocalTime();

        var text = PrinterStatus.FormatTestResult(true, testedAt, testedAt);

        Assert.Equal($"连接成功 · {local:yyyy-MM-dd HH:mm:ss}", text);
    }

    [Fact]
    public void Failure_result_includes_last_success_time()
    {
        var testedAt = new DateTimeOffset(2026, 11, 11, 6, 5, 22, TimeSpan.Zero);
        var successAt = new DateTimeOffset(2026, 11, 10, 2, 22, 7, TimeSpan.Zero);
        var localSuccess = successAt.ToLocalTime();

        var text = PrinterStatus.FormatTestResult(false, testedAt, successAt);

        Assert.Equal($"连接失败 · 上次成功 {localSuccess:yyyy-MM-dd HH:mm}", text);
    }

    [Fact]
    public void Failure_result_without_success_is_plain_failure()
    {
        Assert.Equal("连接失败", PrinterStatus.FormatTestResult(false, DateTimeOffset.UtcNow, null));
    }
}
