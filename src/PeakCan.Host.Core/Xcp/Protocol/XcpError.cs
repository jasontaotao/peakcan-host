namespace PeakCan.Host.Core.Xcp.Protocol;

/// <summary>
/// XCP 1.0 Part 2 Table 8 error codes（线上错误码钉死，spec §3 Protocol 命令清单涉及的码）。
/// </summary>
public enum XcpError : byte
{
    /// <summary>ERR_CMD_SYNCH (0x00) — command pending, synchronization required.</summary>
    CmdSynch = 0x00,

    /// <summary>ERR_CMD_BUSY (0x10) — slave busy processing previous command.</summary>
    CmdBusy = 0x10,

    /// <summary>ERR_DAQ_ACTIVE (0x11) — DAQ list running, operation rejected.</summary>
    DaqActive = 0x11,

    /// <summary>ERR_PGM_ACTIVE (0x12) — PGM mode active, operation rejected.</summary>
    PgmActive = 0x12,

    /// <summary>ERR_CMD_UNKNOWN (0x20) — unknown command.</summary>
    CmdUnknown = 0x20,

    /// <summary>ERR_CMD_INVALID (0x21) — command invalid in current session mode.</summary>
    CmdInvalid = 0x21,

    /// <summary>ERR_OUT_OF_RANGE (0x22) — parameter out of range.</summary>
    OutOfRange = 0x22,

    /// <summary>ERR_WRITE_PROTECTED (0x23) — write access to address is protected.</summary>
    WriteProtected = 0x23,

    /// <summary>ERR_ACCESS_LOCKED (0x24) — access locked, SEED&amp;KEY required.</summary>
    AccessLocked = 0x24,

    /// <summary>ERR_ACCESS_DENIED (0x25) — access denied.</summary>
    AccessDenied = 0x25,

    /// <summary>ERR_CAL_PAGE_ACTIVE (0x26) — operation conflicts with active CAL_PAGE.</summary>
    CalPageActive = 0x26,

    /// <summary>ERR_PAGE_NOT_EXIST (0x27) — requested page does not exist.</summary>
    PageNotExist = 0x27,

    /// <summary>ERR_GENERIC (0x28) — generic error.</summary>
    Generic = 0x28,

    /// <summary>ERR_CRC (0x29) — CRC error on block transfer.</summary>
    Crc = 0x29,

    /// <summary>ERR_SEQUENCE (0x2A) — command sequence error (e.g. UPLOAD without SET_MTA).</summary>
    Sequence = 0x2A,

    /// <summary>ERR_RESOURCE_TEMPORARY (0x2B) — temporary resource limitation.</summary>
    ResourceTemporary = 0x2B,
}
