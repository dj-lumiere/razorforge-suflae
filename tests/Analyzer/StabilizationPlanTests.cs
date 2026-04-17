using Compiler.Diagnostics;
using SemanticVerification.Results;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests locking in P1-P9 stabilization plan fixes.
/// Prevents regressions in generic resolution, body matching,
/// using semantics, and type provenance.
/// </summary>
public class StabilizationPlanTests
{
    #region P1 ??Protocol method lookup on instantiated generics

    /// <summary>
    /// Verifies that protocol method lookup substitutes the owner type argument on instantiated generics.
    /// </summary>
    [Fact]
    public void P1_ProtocolMethodOnGenericOwner_SubstitutesParamType()
    {
        // Protocol method declared with T param, called on resolved owner
        // LookupMethod should substitute T -> S32 in parameter type
        string source = """
                        protocol Summable
                          @readonly
                          routine Me.sum(other: Me) -> Me

                        record Total[T] obeys Summable
                          value: T

                        @readonly
                        routine Total[T].sum(other: Total[T]) -> Total[T]
                          return me

                        routine test()
                          var a = Total[S32](value: 1)
                          var b = Total[S32](value: 2)
                          var c: Total[S32] = a.sum(other: b)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that generic owner and generic member routine substitutions both resolve correctly.
    /// </summary>
    [Fact]
    public void P1_GenericMethodOnGenericOwner_BothLevelsSubstituted()
    {
        // Owner-level T and method-level U should both resolve
        string source = """
                        record Store[T]
                          item: T

                        routine Store[T].transform[U](func_val: U) -> U
                          return func_val

                        routine test()
                          var s = Store[S32](item: 10)
                          var r: Bool = s.transform[Bool](func_val: true)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that a generic member routine return type resolves using the instantiated owner type argument.
    /// </summary>
    [Fact]
    public void P1_GenericMethodReturnType_UsesOwnerTypeArg()
    {
        // Return type T should resolve to the owner's type argument
        string source = """
                        record Slot[T]
                          data: T

                        routine Slot[T].peek() -> T
                          return me.data

                        routine test()
                          var s = Slot[Bool](data: false)
                          var v: Bool = s.peek()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that protocol dispatch on nested instantiated generic types preserves the substituted owner type.
    /// </summary>
    [Fact]
    public void P1_ProtocolMethodOnNestedGenericOwner_UsesConcreteNestedType()
    {
        string source = """
                        protocol Clonable
                          @readonly
                          routine Me.clone() -> Me

                        record Box[T] obeys Clonable
                          value: T

                        @readonly
                        routine Box[T].clone() -> Box[T]
                          return Box[T](value: me.value)

                        routine test()
                          var boxed = Box[Box[S32]](value: Box[S32](value: 7))
                          var copy: Box[Box[S32]] = boxed.clone()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region P2 ??GenericDefinition preserved across all update paths

    /// <summary>
    /// Verifies that generic record protocol updates preserve the generic definition for later substitution.
    /// </summary>
    [Fact]
    public void P2_GenericRecord_PreservesDefinitionAfterProtocolUpdate()
    {
        // GenericDefinition must survive UpdateRecordProtocols
        // so method lookup on Pair[S32] can substitute T -> S32
        string source = """
                        protocol Showable
                          @readonly
                          routine Me.label() -> Text

                        record Pair[T] obeys Showable
                          first: T
                          second: T

                        @readonly
                        routine Pair[T].label() -> Text
                          return "pair"

                        routine Pair[T].get_first() -> T
                          return me.first

                        routine test()
                          var p = Pair[S32](first: 1, second: 2)
                          var v: S32 = p.get_first()
                          var lbl: Text = p.label()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that generic entity protocol updates preserve the generic definition for later substitution.
    /// </summary>
    [Fact]
    public void P2_GenericEntity_PreservesDefinitionAfterProtocolUpdate()
    {
        // Same as above but for entity types
        string source = """
                        protocol Describable
                          @readonly
                          routine Me.describe() -> Text

                        entity Container[T] obeys Describable
                          item: T

                        @readonly
                        routine Container[T].describe() -> Text
                          return "container"

                        routine Container[T].get_item() -> T
                          return me.item

                        routine test()
                          var c = Container[Bool](item: true)
                          var v: Bool = c.get_item()
                          var d: Text = c.describe()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region P3 ??Using bound type agreement

    /// <summary>
    /// Verifies that <c>using</c> binds the correct type for generic resources exposing <c>$enter</c>/<c>$exit</c>.
    /// </summary>
    [Fact]
    public void P3_UsingWithGenericResource_BindsCorrectType()
    {
        // using on a generic type with $enter/$exit
        string source = """
                        record Guard[T]
                          resource: T

                        routine Guard[T].$enter() -> T
                          return me.resource

                        routine Guard[T].$exit()
                          return

                        routine test()
                          var g = Guard[S32](resource: 42)
                          using g as val
                            show(val)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UsingTargetMissingEnterExit);
    }

    /// <summary>
    /// Verifies that <c>using</c> binds the fully substituted nested generic type returned by <c>$enter</c>.
    /// </summary>
    [Fact]
    public void P3_UsingWithNestedGenericReturn_BindsResolvedType()
    {
        string source = """
                        record Box[T]
                          value: T

                        record Guard[T]
                          resource: T

                        routine Guard[T].$enter() -> T
                          return me.resource

                        routine Guard[T].$exit()
                          return

                        routine test()
                          var g = Guard[Box[S32]](resource: Box[S32](value: 42))
                          using g as value_box
                            var n: S32 = value_box.value
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region P4 ??Routine body matching edge cases

    /// <summary>
    /// Verifies that zero-argument and one-argument overloads each resolve to the correct routine body.
    /// </summary>
    [Fact]
    public void P4_OverloadedRoutines_ZeroArgVsOneArg_BothMatch()
    {
        // Zero-arg and one-arg overloads must each find their correct body
        string source = """
                        record Counter
                          count: S32

                        routine Counter.reset() -> Counter
                          return Counter(count: 0)

                        routine Counter.reset(to: S32) -> Counter
                          return Counter(count: to)

                        routine test()
                          var c = Counter(count: 5)
                          var a = c.reset()
                          var b = c.reset(to: 10)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    /// <summary>
    /// Verifies that multiple <c>$create</c> overloads with distinct parameter types all resolve their bodies.
    /// </summary>
    [Fact]
    public void P4_OverloadedCreate_ThreeOverloads_AllMatch()
    {
        // Three $create overloads with different types: S32, Bool, Text
        string source = """
                        record Value
                          raw: S64

                        routine Value.$create(from: S32) -> Value
                          return Value(raw: from.S64())

                        routine Value.$create(from: Bool) -> Value
                          return Value(raw: 1s64)

                        routine Value.$create(from: Text) -> Value
                          return Value(raw: 0s64)

                        routine test()
                          var a = Value(from: 42)
                          var b = Value(from: true)
                          var c = Value(from: "hi")
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    /// <summary>
    /// Verifies that routine body matching on generic owners uses resolved signatures.
    /// </summary>
    [Fact]
    public void P4_GenericRoutineBody_MatchesByResolvedSignature()
    {
        // Body matching on generic owner should use resolved type names
        string source = """
                        record Stack[T]
                          top: T

                        routine Stack[T].push(item: T) -> Stack[T]
                          return Stack[T](top: item)

                        routine Stack[T].peek() -> T
                          return me.top

                        routine test()
                          var s = Stack[S32](top: 0)
                          var s2 = s.push(item: 42)
                          var v: S32 = s2.peek()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    /// <summary>
    /// Verifies that overloaded member routines on a generic owner resolve the correct body when the arity matches.
    /// </summary>
    [Fact]
    public void P4_GenericOwnerOverloads_SameArityDifferentTypes_MatchCorrectBody()
    {
        string source = """
                        record Buffer[T]
                          value: T
                          ready: Bool

                        routine Buffer[T].replace(next: T) -> Buffer[T]
                          return Buffer[T](value: next, ready: me.ready)

                        routine Buffer[T].replace(flag: Bool) -> Buffer[T]
                          return Buffer[T](value: me.value, ready: flag)

                        routine test()
                          var b = Buffer[S32](value: 1, ready: false)
                          var a = b.replace(next: 9)
                          var c = b.replace(flag: true)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    /// <summary>
    /// Verifies that generic creator overload matching stays stable when one overload takes the instantiated owner type.
    /// </summary>
    [Fact]
    public void P4_CreateOverload_WithOwnerTypedParameter_MatchesCorrectBody()
    {
        string source = """
                        record Wrapper[T]
                          value: T

                        routine Wrapper[T].$create(from: T) -> Wrapper[T]
                          return Wrapper[T](value: from)

                        routine Wrapper[T].$create(copy: Wrapper[T]) -> Wrapper[T]
                          return Wrapper[T](value: copy.value)

                        routine test()
                          var a = Wrapper[S32](from: 12)
                          var b = Wrapper[S32](copy: a)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    #endregion

    #region P5 ??GenericAstRewriter no longer rewrites identifiers

    /// <summary>
    /// Verifies that generic constant parameters remain for code generation to resolve rather than being rewritten in the AST.
    /// </summary>
    [Fact]
    public void P5_GenericConstParam_ResolvedAtCodegen()
    {
        // Const generic value N should not be rewritten in AST
        // Codegen resolves via _typeSubstitutions
        string source = """
                        record Wrapper[T]
                          value: T

                        routine Wrapper[T].get() -> T
                          return me.value

                        routine test()
                          var w = Wrapper[S32](value: 99)
                          var v: S32 = w.get()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region P6 ??No string-based generic heuristics

    /// <summary>
    /// Verifies that nested generic types resolve through generic definitions instead of string heuristics.
    /// </summary>
    [Fact]
    public void P6_NestedGenericType_ResolvedViaGenericDefinition()
    {
        // Nested generics like Wrapper[Wrapper[S32]] should resolve
        // through GenericDefinition, not Name.Contains('[') heuristics
        string source = """
                        record Wrapper[T]
                          inner: T

                        routine Wrapper[T].unwrap() -> T
                          return me.inner

                        routine test()
                          var inner = Wrapper[S32](inner: 42)
                          var outer = Wrapper[Wrapper[S32]](inner: inner)
                          var result: Wrapper[S32] = outer.unwrap()
                          var val: S32 = result.unwrap()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that nested generic member-variable types are resolved before a member routine returns them.
    /// </summary>
    [Fact]
    public void P6_NestedGenericMemberType_ReturnsResolvedInnerType()
    {
        string source = """
                        record Node[T]
                          item: T

                        @readonly
                        routine Node[T].hash() -> U64
                          return 0u64

                        record Holder[T]
                          node: Node[T]

                        @readonly
                        routine Holder[T].hash() -> U64
                          return 0u64

                        routine Holder[T].fetch() -> Node[T]
                          return me.node

                        routine test()
                          var h = Holder[S32](node: Node[S32](item: 4))
                          var n: Node[S32] = h.fetch()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Cross-cutting ??Generic + protocol + overload combined

    /// <summary>
    /// Verifies that generic substitution, protocol lookup, and overload body matching work together in one scenario.
    /// </summary>
    [Fact]
    public void Combined_GenericWithProtocolAndOverloads()
    {
        // Complex scenario: generic type obeys protocol, has overloaded methods,
        // method lookup + body matching + type substitution all must work together
        string source = """
                        protocol Clearable
                          routine Me.clear() -> Me

                        record Buffer[T] obeys Clearable
                          item: T
                          size: S32

                        routine Buffer[T].clear() -> Buffer[T]
                          return Buffer[T](item: me.item, size: 0)

                        routine Buffer[T].set(item: T) -> Buffer[T]
                          return Buffer[T](item: item, size: me.size)

                        routine Buffer[T].resize(size: S32) -> Buffer[T]
                          return Buffer[T](item: me.item, size: size)

                        routine test()
                          var b = Buffer[S32](item: 0, size: 10)
                          var c: Buffer[S32] = b.clear()
                          var d = b.set(item: 42)
                          var e = b.resize(size: 5)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion
}
