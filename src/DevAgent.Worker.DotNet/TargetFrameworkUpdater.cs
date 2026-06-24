namespace DevAgent.Worker.DotNet;

using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>Outcome of attempting to upgrade target frameworks in a workspace.</summary>
public sealed record TargetFrameworkUpdateOutcome(bool Changed, int ProjectsUpdated, string Message);

/// <summary>
/// Deterministically upgrades the .NET target framework across every
/// <c>.csproj</c> in a workspace. This is the NON-LLM execution engine behind a
/// "DotNetUpgrade" job: pure XML editing, no model involved.
///
/// It understands both single-target (<c>&lt;TargetFramework&gt;</c>) and
/// multi-targeting (<c>&lt;TargetFrameworks&gt;</c>) projects. Only modern
/// SDK-style frameworks of the form <c>net&lt;major&gt;.&lt;minor&gt;</c>
/// (net5.0+) are touched; <c>netstandard*</c> and legacy <c>net4x</c> targets
/// are deliberately left alone, and (by default) a project already on a newer
/// framework is never downgraded.
///
/// SECURITY: Operates only on paths under the supplied workspace root and runs
/// no external command itself; the caller drives restore/build/test through the
/// SafeCommandRunner.
/// </summary>
public sealed class TargetFrameworkUpdater
{
    private static readonly Regex ModernNet = new(@"^net(\d+)\.(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly bool _onlyUpgrade;

    public TargetFrameworkUpdater(bool onlyUpgrade = true)
    {
        _onlyUpgrade = onlyUpgrade;
    }

    public TargetFrameworkUpdateOutcome UpdateInDirectory(string workspaceRoot, string targetFramework)
    {
        if (!ModernNet.IsMatch(targetFramework))
        {
            return new TargetFrameworkUpdateOutcome(false, 0,
                $"Target framework '{targetFramework}' is not a modern net<major>.<minor> moniker.");
        }

        if (!Directory.Exists(workspaceRoot))
        {
            return new TargetFrameworkUpdateOutcome(false, 0, $"Workspace '{workspaceRoot}' does not exist.");
        }

        var projectsUpdated = 0;
        foreach (var projectFile in Directory.EnumerateFiles(workspaceRoot, "*.csproj", SearchOption.AllDirectories))
        {
            if (TryUpdateProject(projectFile, targetFramework))
            {
                projectsUpdated++;
            }
        }

        return projectsUpdated > 0
            ? new TargetFrameworkUpdateOutcome(true, projectsUpdated, $"Upgraded {projectsUpdated} project(s) to {targetFramework}.")
            : new TargetFrameworkUpdateOutcome(false, 0, $"No project required upgrading to {targetFramework}.");
    }

    private bool TryUpdateProject(string projectFile, string targetFramework)
    {
        var doc = XDocument.Load(projectFile, LoadOptions.PreserveWhitespace);
        var changed = false;

        foreach (var element in doc.Descendants().Where(e => e.Name.LocalName == "TargetFramework"))
        {
            if (ShouldUpgrade(element.Value.Trim(), targetFramework))
            {
                element.Value = targetFramework;
                changed = true;
            }
        }

        foreach (var element in doc.Descendants().Where(e => e.Name.LocalName == "TargetFrameworks"))
        {
            var rewritten = RewriteMultiTarget(element.Value, targetFramework, out var multiChanged);
            if (multiChanged)
            {
                element.Value = rewritten;
                changed = true;
            }
        }

        if (changed)
        {
            doc.Save(projectFile);
        }

        return changed;
    }

    /// <summary>Rewrite a ';'-separated TargetFrameworks list, upgrading only modern-net entries.</summary>
    private string RewriteMultiTarget(string value, string targetFramework, out bool changed)
    {
        changed = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var raw in value.Split(';'))
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            var resolved = ShouldUpgrade(token, targetFramework) ? targetFramework : token;
            if (!ReferenceEquals(resolved, token) && !string.Equals(resolved, token, StringComparison.Ordinal))
            {
                changed = true;
            }

            // De-duplicate (e.g. net6.0;net8.0 upgrading net6.0 -> a single net8.0).
            if (seen.Add(resolved))
            {
                result.Add(resolved);
            }
            else
            {
                changed = true;
            }
        }

        return string.Join(';', result);
    }

    private bool ShouldUpgrade(string current, string target)
    {
        if (!ModernNet.IsMatch(current))
        {
            // Leave netstandard*, net4x and anything non-modern untouched.
            return false;
        }

        var comparison = CompareTfm(current, target);
        return _onlyUpgrade ? comparison < 0 : comparison != 0;
    }

    /// <summary>Compare two modern net monikers numerically (net6.0 &lt; net8.0 &lt; net10.0).</summary>
    public static int CompareTfm(string a, string b)
    {
        var ma = ModernNet.Match(a);
        var mb = ModernNet.Match(b);
        if (!ma.Success || !mb.Success)
        {
            return string.CompareOrdinal(a, b);
        }

        var majorA = int.Parse(ma.Groups[1].Value);
        var majorB = int.Parse(mb.Groups[1].Value);
        if (majorA != majorB)
        {
            return majorA.CompareTo(majorB);
        }

        return int.Parse(ma.Groups[2].Value).CompareTo(int.Parse(mb.Groups[2].Value));
    }
}
