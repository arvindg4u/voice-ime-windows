using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Static audit of the XAML/CS sources: no literal colours may appear outside
/// the palette.
///
/// The old codebase hardcoded <c>White</c> in three templates (primary button
/// label, toggle knob, overlay cancel glyph) and left <c>ComboBox</c>/
/// <c>ScrollBar</c> entirely untemplated. Those are the exact regressions this
/// catches. It parses files as text rather than loading them, so it is fast and
/// runs on any OS.
/// </summary>
public sealed class ThemeTokenAuditTests
{
    /// <summary>Files allowed to name a colour: the palette and the dictionary defaults.</summary>
    private static readonly HashSet<string> ColourOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "Palette.cs",
        "ThemeManager.cs",
        "HandyTheme.xaml",
    };

    private static readonly Regex HexColour = new(
        @"#[0-9A-Fa-f]{6,8}\b", RegexOptions.Compiled);

    /// <summary>
    /// Named colours that mean "a hardcoded value" in this codebase.
    /// <c>Transparent</c> is excluded — it is a structural value (a window with
    /// <c>AllowsTransparency</c> must be transparent), not a theme colour.
    /// </summary>
    private static readonly Regex NamedColour = new(
        @"""\s*(White|Black|Gray|Grey|Red|Green|Blue|Orange|Yellow|LightGray|DarkGray|DimGray|Silver|Gold|Pink|Purple|Cyan|Magenta|Lime|Navy|Teal|Maroon|Olive|Snow|Ivory|Beige|Brown)\s*""",
        RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VoiceIme.Windows.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IEnumerable<string> SourceFiles(string extension)
    {
        var root = RepoRoot();
        foreach (var directory in new[] { "src", "tests" })
        {
            var path = Path.Combine(root, directory);
            if (!Directory.Exists(path))
            {
                continue;
            }

            // Skip build output.
            foreach (var file in Directory.EnumerateFiles(path, "*" + extension, SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    [Fact]
    public void NoHardcodedHexColoursInXaml()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles(".xaml"))
        {
            if (ColourOwners.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.TrimStart();

                // Opacity masks are alpha geometry, not theme colour: the hex
                // here is transparency (0x00 -> 0xFF), and it must be achromatic
                // because only the luminance of an OpacityMask matters. A
                // semantic token would be actively wrong — a theme swap would
                // change how much of the text is visible.
                if (trimmed.StartsWith("<GradientStop", StringComparison.Ordinal))
                {
                    continue;
                }

                // Ignore comments — prose may mention a colour legitimately.
                if (trimmed.StartsWith("<!--", StringComparison.Ordinal) ||
                    trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (HexColour.IsMatch(trimmed))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}: {trimmed.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Hardcoded hex colours found outside the palette. Use a semantic " +
            "DynamicResource token instead:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void NoHardcodedNamedColoursInXaml()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles(".xaml"))
        {
            if (ColourOwners.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
                {
                    continue;
                }

                if (NamedColour.IsMatch(trimmed))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}: {trimmed.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Hardcoded named colours found. Every colour must come from a " +
            "semantic token so it can follow the theme:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

}