namespace DevAgent.Worker.DotNet;

using System.Xml.Linq;

/// <summary>Outcome of attempting to update a PackageReference.</summary>
public sealed record PackageUpdateOutcome(bool Changed, int FilesUpdated, string? Message);

/// <summary>
/// Deterministically updates &lt;PackageReference&gt; versions across the
/// project files in a workspace. This is the first-milestone, NON-LLM update
/// path: pure XML editing, no model involved.
///
/// SECURITY: Operates only on paths under the supplied workspace root. It does
/// not run any external command itself; the caller drives restore/build/test
/// through the SafeCommandRunner.
/// </summary>
public sealed class PackageReferenceUpdater
{
    private readonly bool _onlyUpgrade;

    public PackageReferenceUpdater(bool onlyUpgrade = true)
    {
        _onlyUpgrade = onlyUpgrade;
    }

    public PackageUpdateOutcome UpdateInDirectory(string workspaceRoot, string packageId, string targetVersion)
    {
        if (!Directory.Exists(workspaceRoot))
        {
            return new PackageUpdateOutcome(false, 0, $"Workspace '{workspaceRoot}' does not exist.");
        }

        var projectFiles = Directory.EnumerateFiles(workspaceRoot, "*.csproj", SearchOption.AllDirectories);
        var filesUpdated = 0;

        foreach (var projectFile in projectFiles)
        {
            if (TryUpdateProject(projectFile, packageId, targetVersion))
            {
                filesUpdated++;
            }
        }

        return filesUpdated > 0
            ? new PackageUpdateOutcome(true, filesUpdated, $"Updated {packageId} to {targetVersion} in {filesUpdated} project(s).")
            : new PackageUpdateOutcome(false, 0, $"No project referenced {packageId} at a version requiring change.");
    }

    private bool TryUpdateProject(string projectFile, string packageId, string targetVersion)
    {
        var doc = XDocument.Load(projectFile, LoadOptions.PreserveWhitespace);

        var references = doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference"
                        && string.Equals((string?)e.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase));

        var changed = false;

        foreach (var reference in references)
        {
            var current = GetVersion(reference);
            if (current is null)
            {
                continue;
            }

            if (_onlyUpgrade && CompareVersions(current, targetVersion) >= 0)
            {
                // Current is already >= target; never downgrade.
                continue;
            }

            SetVersion(reference, targetVersion);
            changed = true;
        }

        if (changed)
        {
            ProjectXml.Save(doc, projectFile);
        }

        return changed;
    }

    private static string? GetVersion(XElement reference)
    {
        var attr = reference.Attribute("Version");
        if (attr is not null)
        {
            return attr.Value;
        }

        var child = reference.Elements().FirstOrDefault(e => e.Name.LocalName == "Version");
        return child?.Value;
    }

    private static void SetVersion(XElement reference, string version)
    {
        if (reference.Attribute("Version") is { } attr)
        {
            attr.Value = version;
            return;
        }

        var child = reference.Elements().FirstOrDefault(e => e.Name.LocalName == "Version");
        if (child is not null)
        {
            child.Value = version;
            return;
        }

        // No version specified at all — add it as an attribute.
        reference.SetAttributeValue("Version", version);
    }

    /// <summary>
    /// Best-effort numeric version comparison ("1.2.10" &gt; "1.2.9"). Falls
    /// back to ordinal comparison when a part is non-numeric (e.g. prerelease).
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        if (Version.TryParse(StripPrerelease(a), out var va) && Version.TryParse(StripPrerelease(b), out var vb))
        {
            return va.CompareTo(vb);
        }

        return string.CompareOrdinal(a, b);
    }

    private static string StripPrerelease(string version)
    {
        var dash = version.IndexOf('-');
        return dash >= 0 ? version[..dash] : version;
    }
}
