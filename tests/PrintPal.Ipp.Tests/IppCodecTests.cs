using PrintPal.Ipp;
using Xunit;

namespace PrintPal.Ipp.Tests;

public sealed class IppCodecTests
{
    [Fact]
    public void Print_job_request_roundtrips_attributes_and_document()
    {
        var document = "%PDF-1.7\n% test\n%%EOF"u8.ToArray();

        var builder = new IppMessageBuilder(1, 1, IppOperation.PrintJob, 42);
        builder.BeginOperationAttributes();
        builder.AddString(IppTag.Uri, "printer-uri", "ipp://127.0.0.1:8631/ipp/print");
        builder.AddString(IppTag.MimeMediaType, "document-format", "application/pdf");
        builder.BeginGroup(IppTag.JobAttributes);
        builder.AddInt(IppTag.Integer, "copies", 3);
        builder.AddString(IppTag.Keyword, "sides", "two-sided-long-edge");
        builder.AddString(IppTag.Keyword, "print-color-mode", "color");

        var body = builder.End().Concat(document).ToArray();

        var request = IppRequest.Parse(body);

        Assert.Equal(1, request.VersionMajor);
        Assert.Equal(IppOperation.PrintJob, request.OperationId);
        Assert.Equal(42u, request.RequestId);
        Assert.Equal("application/pdf", request.GetString("document-format"));
        Assert.Equal(3, request.GetInt("copies"));
        Assert.Equal("two-sided-long-edge", request.GetString("sides"));
        Assert.Equal("color", request.GetString("print-color-mode"));
        Assert.Equal(document, request.Document.ToArray());
    }

    [Fact]
    public void Multi_valued_attributes_are_parsed()
    {
        var builder = new IppMessageBuilder(1, 1, IppOperation.GetPrinterAttributes, 1);
        builder.BeginOperationAttributes();
        builder.AddString(IppTag.Uri, "printer-uri", "ipp://127.0.0.1:8631/ipp/print");
        builder.BeginGroup(IppTag.PrinterAttributes);
        builder.AddString(IppTag.Keyword, "sides-supported", "one-sided");
        builder.AddString(IppTag.Keyword, string.Empty, "two-sided-long-edge");
        builder.AddString(IppTag.Keyword, string.Empty, "two-sided-short-edge");

        var request = IppRequest.Parse(builder.End());

        var sides = request.Get("sides-supported");
        Assert.NotNull(sides);
        Assert.Equal(new[] { "one-sided", "two-sided-long-edge", "two-sided-short-edge" }, sides!.StringValues);
    }

    [Fact]
    public void Collections_are_parsed()
    {
        var builder = new IppMessageBuilder(1, 1, IppOperation.GetPrinterAttributes, 1);
        builder.BeginOperationAttributes();
        builder.BeginGroup(IppTag.PrinterAttributes);
        builder.BeginCollection(IppTag.BegCollection, "media-size-supported");
        builder.AddCollectionMemberInt("x-dimension", 21000);
        builder.AddCollectionMemberInt("y-dimension", 29700);
        builder.EndCollection();
        builder.BeginCollection(IppTag.BegCollection, string.Empty);
        builder.AddCollectionMemberInt("x-dimension", 29700);
        builder.AddCollectionMemberInt("y-dimension", 42000);
        builder.EndCollection();

        var request = IppRequest.Parse(builder.End());
        var collections = request.Collections
            .Where(collection => collection.Name == "media-size-supported")
            .ToList();

        Assert.Equal(2, collections.Count);
        Assert.Equal(21000, collections[0].GetInt("x-dimension"));
        Assert.Equal(29700, collections[0].GetInt("y-dimension"));
        Assert.Equal(29700, collections[1].GetInt("x-dimension"));
        Assert.Equal(42000, collections[1].GetInt("y-dimension"));
    }

    [Fact]
    public void Response_parses_status_and_job_attributes()
    {
        var builder = new IppMessageBuilder(1, 1, IppStatus.SuccessfulOk, 7);
        builder.BeginOperationAttributes();
        builder.BeginGroup(IppTag.JobAttributes);
        builder.AddString(IppTag.Uri, "job-uri", "ipp://127.0.0.1:8631/ipp/print/jobs/5");
        builder.AddInt(IppTag.Integer, "job-id", 5);
        builder.AddInt(IppTag.Enum, "job-state", IppJobState.Completed);
        builder.AddString(IppTag.Keyword, "job-state-reasons", "job-completed-successfully");

        var response = IppResponse.Parse(builder.End());

        Assert.Equal(IppStatus.SuccessfulOk, response.StatusCode);
        Assert.Equal(7u, response.RequestId);
        Assert.Equal(5, response.GetInt("job-id"));
        Assert.Equal(IppJobState.Completed, response.GetInt("job-state"));
    }

    [Fact]
    public void Error_response_parses_status_message()
    {
        var builder = new IppMessageBuilder(1, 1, IppStatus.ClientErrorBadRequest, 3);
        builder.BeginOperationAttributes();
        builder.AddString(IppTag.TextWithoutLanguage, "status-message", "bad request");

        var response = IppResponse.Parse(builder.End());

        Assert.Equal(IppStatus.ClientErrorBadRequest, response.StatusCode);
        Assert.Equal("bad request", response.StatusMessage);
    }

    [Fact]
    public void Ipp_uri_maps_to_http_and_https()
    {
        Assert.Equal(
            "http://127.0.0.1:8631/ipp/print",
            IppUri.ToHttp(new Uri("ipp://127.0.0.1:8631/ipp/print")).ToString());

        Assert.Equal(
            "https://printer.local:631/ipp/print",
            IppUri.ToHttp(new Uri("ipps://printer.local/ipp/print")).ToString());
    }
}
