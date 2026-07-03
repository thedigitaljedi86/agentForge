namespace DevAgent.Forge.Tools;

/// <summary>
/// Minimal, deterministic unified-diff helpers used by the patch + replace
/// tools. <see cref="Create"/> renders a whole-file diff for auditing;
/// <see cref="Apply"/> applies an LLM-supplied unified diff to file content.
///
/// This is intentionally small and explicit rather than relying on an external
/// "git apply" — keeping patch application in-process means it is fully bounded
/// by our own validation and has no shell dependency.
/// </summary>
public static class UnifiedDiff
{
    public sealed record ApplyResult(bool Success, string? NewContent, string? Error);

    /// <summary>Renders a whole-file unified diff (for audit/readability).</summary>
    public static string Create(string path, string before, string after)
    {
        if (before == after)
        {
            return string.Empty;
        }

        var oldLines = SplitLines(before);
        var newLines = SplitLines(after);

        var sb = new System.Text.StringBuilder();
        sb.Append("--- a/").Append(path).Append('\n');
        sb.Append("+++ b/").Append(path).Append('\n');
        sb.Append("@@ -1,").Append(oldLines.Count).Append(" +1,").Append(newLines.Count).Append(" @@\n");
        foreach (var line in oldLines) sb.Append('-').Append(line).Append('\n');
        foreach (var line in newLines) sb.Append('+').Append(line).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Applies a unified diff to <paramref name="original"/>. Supports multiple
    /// hunks with context (' '), removal ('-') and addition ('+') lines. Context
    /// and removed lines are verified against the source; a mismatch fails the
    /// apply rather than corrupting the file.
    /// </summary>
    public static ApplyResult Apply(string original, string unifiedDiff)
    {
        var src = SplitLines(original);
        var result = new List<string>();
        var cursor = 0; // index into src

        var diffLines = unifiedDiff.Replace("\r\n", "\n").Split('\n');
        var i = 0;

        while (i < diffLines.Length)
        {
            var line = diffLines[i];

            // Skip file headers / metadata.
            if (line.StartsWith("--- ", StringComparison.Ordinal) ||
                line.StartsWith("+++ ", StringComparison.Ordinal) ||
                line.StartsWith("diff ", StringComparison.Ordinal) ||
                line.StartsWith("index ", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            if (!line.StartsWith("@@", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            // Parse the hunk header: @@ -oldStart,oldLen +newStart,newLen @@
            if (!TryParseHunkHeader(line, out var oldStart))
            {
                return new ApplyResult(false, null, $"Malformed hunk header: '{line}'.");
            }

            // Copy unchanged lines up to the hunk start (1-indexed -> 0-indexed).
            var target = Math.Max(0, oldStart - 1);
            if (target > src.Count)
            {
                return new ApplyResult(false, null, $"Hunk starts beyond end of file (line {oldStart}).");
            }
            while (cursor < target)
            {
                result.Add(src[cursor++]);
            }

            i++; // move past the header into the hunk body

            while (i < diffLines.Length && !diffLines[i].StartsWith("@@", StringComparison.Ordinal))
            {
                var body = diffLines[i];

                // A trailing empty element from Split is treated as end-of-diff.
                if (body.Length == 0 && i == diffLines.Length - 1)
                {
                    i++;
                    break;
                }

                var marker = body.Length > 0 ? body[0] : ' ';
                var text = body.Length > 0 ? body[1..] : string.Empty;

                switch (marker)
                {
                    case ' ': // context — must match source
                        if (cursor >= src.Count || !LinesEqual(src[cursor], text))
                        {
                            return new ApplyResult(false, null,
                                $"Context mismatch at source line {cursor + 1}: expected '{text}'.");
                        }
                        result.Add(src[cursor++]);
                        break;

                    case '-': // removal — must match source, then drop it
                        if (cursor >= src.Count || !LinesEqual(src[cursor], text))
                        {
                            return new ApplyResult(false, null,
                                $"Removal mismatch at source line {cursor + 1}: expected '{text}'.");
                        }
                        cursor++;
                        break;

                    case '+': // addition
                        result.Add(text);
                        break;

                    case '\\': // "\ No newline at end of file" — ignore
                        break;

                    default:
                        return new ApplyResult(false, null, $"Unrecognised patch line: '{body}'.");
                }

                i++;
            }
        }

        // Copy any remaining source lines.
        while (cursor < src.Count)
        {
            result.Add(src[cursor++]);
        }

        return new ApplyResult(true, string.Join('\n', result), null);
    }

    private static bool TryParseHunkHeader(string header, out int oldStart)
    {
        oldStart = 0;
        // Expected form: @@ -a,b +c,d @@  (the ",b"/",d" may be omitted)
        var minus = header.IndexOf('-');
        if (minus < 0)
        {
            return false;
        }

        var rest = header[(minus + 1)..];
        var comma = rest.IndexOfAny(new[] { ',', ' ' });
        var numberText = comma < 0 ? rest : rest[..comma];
        return int.TryParse(numberText, out oldStart);
    }

    private static bool LinesEqual(string a, string b) =>
        a.TrimEnd('\r') == b.TrimEnd('\r');

    private static List<string> SplitLines(string content)
    {
        if (content.Length == 0)
        {
            return new List<string>();
        }

        var normalized = content.Replace("\r\n", "\n");
        var lines = normalized.Split('\n').ToList();

        // A trailing newline yields a final empty element; drop it so line
        // counts match human expectations.
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
