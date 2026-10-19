namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// What shape a settings text field expects. The web <c>TextField</c> checks its value against
/// the kind on every keystroke and shows the mismatch under the field (owner request
/// 2026-10-13). Plain <see cref="Text"/> is never checked. Lives in Core since the C3 field
/// descriptions (2026-10-19): the shape is declared on the option property, beside its default,
/// and the web layer only applies it.
/// </summary>
public enum TextFieldKind
{
    /// <summary>Anything goes.</summary>
    Text,

    /// <summary>A machine name or IP address with no scheme, port or path — the consumers
    /// build the URL themselves (<c>http://{host}:5000/graphql</c>), so a pasted
    /// <c>http://…</c> or <c>host:5000</c> produces a URL that throws.</summary>
    Host,

    /// <summary>An absolute http(s) URL. Consumers string-concatenate a path onto it.</summary>
    Url,

    /// <summary>An IPv4 address (the Wake-on-LAN broadcast target).</summary>
    IpAddress,

    /// <summary>A MAC address: six hex pairs with ':' or '-' separators.</summary>
    Mac,

    /// <summary>A file that should already exist on this PC (a DLL, a certificate, a key).
    /// A missing file is a warning, not a block — it may be installed later.</summary>
    FilePath,

    /// <summary>A folder that should already exist on this PC. Relative paths resolve under
    /// the install folder. Missing is a warning.</summary>
    FolderPath,

    /// <summary>A file or a folder that should already exist (the DFD setting takes
    /// either). Missing is a warning.</summary>
    FileOrFolderPath,

    /// <summary>A file the app writes (day state, tech log, logbook). Only its parent
    /// folder needs to exist; a missing folder is a warning.</summary>
    OutputFilePath,

    /// <summary>Folders separated by ';'. Each missing one is a warning.</summary>
    FolderList,

    /// <summary>A four-character ICAO airport code.</summary>
    Icao,

    /// <summary>A runway designator: 01–36 with an optional L/R/C.</summary>
    Runway,

    /// <summary>Positive whole numbers separated by commas.</summary>
    IntList,
}
