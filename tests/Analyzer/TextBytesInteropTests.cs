using Compiler.Diagnostics;
using SemanticVerification.Results;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

public class TextBytesInteropTests
{
    [Fact]
    public void CharacterLiteral_AssignsToCharacter()
    {
        AnalysisResult result = Analyze("""
                                        routine test()
                                          var ch: Character = 'A'
                                          return
                                        """);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void TextIndexing_AssignsToCharacter()
    {
        AnalysisResult result = Analyze("""
                                        routine test()
                                          var ch: Character = "Hello"[0]
                                          return
                                        """);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void CharacterUsesIsAlphabetic_NotIsLetter()
    {
        AnalysisResult valid = Analyze("""
                                       routine test(ch: Character)
                                         var ok: Bool = ch.is_alphabetic()
                                         return
                                       """);
        Assert.Empty(valid.Errors);

        AnalysisResult invalid = Analyze("""
                                         routine test(ch: Character)
                                           var bad: Bool = ch.is_letter()
                                           return
                                         """);
        Assert.Contains(invalid.Errors,
            e => e.Code == SemanticDiagnosticCode.MemberNotFound);
    }

    [Fact]
    public void TextEncodeAndBytesDecodeUtf8_Analyze()
    {
        AnalysisResult result = Analyze("""
                                        routine test()
                                          var text: Text = "Hello, 계"
                                          var bytes: Bytes = text.encode_as_utf8()
                                          var roundtrip: Text = bytes.decode_as_utf8()
                                          return
                                        """);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void BytesInterpretAsUtf8_ProducesCharacters()
    {
        AnalysisResult result = Analyze("""
                                        routine test()
                                          var bytes: Bytes = "Hi".encode_as_utf8()
                                          for ch in bytes.interpret_as_utf8()
                                            var cp: U32 = ch.codepoint()
                                          return
                                        """);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void BytesLossyUtf8Apis_Analyze()
    {
        AnalysisResult result = Analyze("""
                                        routine test()
                                          var bytes: Bytes = b"\x80ABC"
                                          var text: Text = bytes.decode_as_utf8_lossy()
                                          for ch in bytes.interpret_as_utf8_lossy()
                                            var cp: U32 = ch.codepoint()
                                          return
                                        """);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void BytesStrictUtf8Apis_AnalyzeInsideFailableRoutine()
    {
        AnalysisResult result = Analyze("""
                                        routine test!()
                                          var bytes: Bytes = b"ABC"
                                          var text: Text = bytes.decode_as_utf8!()
                                          var view = bytes.interpret_as_utf8!()
                                          for ch in view
                                            var cp: U32 = ch.codepoint()
                                          absent
                                        """);

        Assert.Empty(result.Errors);
    }
}
