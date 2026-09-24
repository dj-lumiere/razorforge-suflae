using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// A positional construction of a generic type (<c>Hijacked[T](raw)</c>) binds to the creator whose
/// parameter types fit the arguments, exactly like the named form (<c>Hijacked[T](from: raw)</c>), whether
/// the argument is a variable or a nested call. An argument no creator takes gets a message that names
/// its type instead of an empty member name.
/// </summary>
public class PositionalGenericCreatorTests
{
    [Fact]
    public void Analyze_PositionalGenericCreatorWithNestedCall_Binds()
    {
        const string source = """
                              import BuilderQuery

                              dangerous routine alloc_nested(count: U64) -> Hijacked[FrozenController]
                                var header = FrozenController.data_size()
                                var ctrl = Hijacked[FrozenController](rf_allocate_dynamic(size: header + U32.data_size() * count))
                                return ctrl

                              dangerous routine alloc_split(count: U64) -> Hijacked[FrozenController]
                                var raw = rf_allocate_dynamic(size: U32.data_size() * count)
                                var ctrl = Hijacked[FrozenController](raw)
                                return ctrl

                              routine start()
                                return
                              """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_PositionalGenericCreatorNoCreatorFits_NamesTheArgumentType()
    {
        const string source = """
                              dangerous routine wrap(flag: Bool) -> Hijacked[FrozenController]
                                return Hijacked[FrozenController](flag)

                              routine start()
                                return
                              """;

        AssertHasErrorSa(source: source, expectedErrorSubstring: "takes a 'Bool' here");
    }
}
