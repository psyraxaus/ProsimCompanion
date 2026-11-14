using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Scenarios;

/// <summary>One failure of ProSim's live list, as the SDK's <c>Failure</c> object describes it
/// (<c>name</c>, <c>description</c>, <c>domain</c> such as "Electric", <c>groupName</c> /
/// <c>groupMember</c> such as "Engine failure" / "Engine 1", <c>failureClass</c>). Only the
/// name is certain; the rest is null when the source had it not.</summary>
public sealed record ProsimFailureInfo(
    string Name,
    string? Description = null,
    string? Domain = null,
    string? GroupName = null,
    string? GroupMember = null,
    string? FailureClass = null);

/// <summary>The document written to <c>prosim-failures.json</c>.</summary>
/// <param name="ListedUtc">When the list was read from ProSim.</param>
/// <param name="ProsimVersion">ProSim's own version string (<c>system.version</c>) when the
/// setup check had read it; null otherwise.</param>
/// <param name="Count">The number of failures (the length of <paramref name="Failures"/>).</param>
/// <param name="Failures">ProSim's list, in ProSim's order.</param>
public sealed record ProsimFailureListDocument(
    DateTimeOffset ListedUtc,
    string? ProsimVersion,
    int Count,
    IReadOnlyList<ProsimFailureInfo> Failures);

/// <summary>
/// ProSim's live failure list on disk (issue #197). The scenario catalogue's
/// <c>failureName</c>s are only knowable live (ADR-0018): on the owner's rig ProSim 1.76b5
/// listed 691 failures and none of the 19 seeded names was among them, and the names
/// themselves were in no file — the log and the <c>scenario.failuresListed</c> event carry the
/// count and the unknown catalogue names only. Each successful read of the list overwrites
/// this file under the app's data root, and the diagnostics bundle carries it, so the catalogue
/// can be mapped offline. Read-only knowledge: nothing here arms anything, and the gate that
/// refuses a name outside the live list is untouched.
/// </summary>
public sealed class ProsimFailureListFile
{
    /// <summary>The file's name, on disk and inside the diagnostics bundle.</summary>
    public const string FileName = "prosim-failures.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        // Readable names and descriptions ("é", "→") instead of \u escapes; the file is data
        // for people and the reducer, never markup.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ILogger<ProsimFailureListFile> _logger;
    private readonly object _io = new();

    /// <param name="path">Where the file lives; null = <c>prosim-failures.json</c> under
    /// <see cref="UserDataPaths.Root"/>. Tests pass a temp path.</param>
    public ProsimFailureListFile(ILogger<ProsimFailureListFile> logger, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        Path = string.IsNullOrWhiteSpace(path) ? System.IO.Path.Combine(UserDataPaths.Root, FileName) : path;
    }

    /// <summary>The file's full path (it may not exist yet).</summary>
    public string Path { get; }

    /// <summary>The document for one read of the list: entries without a name are dropped,
    /// blank details become null, ProSim's order is kept.</summary>
    public static ProsimFailureListDocument Build(IEnumerable<ProsimFailureInfo> failures, string? prosimVersion, DateTimeOffset listedUtc)
    {
        ArgumentNullException.ThrowIfNull(failures);
        var list = failures
            .Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Name))
            .Select(f => new ProsimFailureInfo(
                f.Name.Trim(), Clean(f.Description), Clean(f.Domain), Clean(f.GroupName), Clean(f.GroupMember), Clean(f.FailureClass)))
            .ToList();
        return new ProsimFailureListDocument(listedUtc.ToUniversalTime(), Clean(prosimVersion), list.Count, list);
    }

    /// <summary>The document as the file holds it (camelCase, indented, nulls left out).</summary>
    public static string Serialize(ProsimFailureListDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, Json);
    }

    /// <summary>Overwrites the file atomically (temp, then move). Never throws: a failed write
    /// costs the offline list, nothing of the flight. False when it could not be written.</summary>
    public bool Write(ProsimFailureListDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var tmp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            lock (_io)
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(tmp, Serialize(document));
                File.Move(tmp, Path, overwrite: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Could not write the ProSim failure list {Path}", Path);
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(cleanupEx, "Could not delete {Path}", tmp);
            }

            return false;
        }
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
