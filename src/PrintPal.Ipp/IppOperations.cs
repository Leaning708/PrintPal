namespace PrintPal.Ipp;

public static class IppOperation
{
    public const ushort PrintJob = 0x0002;
    public const ushort PrintUri = 0x0003;
    public const ushort ValidateJob = 0x0004;
    public const ushort CreateJob = 0x0005;
    public const ushort SendDocument = 0x0006;
    public const ushort SendUri = 0x0007;
    public const ushort CancelJob = 0x0008;
    public const ushort GetJobAttributes = 0x0009;
    public const ushort GetJobs = 0x000A;
    public const ushort GetPrinterAttributes = 0x000B;
    public const ushort HoldJob = 0x000C;
    public const ushort ReleaseJob = 0x000D;
    public const ushort RestartJob = 0x000E;
    public const ushort PausePrinter = 0x0010;
    public const ushort ResumePrinter = 0x0011;
    public const ushort PurgeJobs = 0x0012;

    public static string GetName(ushort operation) => operation switch
    {
        PrintJob => "Print-Job",
        PrintUri => "Print-URI",
        ValidateJob => "Validate-Job",
        CreateJob => "Create-Job",
        SendDocument => "Send-Document",
        SendUri => "Send-URI",
        CancelJob => "Cancel-Job",
        GetJobAttributes => "Get-Job-Attributes",
        GetJobs => "Get-Jobs",
        GetPrinterAttributes => "Get-Printer-Attributes",
        HoldJob => "Hold-Job",
        ReleaseJob => "Release-Job",
        RestartJob => "Restart-Job",
        PausePrinter => "Pause-Printer",
        ResumePrinter => "Resume-Printer",
        PurgeJobs => "Purge-Jobs",
        _ => $"0x{operation:X4}",
    };
}

public static class IppStatus
{
    public const ushort SuccessfulOk = 0x0000;
    public const ushort SuccessfulOkIgnoredOrSubstitutedAttributes = 0x0001;
    public const ushort ClientErrorBadRequest = 0x0400;
    public const ushort ClientErrorForbidden = 0x0401;
    public const ushort ClientErrorNotAuthenticated = 0x0402;
    public const ushort ClientErrorNotAuthorized = 0x0403;
    public const ushort ClientErrorNotPossible = 0x0404;
    public const ushort ClientErrorTimeout = 0x0405;
    public const ushort ClientErrorNotFound = 0x0406;
    public const ushort ClientErrorGone = 0x0407;
    public const ushort ClientErrorRequestEntityTooLarge = 0x0408;
    public const ushort ClientErrorRequestValueTooLong = 0x0409;
    public const ushort ClientErrorDocumentFormatError = 0x040A;
    public const ushort ClientErrorDocumentFormatNotSupported = 0x040B;
    public const ushort ClientErrorAttributesOrValuesNotSupported = 0x040C;
    public const ushort ClientErrorUriSchemeNotSupported = 0x040D;
    public const ushort ClientErrorCharsetNotSupported = 0x040E;
    public const ushort ClientErrorConflictingAttributes = 0x040F;
    public const ushort ClientErrorCompressionNotSupported = 0x0410;
    public const ushort ClientErrorCompressionError = 0x0411;
    public const ushort ClientErrorDocumentAccessError = 0x0412;
    public const ushort ClientErrorDocumentPasswordError = 0x0413;
    public const ushort ServerErrorInternalError = 0x0500;
    public const ushort ServerErrorOperationNotSupported = 0x0501;
    public const ushort ServerErrorServiceUnavailable = 0x0502;
    public const ushort ServerErrorVersionNotSupported = 0x0503;
    public const ushort ServerErrorDeviceError = 0x0504;
    public const ushort ServerErrorTemporaryError = 0x0505;
    public const ushort ServerErrorNotAcceptingJobs = 0x0506;
    public const ushort ServerErrorBusy = 0x0507;
    public const ushort ServerErrorJobCanceled = 0x0508;
    public const ushort ServerErrorMultipleDocumentJobsNotSupported = 0x0509;

    public static bool IsSuccess(ushort status) => status < 0x0100;

    public static string GetName(ushort status) => status switch
    {
        SuccessfulOk => "successful-ok",
        SuccessfulOkIgnoredOrSubstitutedAttributes => "successful-ok-ignored-or-substituted-attributes",
        ClientErrorBadRequest => "client-error-bad-request",
        ClientErrorForbidden => "client-error-forbidden",
        ClientErrorNotAuthenticated => "client-error-not-authenticated",
        ClientErrorNotAuthorized => "client-error-not-authorized",
        ClientErrorNotPossible => "client-error-not-possible",
        ClientErrorTimeout => "client-error-timeout",
        ClientErrorNotFound => "client-error-not-found",
        ClientErrorGone => "client-error-gone",
        ClientErrorRequestEntityTooLarge => "client-error-request-entity-too-large",
        ClientErrorRequestValueTooLong => "client-error-request-value-too-long",
        ClientErrorDocumentFormatError => "client-error-document-format-error",
        ClientErrorDocumentFormatNotSupported => "client-error-document-format-not-supported",
        ClientErrorAttributesOrValuesNotSupported => "client-error-attributes-or-values-not-supported",
        ClientErrorUriSchemeNotSupported => "client-error-uri-scheme-not-supported",
        ClientErrorCharsetNotSupported => "client-error-charset-not-supported",
        ClientErrorConflictingAttributes => "client-error-conflicting-attributes",
        ClientErrorCompressionNotSupported => "client-error-compression-not-supported",
        ClientErrorCompressionError => "client-error-compression-error",
        ClientErrorDocumentAccessError => "client-error-document-access-error",
        ClientErrorDocumentPasswordError => "client-error-document-password-error",
        ServerErrorInternalError => "server-error-internal-error",
        ServerErrorOperationNotSupported => "server-error-operation-not-supported",
        ServerErrorServiceUnavailable => "server-error-service-unavailable",
        ServerErrorVersionNotSupported => "server-error-version-not-supported",
        ServerErrorDeviceError => "server-error-device-error",
        ServerErrorTemporaryError => "server-error-temporary-error",
        ServerErrorNotAcceptingJobs => "server-error-not-accepting-jobs",
        ServerErrorBusy => "server-error-busy",
        ServerErrorJobCanceled => "server-error-job-canceled",
        ServerErrorMultipleDocumentJobsNotSupported => "server-error-multiple-document-jobs-not-supported",
        _ => $"0x{status:X4}",
    };
}

public static class IppJobState
{
    public const int Pending = 3;
    public const int PendingHeld = 4;
    public const int Processing = 5;
    public const int ProcessingStopped = 6;
    public const int Canceled = 7;
    public const int Aborted = 8;
    public const int Completed = 9;
}
