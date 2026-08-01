namespace ProsimCompanion.Gsx.Transport;

/// <summary>
/// Discovers the Couatl Remote API port from <c>%APPDATA%\Virtuali\CouatlAddons.ini</c>
/// (<c>[gsx] remote_server_port</c>). The ini is re-read on every connection attempt — never
/// cached — and the key is deliberately not configurable in-app (a stale persisted port once
/// caused a silent degradation in the predecessor). See docs/integrations/gsx-remote-api.md §1.
/// </summary>
public static class CouatlPortLocator
{
    public const int DefaultPort = 8744;
    private const string Section = "gsx";
    private const string Key = "remote_server_port";

    /// <summary>Reads the port from the standard ini location; <see cref="DefaultPort"/> on any
    /// failure (missing file/section/key, unparsable, out of range).</summary>
    public static int LocatePort()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Virtuali",
                "CouatlAddons.ini");
            return File.Exists(path) ? ParsePort(File.ReadAllText(path)) : DefaultPort;
        }
        catch (IOException)
        {
            return DefaultPort;
        }
        catch (UnauthorizedAccessException)
        {
            return DefaultPort;
        }
    }

    /// <summary>Pure ini parsing: case-insensitive section/key, key matched only inside
    /// <c>[gsx]</c>, <c>;</c>/<c>#</c> comment lines and trailing inline comments stripped,
    /// last-wins on duplicates, 1–65535 range enforced.</summary>
    public static int ParsePort(string iniText)
    {
        ArgumentNullException.ThrowIfNull(iniText);

        var inGsxSection = false;
        var port = DefaultPort;

        foreach (var rawLine in iniText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                var end = line.IndexOf(']', 1);
                inGsxSection = end > 1
                    && line[1..end].Trim().Equals(Section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inGsxSection)
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (!key.Equals(Key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(separator + 1)..];
            var comment = value.IndexOfAny([';', '#']);
            if (comment >= 0)
            {
                value = value[..comment];
            }

            if (int.TryParse(value.Trim(), out var parsed) && parsed is >= 1 and <= 65535)
            {
                port = parsed;
            }
        }

        return port;
    }
}
