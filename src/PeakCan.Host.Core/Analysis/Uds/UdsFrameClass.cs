namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// Mutually exclusive bucket for every input frame. The five buckets must
/// always sum to the raw input count — that closed equation is what makes a
/// partially-readable log trustworthy instead of silently lossy.
/// </summary>
public enum UdsFrameClass
{
    /// <summary>Address-matched classic CAN frame with a parseable ISO-TP PCI.</summary>
    MatchedDiagnostic,
    /// <summary>Well-formed frame on another id/channel — counted only, never stored as evidence.</summary>
    NonDiagnostic,
    /// <summary>Address-matched but unusable (bad PCI, orphan CF, failed response check…).</summary>
    Noise,
    /// <summary>Not a classic CAN data frame (CAN FD, RTR, error frame).</summary>
    UnsupportedInput,
}

/// <summary>Which side of the diagnostic pair a matched frame belongs to.</summary>
public enum UdsDirectionRole
{
    Request,
    Response,
}

/// <summary>Why an address-matched frame could not become a valid PDU.</summary>
public enum UdsNoiseReason
{
    /// <summary>PCI nibble is unknown or its declared length is impossible.</summary>
    InvalidPci,
    /// <summary>Frame carries no data at all.</summary>
    InsufficientDlc,
    /// <summary>Consecutive frame arrived with the wrong sequence number.</summary>
    SequenceError,
    /// <summary>Consecutive frame with no first frame in flight (log started mid-transfer).</summary>
    OrphanConsecutiveFrame,
    /// <summary>Response did not echo the request (DID / sub-function / routine id / block seq).</summary>
    ResponseValidationFailed,
    /// <summary>Frame is not a classic CAN data frame.</summary>
    UnsupportedFrameType,
}

/// <summary>Classification of one input frame, keyed by its stable evidence index.</summary>
public sealed record UdsFrameClassification(
    int EvidenceIndex,
    UdsFrameClass Class,
    UdsNoiseReason? NoiseReason = null,
    UdsDirectionRole? Role = null);
