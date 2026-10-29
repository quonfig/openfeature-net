using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Quonfig.OpenFeature.ServerProvider.Tests;

/// <summary>
/// Pins <see cref="IsoDurationText"/> to the shared duration grammar fixture,
/// integration-test-data <c>tests/duration/grammar.yaml</c> (every <c>valid</c> value accepted,
/// every <c>invalid</c> value rejected).
/// </summary>
public sealed class IsoDurationTextTests
{
    [Fact]
    public void AcceptsEveryValidGrammarValue()
    {
        var (valid, _) = LoadGrammar();
        valid.Should().NotBeEmpty();
        foreach (var value in valid)
        {
            IsoDurationText.IsValid(value).Should().BeTrue("\"{0}\" is in the grammar's valid list", value);
        }
    }

    [Fact]
    public void RejectsEveryInvalidGrammarValue()
    {
        var (_, invalid) = LoadGrammar();
        invalid.Should().NotBeEmpty();
        foreach (var value in invalid)
        {
            IsoDurationText.IsValid(value).Should().BeFalse("\"{0}\" is in the grammar's invalid list", Regex.Escape(value));
        }
    }

    [Fact]
    public void RejectsNull() => IsoDurationText.IsValid(null).Should().BeFalse();

    /// <summary>Reads the fixture's two lists. Its shape is fixed: <c>- { value: "...", millis: N }</c>
    /// under <c>valid:</c> and <c>- "..."</c> under <c>invalid:</c>, with YAML double-quoted escapes.</summary>
    private static (List<string> Valid, List<string> Invalid) LoadGrammar()
    {
        var valid = new List<string>();
        var invalid = new List<string>();
        List<string>? current = null;
        foreach (var line in File.ReadAllLines(GrammarPath()))
        {
            if (line.StartsWith("valid:", System.StringComparison.Ordinal))
            {
                current = valid;
                continue;
            }

            if (line.StartsWith("invalid:", System.StringComparison.Ordinal))
            {
                current = invalid;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            var match = Regex.Match(line, "^\\s*-\\s*(?:\\{\\s*value:\\s*)?\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (match.Success)
            {
                current.Add(Regex.Unescape(match.Groups[1].Value));
            }
        }

        return (valid, invalid);
    }

    private static string GrammarPath()
    {
        var dir = System.AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, "integration-test-data", "tests", "duration", "grammar.yaml"));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir)
            {
                break;
            }

            dir = parent;
        }

        throw new FileNotFoundException("could not locate integration-test-data/tests/duration/grammar.yaml by walking up from " + System.AppContext.BaseDirectory);
    }
}
