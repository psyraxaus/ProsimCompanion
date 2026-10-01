using System.Text.RegularExpressions;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>
/// Guard against the 2026-09-28 /speech crash (ticket t-20260929-1933, build 0.5.0-rc.9): a
/// component attribute written as <c>Hint="\"Gear down\" …"</c>. Razor has no backslash escape
/// inside a double-quoted attribute, so the value ended at the first inner quote and the rest
/// of the text became further attributes — <c>Gear</c> was passed as a parameter to
/// <c>NumberField&lt;int&gt;</c>, and the whole page threw on every render. A compiler never
/// sees it (the attribute splat is legal Razor), so this source scan is the only cheap gate.
/// Use single quotes around the attribute or <c>&amp;quot;</c> instead.
/// </summary>
public sealed partial class RazorAttributeEscapeGuardTests
{
    [Fact]
    public void NoRazorAttribute_UsesABackslashEscapedQuote()
    {
        var offenders = new List<string>();
        foreach (var (absolute, relative) in RazorFiles())
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(absolute))
            {
                lineNumber++;
                if (EscapedQuoteAttribute().IsMatch(line))
                {
                    offenders.Add($"{relative}:{lineNumber}: {line.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Razor attributes cannot escape a quote with a backslash — the value ends at the inner quote and the "
            + "remainder becomes stray parameters (the /speech 'Gear' render crash). Use single quotes or &quot;:\n"
            + string.Join('\n', offenders));
    }

    private static IEnumerable<(string Absolute, string Relative)> RazorFiles()
    {
        var root = RepoRoot();
        return Directory
            .EnumerateFiles(Path.Combine(root, "src", "ProsimCompanion.Web"), "*.razor", SearchOption.AllDirectories)
            .Select(path => (path, Path.GetRelativePath(root, path)));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProsimCompanion.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent!;
        }

        throw new InvalidOperationException("Repo root (ProsimCompanion.slnx) not found above the test assembly.");
    }

    /// <summary>An attribute whose double-quoted value starts with a backslash-escaped quote.</summary>
    [GeneratedRegex(@"[A-Za-z_][\w\-]*\s*=\s*""\\""")]
    private static partial Regex EscapedQuoteAttribute();
}
