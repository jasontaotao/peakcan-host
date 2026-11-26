namespace PeakCan.Host.App.Services.Uds;

/// <summary>Whether the exported file is redacted, raw, or the user backed out.</summary>
public enum UdsExportChoice
{
    Redacted,
    Raw,
    Cancel,
}

/// <summary>
/// The three export decisions the user makes, isolated from WPF so the view
/// model stays testable: pick a path/format, confirm overwrite, choose redaction.
/// </summary>
public interface IUdsExportPrompt
{
    /// <summary>Returns null when the user cancels; otherwise the chosen path.</summary>
    string? ChooseSavePath(out bool isCsv);

    /// <summary>Only called when the target file already exists.</summary>
    bool ConfirmOverwrite(string path);

    UdsExportChoice AskRedaction();
}
