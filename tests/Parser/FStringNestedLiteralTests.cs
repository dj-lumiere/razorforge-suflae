namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// F-string interpolation holes (`{...}`) must permit nested string literals of any
/// flavor — plain `"..."`, bytes `b"..."`, raw `r"..."`, formatted `f"..."`, and
/// raw-formatted `rf"..."`. Previously the insertion-expression tokenizer treated the
/// `b`/`r`/`f` prefix as a bare identifier, splitting `b"42"` into two tokens.
/// </summary>
public class FStringNestedLiteralTests
{
    [Fact]
    public void Parse_FString_WithNestedBytesLiteral_Parses()
    {
        string source = """"
                        routine start()
                          show(f"x: {f(bytes: b"42")}")
                          return
                        """";

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_FString_WithNestedPlainString_Parses()
    {
        string source = """"
                        routine start()
                          show(f"x: {len(s: "hi")}")
                          return
                        """";

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_FString_WithNestedRawString_Parses()
    {
        string source = """"
                        routine start()
                          show(f"x: {parse(s: r"\d+")}")
                          return
                        """";

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_FString_WithNestedFString_Parses()
    {
        string source = """"
                        routine start()
                          show(f"x: {wrap(s: f"inner {y}")}")
                          return
                        """";

        AssertParses(source: source);
    }
}
