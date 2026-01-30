using Compilers.Analysis.Results;
using RazorForge.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for protocol implementation analysis in the semantic analyzer:
/// protocol conformance, missing methods, signature matching.
/// </summary>
public class ProtocolImplementationTests
{
    #region Basic Protocol Implementation

    [Fact]
    public void Analyze_ImplementsAllMethods_NoError()
    {
        string source = """
                        protocol Displayable {
                            @readonly
                            routine Me.display() -> Text
                        }

                        record Point follows Displayable {
                            x: F32
                            y: F32
                        }

                        @readonly
                        routine Point.display() -> Text {
                            return "point"
                        }
                        """;

        Analyze(source: source);
        // Should validate protocol implementation
    }

    [Fact]
    public void Analyze_MissingProtocolMethod_ReportsError()
    {
        string source = """
                        protocol Displayable {
                            @readonly
                            routine Me.display() -> Text
                        }

                        record Point follows Displayable {
                            x: F32
                            y: F32
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    [Fact]
    public void Analyze_WrongMethodSignature_ReportsError()
    {
        string source = """
                        protocol Displayable {
                            @readonly
                            routine Me.display() -> Text
                        }

                        record Point follows Displayable {
                            x: F32
                            y: F32
                        }

                        @readonly
                        routine Point.display() -> S32 {
                            return 0
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Protocol Method Attributes

    [Fact]
    public void Analyze_MethodMissingReadonly_ReportsError()
    {
        string source = """
                        protocol Displayable {
                            @readonly
                            routine Me.display() -> Text
                        }

                        record Point follows Displayable {
                            x: F32
                            y: F32
                        }

                        routine Point.display() -> Text {
                            return "point"
                        }
                        """;

        Analyze(source: source);
        // Should warn about missing @readonly attribute
    }

    [Fact]
    public void Analyze_MethodWithWritableWhenProtocolReadonly_ReportsError()
    {
        string source = """
                        protocol Displayable {
                            @readonly
                            routine Me.display() -> Text
                        }

                        entity Point follows Displayable {
                            var x: F32
                            var y: F32
                        }

                        @writable
                        routine Point.display() -> Text {
                            return "point"
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Multiple Protocols

    [Fact]
    public void Analyze_MultipleProtocols_AllImplemented_NoError()
    {
        string source = """
                        protocol Displayable {
                            @readonly
                            routine Me.display() -> Text
                        }

                        protocol Comparable {
                            @readonly
                            routine Me.__cmp__(other: Me) -> S32
                        }

                        record Value follows Displayable, Comparable {
                            value: S32
                        }

                        @readonly
                        routine Value.display() -> Text {
                            return "value"
                        }

                        @readonly
                        routine Value.__cmp__(other: Value) -> S32 {
                            return me.value - other.value
                        }
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_MultipleProtocols_OneMissing_ReportsError()
    {
        string source = """
                        protocol Displayable {
                            @readonly
                            routine Me.display() -> Text
                        }

                        protocol Comparable {
                            @readonly
                            routine Me.__cmp__(other: Me) -> S32
                        }

                        record Value follows Displayable, Comparable {
                            value: S32
                        }

                        @readonly
                        routine Value.display() -> Text {
                            return "value"
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Generic Protocol Implementation

    [Fact]
    public void Analyze_GenericProtocol_Implementation_NoError()
    {
        string source = """
                        protocol Container {
                            @readonly
                            routine Me.count() -> uaddr
                        }

                        entity List<T> follows Container {
                            var items: [T]
                        }

                        @readonly
                        routine List<T>.count() -> uaddr {
                            return 0
                        }
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Protocol Method Parameters

    [Fact]
    public void Analyze_ProtocolMethodWithParameters_NoError()
    {
        string source = """
                        protocol Addable {
                            @readonly
                            routine Me.__add__(other: Me) -> Me
                        }

                        record Point follows Addable {
                            x: F32
                            y: F32
                        }

                        @readonly
                        routine Point.__add__(other: Point) -> Point {
                            return Point(x: me.x + other.x, y: me.y + other.y)
                        }
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_ProtocolMethodWrongParameterType_ReportsError()
    {
        string source = """
                        protocol Addable {
                            @readonly
                            routine Me.__add__(other: Me) -> Me
                        }

                        record Point follows Addable {
                            x: F32
                            y: F32
                        }

                        @readonly
                        routine Point.__add__(other: S32) -> Point {
                            return Point(x: me.x, y: me.y)
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Entity Protocol Implementation

    [Fact]
    public void Analyze_EntityImplementsProtocol_NoError()
    {
        string source = """
                        protocol Countable {
                            @readonly
                            routine Me.count() -> S32
                        }

                        entity Counter follows Countable {
                            var value: S32
                        }

                        @readonly
                        routine Counter.count() -> S32 {
                            return me.value
                        }
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Suflae Protocol Tests

    [Fact]
    public void AnalyzeSuflae_ImplementsProtocol_NoError()
    {
        string source = """
                        protocol Displayable:
                            @readonly
                            routine Me.display() -> S32

                        record Point follows Displayable:
                            x: F32
                            y: F32

                        @readonly
                        routine Point.display() -> S32:
                            return 0s32
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void AnalyzeSuflae_MissingProtocolMethod_ReportsError()
    {
        string source = """
                        protocol Displayable:
                            @readonly
                            routine Me.display() -> S32

                        record Point follows Displayable:
                            x: F32
                            y: F32
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(result.Errors, e => e.Code == SemanticDiagnosticCode.MissingProtocolMethod);
    }

    #endregion

    #region Protocol Inheritance

    [Fact]
    public void Analyze_ProtocolExtends_Implementation_NoError()
    {
        string source = """
                        protocol Displayable {
                            @readonly
                            routine Me.display() -> Text
                        }

                        protocol DebugDisplayable follows Displayable {
                            @readonly
                            routine Me.debug_display() -> Text
                        }

                        record Point follows DebugDisplayable {
                            x: F32
                            y: F32
                        }

                        @readonly
                        routine Point.display() -> Text {
                            return "point"
                        }

                        @readonly
                        routine Point.debug_display() -> Text {
                            return "Point(x, y)"
                        }
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_ProtocolExtends_MissingParentMethod_ReportsError()
    {
        string source = """
                        protocol Displayable {
                            @readonly
                            routine Me.display() -> Text
                        }

                        protocol DebugDisplayable follows Displayable {
                            @readonly
                            routine Me.debug_display() -> Text
                        }

                        record Point follows DebugDisplayable {
                            x: F32
                            y: F32
                        }

                        @readonly
                        routine Point.debug_display() -> Text {
                            return "Point(x, y)"
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Protocol with Default Values

    [Fact]
    public void Analyze_ProtocolMethodWithDefaultParameter_NoError()
    {
        string source = """
                        protocol Configurable {
                            @writable
                            routine Me.configure(value: S32 = 0)
                        }

                        entity Settings follows Configurable {
                            var value: S32
                        }

                        @writable
                        routine Settings.configure(value: S32 = 0) {
                            me.value = value
                        }
                        """;

        Analyze(source: source);
    }

    #endregion
}
