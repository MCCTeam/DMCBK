using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Lexer edges.
/// </summary>
public sealed class LexerEdgeTests : IDisposable
{
    public void Dispose() => ScriptTestHelpers.Reset();

    [Fact]
    public void Lex_EmptySource_YieldsOnlyEof()
    {
        BeaconLexResult lexed = BeaconLexer.Lex("e.bcn", string.Empty);
        Assert.Single(lexed.Tokens);
        Assert.Equal(BeaconTokenKind.EndOfFile, lexed.Tokens[0].Kind);
    }

    [Fact]
    public void Lex_UnicodeIdentifier_Preserved()
    {
        BeaconLexResult lexed = BeaconLexer.Lex("u.bcn", "# beacon 1\nshow \"caf\u00E9 \u263A\"\n");
        Assert.DoesNotContain(lexed.Diagnostics, d => d.Severity == BeaconSeverity.Error);
        BeaconToken text = Assert.Single(lexed.Tokens, t => t.Kind == BeaconTokenKind.Text);
        Assert.Contains("caf\u00E9", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Lex_Crlf_ParsesWithoutError()
    {
        BeaconLexResult lexed = BeaconLexer.Lex("c.bcn", "# beacon 1\r\nshow 1\r\n");
        Assert.DoesNotContain(lexed.Diagnostics, d => d.Severity == BeaconSeverity.Error);
        Assert.Contains(lexed.Tokens, t => t.Kind == BeaconTokenKind.Number);
        BeaconFormatResult formatted = BeaconFormat.FormatSource("c.bcn", "# beacon 1\r\nshow 1\r\n");
        Assert.DoesNotContain("\r", formatted.Formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void Lex_Https_NotComment()
    {
        BeaconLexResult lexed = BeaconLexer.Lex("h.bcn", "# beacon 1\nshow \"https://example.com\"\n");
        Assert.DoesNotContain(lexed.Diagnostics, d => d.Severity == BeaconSeverity.Error);
    }

    [Fact]
    public void Lex_UnclosedBlockComment_WarnsOnce()
    {
        BeaconLexResult lexed = BeaconLexer.Lex("b.bcn", "# beacon 1\n/* never closed\nshow 1\n");
        Assert.Single(lexed.Diagnostics, d => d.Code == BeaconDiagnosticCodes.UnclosedBlockComment);
    }

    [Fact]
    public void Lex_LeadingSlash_Errors()
    {
        BeaconLexResult lexed = BeaconLexer.Lex("s.bcn", "# beacon 1\n/home\n");
        Assert.Contains(lexed.Diagnostics, d => d.Code == BeaconDiagnosticCodes.StrictLeadingSlash);
    }

    [Fact]
    public void Lex_Equals_Errors()
    {
        BeaconLexResult lexed = BeaconLexer.Lex("e.bcn", "# beacon 1\nset x = 5\n");
        Assert.Contains(lexed.Diagnostics, d => d.Code == BeaconDiagnosticCodes.StrictEquals);
    }

    [Fact]
    public void Lex_UnclosedString_Errors()
    {
        BeaconLexResult lexed = BeaconLexer.Lex("u.bcn", "# beacon 1\nshow \"oops\n");
        Assert.Contains(lexed.Diagnostics, d => d.Severity == BeaconSeverity.Error);
    }

    [Fact]
    public void Lex_SlashSlash_AfterWhitespace_IsComment()
    {
        BeaconLexResult lexed = BeaconLexer.Lex("c.bcn", "# beacon 1\nshow 1 // trailing note\n");
        Assert.DoesNotContain(lexed.Diagnostics, d => d.Severity == BeaconSeverity.Error);
    }

    [Fact]
    public void Lex_DeepNesting_Parses()
    {
        string expr = "1";
        for (int i = 0; i < 20; i++)
            expr = "(" + expr + " + 1)";

        BeaconLexResult lexed = BeaconLexer.Lex("d.bcn", "# beacon 1\nshow " + expr + "\n");
        Assert.DoesNotContain(lexed.Diagnostics, d => d.Severity == BeaconSeverity.Error);
        BeaconParseResult parsed = BeaconParser.Parse("d.bcn", lexed.Tokens);
        Assert.DoesNotContain(parsed.Diagnostics, d => d.Severity == BeaconSeverity.Error);
    }

    [Fact]
    public void Lex_HugeInput_LexesWithoutError()
    {
        string body = string.Concat(Enumerable.Repeat("show 1\n", 5000));
        BeaconLexResult lexed = BeaconLexer.Lex("h.bcn", "# beacon 1\n" + body);
        Assert.DoesNotContain(lexed.Diagnostics, d => d.Severity == BeaconSeverity.Error);
        Assert.True(lexed.Tokens.Count > 5000);
    }

    [Fact]
    public void Lex_RegexVsDivision_Disambiguated()
    {
        BeaconLexResult div = BeaconLexer.Lex("d.bcn", "# beacon 1\nshow 6 / 2\n");
        Assert.DoesNotContain(div.Tokens, t => t.Kind == BeaconTokenKind.Regex);
        BeaconLexResult rx = BeaconLexer.Lex("r.bcn", "# beacon 1\nshow (\"aa\" matches /a+/)\n");
        Assert.Contains(rx.Tokens, t => t.Kind == BeaconTokenKind.Regex);
    }
}
