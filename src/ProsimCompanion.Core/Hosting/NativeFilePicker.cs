using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ProsimCompanion.Core.Hosting;

/// <summary>Which Windows dialog a <see cref="NativePickRequest"/> wants.</summary>
public enum NativePickKind
{
    /// <summary>An existing file (Open dialog, file must exist).</summary>
    OpenFile,

    /// <summary>A file the app will write (Save dialog, no overwrite prompt — the app
    /// owns the file; the dialog only picks the place and name).</summary>
    SaveFile,

    /// <summary>A folder.</summary>
    Folder,
}

/// <summary>What the settings page asks the desktop shell to show (owner request
/// 2026-10-13, option 3: the Windows dialog, only for a browser on the sim PC itself).</summary>
/// <param name="Kind">Which dialog.</param>
/// <param name="Title">Dialog title — the field's label.</param>
/// <param name="Filter">Windows filter string (<c>Certificates (*.pfx)|*.pfx</c>); null for all files.</param>
/// <param name="InitialPath">The field's current value; its folder is where the dialog opens.</param>
/// <param name="DefaultFileName">Save dialog only: the suggested file name.</param>
/// <param name="StartIn">The field's expected folders (<see cref="ExpectedFolders"/>), tried in
/// order when <paramref name="InitialPath"/> gives no usable folder.</param>
public sealed record NativePickRequest(
    NativePickKind Kind,
    string Title,
    string? Filter,
    string? InitialPath,
    string? DefaultFileName,
    IReadOnlyList<string>? StartIn = null);

/// <summary>The dialog's outcome: a path, a cancel (both null), or a one-line problem for
/// the page to show (another dialog already open, no desktop app).</summary>
public sealed record NativePickResult(string? Path, string? Problem)
{
    public static NativePickResult Cancelled { get; } = new(null, null);

    public static NativePickResult Picked(string path) => new(path, null);

    public static NativePickResult Failed(string problem) => new(null, problem);
}

/// <summary>
/// The desktop shell's file/folder dialogs, offered to the web UI. Implemented by the WPF app
/// (the only project with a desktop); absent in tests and in any headless host, in which case
/// the page shows no Browse button (degrade, not fail). The dialog appears on the sim PC's
/// screen, so the page offers it only to a browser running on that PC
/// (<see cref="LocalClient"/>).
/// </summary>
public interface INativeFilePicker
{
    /// <summary>True when a desktop is up to show a dialog on.</summary>
    bool IsAvailable { get; }

    /// <summary>Shows the dialog and waits for the pilot. Never throws for a user-level
    /// problem — those come back in <see cref="NativePickResult.Problem"/>.</summary>
    Task<NativePickResult> PickAsync(NativePickRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Is the browser on this PC? Decided from the HTTP request's remote address: loopback, or
/// one of this PC's own interface addresses (the pilot may open the page through the LAN
/// address even at the sim PC). Pure for the comparison; the address list is a cached
/// snapshot of the NICs.
/// </summary>
public static class LocalClient
{
    private static readonly object Gate = new();
    private static IReadOnlyList<IPAddress> _hostAddresses = [];
    private static DateTimeOffset _hostAddressesAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    /// <summary>True when <paramref name="remote"/> is loopback or equals one of
    /// <paramref name="hostAddresses"/>. IPv4-mapped IPv6 addresses (<c>::ffff:a.b.c.d</c>,
    /// what Kestrel reports on a dual-stack socket) compare as their IPv4 form.</summary>
    public static bool IsLocal(IPAddress? remote, IEnumerable<IPAddress> hostAddresses)
    {
        ArgumentNullException.ThrowIfNull(hostAddresses);
        if (remote is null)
        {
            return false;
        }

        var normalized = Normalize(remote);
        if (IPAddress.IsLoopback(normalized))
        {
            return true;
        }

        return hostAddresses.Any(host => Normalize(host).Equals(normalized));
    }

    /// <summary>This PC's unicast addresses on every interface that is up, plus loopback.
    /// Cached for a short while — a page load must not enumerate the NICs every time.</summary>
    public static IReadOnlyList<IPAddress> HostAddresses()
    {
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _hostAddressesAt < CacheFor)
            {
                return _hostAddresses;
            }

            try
            {
                _hostAddresses = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                    .Select(unicast => unicast.Address)
                    .Append(IPAddress.Loopback)
                    .Append(IPAddress.IPv6Loopback)
                    .Distinct()
                    .ToList();
            }
            catch (NetworkInformationException)
            {
                _hostAddresses = [IPAddress.Loopback, IPAddress.IPv6Loopback];
            }

            _hostAddressesAt = now;
            return _hostAddresses;
        }
    }

    private static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        // A link-local IPv6 address carries a scope id on the host side but not always on the
        // request side; compare without it.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes())
            : address;
    }
}

/// <summary>Where a dialog should open: the value itself when it is a folder, its parent
/// folder when it is a file, else the first of the field's expected folders that exists, else
/// the fallback (the system drive — a folder Windows cannot open makes it pick its last-used
/// one, which reads as random). Pure apart from the Exists checks, after the
/// invalid-character gate (Snyk path rule).</summary>
public static class PickStart
{
    public static string DirectoryFor(string? value, string fallback)
        => DirectoryFor(value, [], fallback);

    public static string DirectoryFor(string? value, IEnumerable<string> expected, string fallback)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(fallback);
        return FromValue(value) ?? expected.FirstOrDefault(SafeDirectoryExists) ?? fallback;
    }

    private static string? FromValue(string? value)
    {
        var path = (value ?? "").Trim().Trim('"');
        if (path.Length == 0 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }

        try
        {
            if (Directory.Exists(path))
            {
                return path;
            }

            var parent = Path.GetDirectoryName(path);
            return !string.IsNullOrEmpty(parent) && Directory.Exists(parent) ? parent : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool SafeDirectoryExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return false;
        }

        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
