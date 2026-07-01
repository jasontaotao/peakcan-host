namespace PeakCan.Host.Core.Uds;

/// <summary>
/// UDS diagnostic exception. Thrown on negative responses or errors.
/// </summary>
public class UdsException : Exception
{
    public UdsException(string message) : base(message) { }
    public UdsException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// UDS negative response exception. Thrown when ECU responds with NRC.
/// </summary>
public class UdsNegativeResponseException : UdsException
{
    /// <summary>Service ID that was rejected.</summary>
    public byte ServiceId { get; }

    /// <summary>Negative Response Code.</summary>
    public UdsNegativeResponseCode ResponseCode { get; }

    public UdsNegativeResponseException(byte serviceId, UdsNegativeResponseCode code)
        : base($"UDS negative response: Service 0x{serviceId:X2}, NRC 0x{(byte)code:X2} ({code})")
    {
        ServiceId = serviceId;
        ResponseCode = code;
    }
}

/// <summary>
/// ISO 14229 Negative Response Codes (NRC).
/// </summary>
public enum UdsNegativeResponseCode : byte
{
    /// <summary>General rejection.</summary>
    GeneralReject = 0x10,

    /// <summary>Service not supported.</summary>
    ServiceNotSupported = 0x11,

    /// <summary>Sub-function not supported.</summary>
    SubFunctionNotSupported = 0x12,

    /// <summary>Incorrect message length or invalid format.</summary>
    IncorrectMessageLength = 0x13,

    /// <summary>Response too long.</summary>
    ResponseTooLong = 0x14,

    /// <summary>Conditions not correct.</summary>
    ConditionsNotCorrect = 0x22,

    /// <summary>Request sequence error.</summary>
    RequestSequenceError = 0x24,

    /// <summary>No response from subnet component.</summary>
    NoResponseFromSubnet = 0x25,

    /// <summary>Failure prevents execution.</summary>
    FailurePreventsExecution = 0x26,

    /// <summary>Request out of range.</summary>
    RequestOutOfRange = 0x31,

    /// <summary>Security access denied.</summary>
    SecurityAccessDenied = 0x33,

    /// <summary>Invalid key.</summary>
    InvalidKey = 0x35,

    /// <summary>Exceeded number of attempts.</summary>
    ExceededNumberOfAttempts = 0x36,

    /// <summary>Required time delay not expired.</summary>
    RequiredTimeDelayNotExpired = 0x37,

    /// <summary>Upload/download not accepted.</summary>
    UploadDownloadNotAccepted = 0x70,

    /// <summary>Transfer data suspended.</summary>
    TransferDataSuspended = 0x71,

    /// <summary>General programming failure.</summary>
    GeneralProgrammingFailure = 0x72,

    /// <summary>Wrong block sequence counter.</summary>
    WrongBlockSequenceCounter = 0x73,

    /// <summary>Sub-function not supported in active session.</summary>
    SubFunctionNotSupportedInActiveSession = 0x7E,

    /// <summary>Service not supported in active session.</summary>
    ServiceNotSupportedInActiveSession = 0x7F
}
