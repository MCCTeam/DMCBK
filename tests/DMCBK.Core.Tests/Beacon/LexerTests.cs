using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Lexer contract: span-tracked tokens, interpolation holes, regex-versus-division, units, comments, strict lexical errors, and Discord-paste tolerance.
/// </summary>
public sealed class LexerTests
{
    private static IReadOnlyList<BeaconToken> LexTokens(string source)
    {
        BeaconLexResult result = BeaconLexer.Lex("test.mcc", source);
        return result.Tokens.Where(t => t.Kind != BeaconTokenKind.EndOfFile).ToList();
    }

    private static BeaconLexResult Lex(string source) => BeaconLexer.Lex("test.mcc", source);

    private static IReadOnlyList<BeaconDiagnostic> LexErrors(string source)
        => Lex(source).Diagnostics.Where(d => d.Severity == BeaconSeverity.Error).ToList();

    [Fact]
    public void Identifiers_LexCaseSensitive()
    {
        IReadOnlyList<BeaconToken> tokens = LexTokens("set name to Steve");

        Assert.Equal(
            ["set", "name", "to", "Steve"],
            tokens.Select(t => t.Text).ToList());
        Assert.All(tokens, t => Assert.NotEqual(BeaconTokenKind.EndOfFile, t.Kind));
        Assert.Equal(BeaconTokenKind.Keyword, tokens[0].Kind);
        Assert.Equal(BeaconTokenKind.Identifier, tokens[1].Kind);
        Assert.Equal(BeaconTokenKind.Identifier, tokens[3].Kind);
        Assert.Equal("Steve", tokens[3].Text);
    }

    [Fact]
    public void Identifiers_AllowDigitsAndUnderscoresAfterFirstLetter()
    {
        IReadOnlyList<BeaconToken> tokens = LexTokens("player1 warn_count");

        Assert.Equal(
            [BeaconTokenKind.Identifier, BeaconTokenKind.Identifier],
            tokens.Select(t => t.Kind).ToList());
    }

    [Fact]
    public void LeadingUnderscore_IsNotAnIdentifier()
    {
        IReadOnlyList<BeaconToken> tokens = LexTokens("_x");

        Assert.Equal(BeaconTokenKind.Symbol, tokens[0].Kind);
        Assert.Equal(BeaconTokenKind.Identifier, tokens[1].Kind);
    }

    [Theory]
    [InlineData("60", 60)]
    [InlineData("2.5", 2.5)]
    [InlineData("0", 0)]
    public void Numbers_LexWithValues(string text, double expected)
    {
        BeaconToken token = Assert.Single(LexTokens(text));

        Assert.Equal(BeaconTokenKind.Number, token.Kind);
        Assert.Equal(expected, token.NumberValue);
    }

    [Fact]
    public void Number_WithTrailingDot_StopsBeforeDot()
    {
        IReadOnlyList<BeaconToken> tokens = LexTokens("2.foo");

        Assert.Equal(BeaconTokenKind.Number, tokens[0].Kind);
        Assert.Equal(BeaconTokenKind.Symbol, tokens[1].Kind);
    }

    [Fact]
    public void Number_NoHexOrExponents()
    {
        // 0x1F is Number(0) then Ident(x1F); 1e5 is Number(1) then Ident(e5).
        IReadOnlyList<BeaconToken> hex = LexTokens("0x1F");
        Assert.Equal(BeaconTokenKind.Number, hex[0].Kind);
        Assert.Equal(BeaconTokenKind.Identifier, hex[1].Kind);

        IReadOnlyList<BeaconToken> exp = LexTokens("1e5");
        Assert.Equal(BeaconTokenKind.Number, exp[0].Kind);
        Assert.Equal(BeaconTokenKind.Identifier, exp[1].Kind);
    }

    [Fact]
    public void Text_LexesAsOneTokenWithQuotes()
    {
        BeaconToken token = Assert.Single(LexTokens("\"hello\""));

        Assert.Equal(BeaconTokenKind.Text, token.Kind);
        Assert.Equal("\"hello\"", token.Text);
    }

    [Fact]
    public void TripleText_LexesMultiline()
    {
        BeaconToken token = Assert.Single(LexTokens("\"\"\"line1\nline2\"\"\""));

        Assert.Equal(BeaconTokenKind.TripleText, token.Kind);
    }

    [Theory]
    [InlineData("is not")]
    [InlineData("is empty")]
    [InlineData("is not set")]
    [InlineData("starts with")]
    [InlineData("ends with")]
    [InlineData("for each")]
    [InlineData("else if")]
    [InlineData("stop event")]
    [InlineData("cancel event")]
    [InlineData("cancel task")]
    [InlineData("lock shared")]
    public void MultiwordKeywords_LexAsOneToken(string words)
    {
        BeaconToken token = Assert.Single(LexTokens(words));

        Assert.Equal(BeaconTokenKind.Keyword, token.Kind);
        Assert.Equal(words, token.Normalized);
    }

    [Theory]
    [InlineData("IS NOT", "is not")]
    [InlineData("For Each", "for each")]
    [InlineData("STOP EVENT", "stop event")]
    public void MultiwordKeywords_AreCaseInsensitive(string source, string normalized)
    {
        BeaconToken token = Assert.Single(LexTokens(source));

        Assert.Equal(BeaconTokenKind.Keyword, token.Kind);
        Assert.Equal(normalized, token.Normalized);
    }

    [Fact]
    public void MultiwordKeywords_RequireSingleSpaces()
    {
        IReadOnlyList<BeaconToken> tokens = LexTokens("is  not");

        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, t => Assert.Equal(BeaconTokenKind.Keyword, t.Kind));
        Assert.Equal("is", tokens[0].Normalized);
        Assert.Equal("not", tokens[1].Normalized);
    }

    [Fact]
    public void EndPlusLabel_AreTwoTokens()
    {
        IReadOnlyList<BeaconToken> tokens = LexTokens("end if");

        Assert.Equal(2, tokens.Count);
        Assert.Equal("end", tokens[0].Normalized);
        Assert.Equal("if", tokens[1].Normalized);
    }

    [Fact]
    public void Keywords_AreCaseInsensitive()
    {
        BeaconToken token = Assert.Single(LexTokens("SET"));

        Assert.Equal(BeaconTokenKind.Keyword, token.Kind);
        Assert.Equal("set", token.Normalized);
        Assert.Equal("SET", token.Text);
    }

    [Fact]
    public void Text_Hole_ProducesLiteralHoleLiteralParts()
    {
        BeaconToken token = Assert.Single(LexTokens("\"Welcome {player}!\""));

        Assert.Equal(3, token.Parts.Count);
        var first = Assert.IsType<BeaconTextLiteral>(token.Parts[0]);
        Assert.Equal("Welcome ", first.Value);
        var hole = Assert.IsType<BeaconTextHole>(token.Parts[1]);
        Assert.Equal("player", hole.Expression);
        var last = Assert.IsType<BeaconTextLiteral>(token.Parts[2]);
        Assert.Equal("!", last.Value);
    }

    [Fact]
    public void Text_Escapes_ProduceLiteralBraces()
    {
        BeaconToken token = Assert.Single(LexTokens("\"{{hi}}\""));

        var literal = Assert.Single(token.Parts);
        var text = Assert.IsType<BeaconTextLiteral>(literal);
        Assert.Equal("{hi}", text.Value);
    }

    [Fact]
    public void Text_NamedEscapes_ProduceControlChars()
    {
        BeaconToken token = Assert.Single(LexTokens("\"a\\nb\\tc\\\\d\\\"e\""));

        var literal = Assert.Single(token.Parts);
        var text = Assert.IsType<BeaconTextLiteral>(literal);
        Assert.Equal("a\nb\tc\\d\"e", text.Value);
    }

    [Fact]
    public void Text_UnknownEscape_DropsBackslashAsBefore()
    {
        BeaconToken token = Assert.Single(LexTokens("\"a\\qb\""));

        var literal = Assert.Single(token.Parts);
        var text = Assert.IsType<BeaconTextLiteral>(literal);
        Assert.Equal("aqb", text.Value);
    }

    [Fact]
    public void Text_Hole_HoldsArbitraryExpressionSource()
    {
        BeaconToken token = Assert.Single(LexTokens("\"Online: {online_count + 1}\""));

        var hole = Assert.Single(token.Parts.OfType<BeaconTextHole>());
        Assert.Equal("online_count + 1", hole.Expression);
    }

    [Fact]
    public void Text_Hole_SupportsNestedBracesForMaps()
    {
        BeaconToken token = Assert.Single(LexTokens("\"{saved or {}}\""));

        var hole = Assert.Single(token.Parts.OfType<BeaconTextHole>());
        Assert.Equal("saved or {}", hole.Expression);
    }

    [Fact]
    public void TripleText_SupportsInterpolation()
    {
        BeaconToken token = Assert.Single(LexTokens("\"\"\"Dear {player},\nhello\"\"\""));

        Assert.Equal(BeaconTokenKind.TripleText, token.Kind);
        Assert.Contains(token.Parts, p => p is BeaconTextHole h && h.Expression == "player");
    }

    [Fact]
    public void UnclosedString_IsParseErrorWithToken()
    {
        BeaconLexResult result = Lex("\"oops");

        Assert.Contains(result.Diagnostics, d =>
            string.Equals(d.Code, BeaconDiagnosticCodes.Parse, StringComparison.Ordinal)
            && d.Severity == BeaconSeverity.Error);
        Assert.Contains(result.Tokens, t => t.Kind == BeaconTokenKind.Text);
    }

    [Fact]
    public void UnclosedHole_IsParseError()
    {
        BeaconLexResult result = Lex("\"hi {name");

        Assert.Contains(result.Diagnostics, d =>
            string.Equals(d.Code, BeaconDiagnosticCodes.Parse, StringComparison.Ordinal)
            && d.Severity == BeaconSeverity.Error);
    }

    [Fact]
    public void SingleLineText_EndsAtNewline_WithParseError()
    {
        BeaconLexResult result = Lex("\"oops\nsay \"hi\"");

        Assert.Contains(result.Diagnostics, d =>
            string.Equals(d.Code, BeaconDiagnosticCodes.Parse, StringComparison.Ordinal));
        Assert.Contains(result.Tokens, t => t.Kind == BeaconTokenKind.Keyword && t.Normalized == "say");
    }

    [Fact]
    public void Division_LexesAsSymbol()
    {
        IReadOnlyList<BeaconToken> tokens = LexTokens("a / b");

        Assert.Equal(
            [BeaconTokenKind.Identifier, BeaconTokenKind.Symbol, BeaconTokenKind.Identifier],
            tokens.Select(t => t.Kind).ToList());
        Assert.Equal("/", tokens[1].Text);
        Assert.Empty(LexErrors("a / b"));
    }

    [Fact]
    public void Regex_AfterMatches()
    {
        IReadOnlyList<BeaconToken> tokens = LexTokens("text matches /x/");

        BeaconToken regex = Assert.Single(tokens, t => t.Kind == BeaconTokenKind.Regex);
        Assert.Equal("x", regex.Pattern);
    }

    [Theory]
    [InlineData("return /ab+c/")]
    [InlineData("when /ab+c/")]
    [InlineData("(/ab+c/)")]
    [InlineData("[/ab+c/]")]
    [InlineData("{a: /ab+c/}")]
    [InlineData("f(t, /ab+c/)")]
    public void Regex_LexesInOperandPositions(string source)
    {
        IReadOnlyList<BeaconToken> tokens = LexTokens(source);

        BeaconToken regex = Assert.Single(tokens, t => t.Kind == BeaconTokenKind.Regex);
        Assert.Equal("ab+c", regex.Pattern);
    }

    [Fact]
    public void Regex_SupportsNamedGroups()
    {
        IReadOnlyList<BeaconToken> tokens = LexTokens("match(t, /(?<n>[0-9]+)/)");

        BeaconToken regex = Assert.Single(tokens, t => t.Kind == BeaconTokenKind.Regex);
        Assert.Equal("(?<n>[0-9]+)", regex.Pattern);
    }

    [Fact]
    public void Regex_EscapedSlash_DoesNotTerminate()
    {
        IReadOnlyList<BeaconToken> tokens = LexTokens("text matches /a\\/b/");

        BeaconToken regex = Assert.Single(tokens, t => t.Kind == BeaconTokenKind.Regex);
        Assert.Equal("a\\/b", regex.Pattern);
    }

    [Fact]
    public void Regex_Unclosed_IsParseError()
    {
        BeaconLexResult result = Lex("text matches /abc");

        Assert.Contains(result.Diagnostics, d =>
            string.Equals(d.Code, BeaconDiagnosticCodes.Parse, StringComparison.Ordinal)
            && d.Severity == BeaconSeverity.Error);
    }

    [Fact]
    public void SlashSlash_NeverStartsRegex()
    {
        // https:// must not break: no comment, no regex, plain symbols.
        BeaconLexResult result = Lex("say https://example.com");

        Assert.DoesNotContain(result.Tokens, t => t.Kind == BeaconTokenKind.Regex);
        Assert.Empty(result.Comments);
        Assert.Contains(result.Tokens, t => t.Kind == BeaconTokenKind.Identifier && t.Text == "https");
    }

    [Theory]
    [InlineData("seconds", "second")]
    [InlineData("SECONDS", "second")]
    [InlineData("sec", "second")]
    [InlineData("s", "second")]
    [InlineData("second", "second")]
    [InlineData("minutes", "minute")]
    [InlineData("min", "minute")]
    [InlineData("milliseconds", "millisecond")]
    [InlineData("hours", "hour")]
    public void Units_LexWithCanonicalNames(string spelling, string canonical)
    {
        BeaconToken token = Assert.Single(LexTokens(spelling));

        Assert.Equal(BeaconTokenKind.Unit, token.Kind);
        Assert.Equal(canonical, token.CanonicalUnit);
        Assert.Equal(spelling, token.Text);
    }

    [Fact]
    public void HashComment_IsSkippedButRecorded()
    {
        BeaconLexResult result = Lex("say \"hi\" # trailing");

        Assert.Single(result.Comments);
        Assert.Contains("# trailing", result.Comments[0].Text, StringComparison.Ordinal);
        Assert.Equal(BeaconTokenKind.Keyword, result.Tokens[0].Kind);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void SlashComment_AtLineStart_IsComment()
    {
        BeaconLexResult result = Lex("// hello\nsay \"hi\"");

        Assert.Single(result.Comments);
        Assert.Contains(result.Tokens, t => t.Kind == BeaconTokenKind.Keyword && t.Normalized == "say");
    }

    [Fact]
    public void SlashComment_AfterWhitespace_IsComment()
    {
        BeaconLexResult result = Lex("say \"hi\" // done");

        Assert.Single(result.Comments);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void BlockComment_IsSkipped()
    {
        BeaconLexResult result = Lex("say /* quiet */ \"hi\"");

        Assert.Empty(result.Comments);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Tokens, t => t.Kind == BeaconTokenKind.Text);
    }

    [Fact]
    public void UnclosedBlockComment_YieldsExactlyOneWarning()
    {
        BeaconLexResult result = Lex("say \"hi\" /* oops");

        BeaconDiagnostic warning = Assert.Single(result.Diagnostics);
        Assert.Equal(BeaconDiagnosticCodes.UnclosedBlockComment, warning.Code);
        Assert.Equal(BeaconSeverity.Warning, warning.Severity);
        Assert.Contains(result.Tokens, t => t.Kind == BeaconTokenKind.Keyword && t.Normalized == "say");
    }

    [Fact]
    public void EqualsOutsideOperators_SuggestsSetToOrIs()
    {
        BeaconLexResult result = Lex("set x = 5");

        BeaconDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(BeaconDiagnosticCodes.StrictEquals, diagnostic.Code);
        Assert.Equal(BeaconSeverity.Error, diagnostic.Severity);
        Assert.Contains("set x to 5", diagnostic.Suggestion, StringComparison.Ordinal);
        Assert.Contains("is", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BareEquals_SuggestsSetTo()
    {
        BeaconLexResult result = Lex("x = 5");

        Assert.Contains(result.Diagnostics, d =>
            string.Equals(d.Code, BeaconDiagnosticCodes.StrictEquals, StringComparison.Ordinal));
    }

    [Fact]
    public void ComparisonOperators_DoNotTriggerEqualsError()
    {
        Assert.Empty(LexErrors("a == b"));
        Assert.Empty(LexErrors("a != b"));
        Assert.Empty(LexErrors("a <= b"));
        Assert.Empty(LexErrors("a >= b"));
    }

    [Fact]
    public void LeadingSlash_SuggestsServerOrMcc()
    {
        BeaconLexResult result = Lex("/home");

        BeaconDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(BeaconDiagnosticCodes.StrictLeadingSlash, diagnostic.Code);
        Assert.Equal(BeaconSeverity.Error, diagnostic.Severity);
        Assert.Contains("server", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void LeadingSlash_AfterIndentation_IsStillRejected()
    {
        BeaconLexResult result = Lex("  /home");

        Assert.Contains(result.Diagnostics, d =>
            string.Equals(d.Code, BeaconDiagnosticCodes.StrictLeadingSlash, StringComparison.Ordinal));
    }

    [Fact]
    public void SmartQuotes_NormalizeWithNoteNamingLine()
    {
        BeaconLexResult result = Lex("say \u201chello\u201d");

        Assert.DoesNotContain(result.Tokens, t => t.Text.Contains('\u201c'));
        Assert.Contains(result.Tokens, t => t.Kind == BeaconTokenKind.Text);
        BeaconDiagnostic note = Assert.Single(result.Diagnostics);
        Assert.Equal(BeaconDiagnosticCodes.PasteNormalization, note.Code);
        Assert.Equal(BeaconSeverity.Note, note.Severity);
        Assert.Contains("1", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscordMangled_Script_LexesLikeCleanForm()
    {
        BeaconLexResult clean = Lex("set greeting to \"hi\"\nwait 2 seconds\n");
        BeaconLexResult mangled = Lex("SET greeting TO \u201chi\u201d\nWAIT 2 Seconds\n");

        List<string> CleanSignature() =>
            clean.Tokens.Select(t => t.Kind + ":" + (t.Kind == BeaconTokenKind.Keyword ? t.Normalized : t.Kind == BeaconTokenKind.Unit ? t.CanonicalUnit : t.Kind == BeaconTokenKind.Identifier ? t.Text : "")).ToList();
        List<string> MangledSignature() =>
            mangled.Tokens.Select(t => t.Kind + ":" + (t.Kind == BeaconTokenKind.Keyword ? t.Normalized : t.Kind == BeaconTokenKind.Unit ? t.CanonicalUnit : t.Kind == BeaconTokenKind.Identifier ? t.Text : "")).ToList();

        Assert.Equal(CleanSignature(), MangledSignature());
        Assert.Contains(mangled.Diagnostics, d =>
            string.Equals(d.Code, BeaconDiagnosticCodes.PasteNormalization, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryToken_CarriesFileLineColumnSpan()
    {
        BeaconLexResult result = Lex("say \"hi\"\nwait 2 seconds");

        foreach (BeaconToken token in result.Tokens)
        {
            Assert.Equal("test.mcc", token.Span.File);
            Assert.True(token.Span.Line >= 1);
            Assert.True(token.Span.Column >= 1);
        }

        BeaconToken say = result.Tokens[0];
        Assert.Equal((1, 1), (say.Span.Line, say.Span.Column));
        BeaconToken wait = result.Tokens.First(t => t.Kind == BeaconTokenKind.Keyword && t.Normalized == "wait");
        Assert.Equal(2, wait.Span.Line);
    }

    [Fact]
    public void Stream_EndsWithEndOfFile()
    {
        BeaconLexResult result = Lex("say \"hi\"");

        Assert.Equal(BeaconTokenKind.EndOfFile, result.Tokens[^1].Kind);
    }
}
