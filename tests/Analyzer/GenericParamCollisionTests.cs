using Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Regression lock for the generic-parameter alpha-rename work (CLAUDE.md ⚡ DO-FIRST TODO).
///
/// A generic parameter's NAME is a label, not an identity — a user type named the same as a stdlib
/// generic's parameter (`record T` vs `List[T]`/`Array[T,N]`/`Hijacked[T]`, `record N` vs `Array[T,N]`,
/// `record M` vs `UnpackedFloat[M,L,W]`) must NOT break that generic's monomorphization. Today it DOES
/// (Track-C "unsubstituted generic parameter" / RF-S954), because the pipeline keys on the parameter
/// NAME as identity in ~6 places. These tests encode the fixed behavior; UN-SKIP each when the alpha-
/// rename (parameter identity = slot, not name) lands.
/// </summary>
public class GenericParamCollisionTests
{
    /// <summary>
    /// A user `record T` must coexist with `List[S32]` (List → Hijacked[T]/Array[T,N] internally).
    /// Currently FAILS: `record T` collides with the stdlib generics' `T` parameter.
    /// </summary>
    [Fact]
    public void UserRecordT_CoexistsWith_ListS32()
    {
        AssertAnalyzes(source: """
                               record T
                                 a: S32

                               routine start()
                                 var xs = List[S32]()
                                 xs.add_last(10_s32)
                                 var t = T(a: 1_s32)
                                 return
                               """);
    }

    /// <summary>
    /// The full goal: user `record T`/`N`/`M` (colliding with `Array[T,N]`, `UnpackedFloat[M,L,W]`) plus a
    /// user generic routine `identity[T]` plus F128 (heavy generic instantiation) all coexist in one file.
    /// </summary>
    [Fact]
    public void UserRecordsTNM_CoexistWith_Generics_And_Identity_And_F128()
    {
        AssertAnalyzes(source: """
                               record T
                                 a: S32
                               record N
                                 a: S32
                               record M
                                 a: S32

                               routine identity[U](x: U) -> U
                                 return x

                               routine start()
                                 var xs = List[S32]()
                                 xs.add_last(10_s32)
                                 var id = identity(7_s32)
                                 var f = 3.0_f128.sqrt()
                                 var t = T(a: 1_s32)
                                 var n = N(a: 2_s32)
                                 var m = M(a: 3_s32)
                                 return
                               """);
    }
}
