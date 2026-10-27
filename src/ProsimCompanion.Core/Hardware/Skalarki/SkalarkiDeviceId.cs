namespace ProsimCompanion.Core.Hardware.Skalarki;

/// <summary>
/// The Skalarki boards the Profiler reports, mirroring the SDK's <c>DeviceType</c> enum value
/// for value (1 = GLARE … 26 = DRAIMS2) so the SDK bridge converts by cast and Core and Web
/// never see an SDK type (docs/integrations/skalarki.md). The SDK's trailing <c>Last</c>
/// sentinel is deliberately absent.
/// </summary>
public enum SkalarkiDeviceId
{
    Glare = 1,
    Pedestal = 2,
    Overhead = 3,
    Mcdu1 = 4,
    Mcdu2 = 5,
    Md80Mcp = 6,
    Tq = 7,
    Mip = 8,
    Clock = 9,
    Ecam = 10,
    LowerPed = 11,
    Atc = 12,
    Rmp1 = 13,
    Rmp2 = 14,
    Rmp3 = 15,
    Acp1 = 16,
    Acp2 = 17,
    Acp3 = 18,
    Refuel = 19,
    PwrCtrl = 20,
    CbCtrl = 21,
    DcduCs = 22,
    DcduFo = 23,
    Mcdu3 = 24,
    Draims1 = 25,
    Draims2 = 26,
}

/// <summary>Which descriptor catalogue the SDK bridge registers for the enabled boards: the
/// SDK ships one per cockpit family (A32X is the plain <c>Switches</c>/<c>Encoders</c> set).</summary>
public enum SkalarkiCatalogue
{
    A32X,
    A33X,
    B73X,
    B77X,
}

/// <summary>Names and labels for <see cref="SkalarkiDeviceId"/>.</summary>
public static class SkalarkiDevices
{
    /// <summary>Every board id in SDK order.</summary>
    public static IReadOnlyList<SkalarkiDeviceId> All { get; } = Enum.GetValues<SkalarkiDeviceId>();

    /// <summary>The lower-snake node id the diagnostics page uses (<c>mcdu1</c>, <c>rmp2</c>,
    /// <c>lower_ped</c>): the enum name split at its capital-letter boundaries.</summary>
    public static string NodeId(SkalarkiDeviceId device)
    {
        var name = device.ToString();
        var text = new System.Text.StringBuilder(name.Length + 2);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c))
            {
                text.Append('_');
            }

            text.Append(char.ToLowerInvariant(c));
        }

        return text.ToString();
    }

    /// <summary>The SDK's own capital name for a board (<c>LOWERPED</c>, <c>DCDUCS</c>), which is
    /// what the Profiler shows and what the layout file names boards by.</summary>
    public static string SdkName(SkalarkiDeviceId device) => device.ToString().ToUpperInvariant();

    /// <summary>The short avionics label the page prints when the user has not renamed the
    /// board: <c>MCDU 1</c>, <c>RMP 2</c>, <c>LOWER PED</c>, <c>DCDU CS</c>.</summary>
    public static string DefaultLabel(SkalarkiDeviceId device) => device switch
    {
        SkalarkiDeviceId.Glare => "GLARE",
        SkalarkiDeviceId.Pedestal => "PEDESTAL",
        SkalarkiDeviceId.Overhead => "OVERHEAD",
        SkalarkiDeviceId.Md80Mcp => "MD80 MCP",
        SkalarkiDeviceId.LowerPed => "LOWER PED",
        SkalarkiDeviceId.PwrCtrl => "PWR CTRL",
        SkalarkiDeviceId.CbCtrl => "CB CTRL",
        SkalarkiDeviceId.DcduCs => "DCDU CS",
        SkalarkiDeviceId.DcduFo => "DCDU FO",
        _ => SplitDigits(device.ToString().ToUpperInvariant()),
    };

    /// <summary>Parses a board name as the SDK spells it (<c>MCDU1</c>, case-insensitive) or
    /// as the enum spells it (<c>Mcdu1</c>).</summary>
    public static bool TryParse(string? text, out SkalarkiDeviceId device)
    {
        device = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var wanted = text.Trim().Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        foreach (var candidate in All)
        {
            if (candidate.ToString().Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                device = candidate;
                return true;
            }
        }

        return false;
    }

    private static string SplitDigits(string upper)
    {
        var cut = upper.IndexOfAny(['1', '2', '3']);
        return cut > 0 ? upper[..cut] + " " + upper[cut..] : upper;
    }
}
