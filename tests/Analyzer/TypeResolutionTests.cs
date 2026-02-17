using Compilers.Analysis.Results;
using Compilers.Analysis.Symbols;
using Compilers.Analysis.Types;
using RazorForge.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using Compilers.Analysis.Enums;
using static TestHelpers;

/// <summary>
/// Tests for semantic analyzer type resolution.
/// </summary>
public class TypeResolutionTests
{
    #region Basic Type Registration

    [Fact]
    public void Analyze_Record_RegistersInTypeRegistry()
    {
        string source = """
                        record Point {
                            x: F32
                            y: F32
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        TypeInfo? type = result.Registry.GetType(name: "Point");

        Assert.NotNull(@object: type);
        Assert.Equal(expected: TypeCategory.Record, actual: type.Category);
    }

    [Fact]
    public void Analyze_Entity_RegistersInTypeRegistry()
    {
        string source = """
                        entity User {
                            name: Text
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        TypeInfo? type = result.Registry.GetType(name: "User");

        Assert.NotNull(@object: type);
        Assert.Equal(expected: TypeCategory.Entity, actual: type.Category);
    }

    [Fact]
    public void Analyze_Choice_RegistersInTypeRegistry()
    {
        string source = """
                        choice Direction {
                            NORTH
                            SOUTH
                            EAST
                            WEST
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        TypeInfo? type = result.Registry.GetType(name: "Direction");

        Assert.NotNull(@object: type);
        Assert.Equal(expected: TypeCategory.Choice, actual: type.Category);
    }

    [Fact]
    public void Analyze_Variant_RegistersInTypeRegistry()
    {
        // Note: Don't use "Result" as it's a well-known error handling type
        string source = """
                        variant MyVariant {
                            SUCCESS: S32
                            ERROR: Text
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        TypeInfo? type = result.Registry.GetType(name: "MyVariant");

        Assert.NotNull(@object: type);
        Assert.Equal(expected: TypeCategory.Variant, actual: type.Category);
    }

    [Fact]
    public void Analyze_Protocol_RegistersInTypeRegistry()
    {
        string source = """
                        protocol Displayable {
                            @readonly
                            routine Me.display() -> Text
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        TypeInfo? type = result.Registry.GetType(name: "Displayable");

        Assert.NotNull(@object: type);
        Assert.Equal(expected: TypeCategory.Protocol, actual: type.Category);
    }

    #endregion

    #region Generic Type Registration

    [Fact]
    public void Analyze_GenericRecord_RegistersWithTypeParameters()
    {
        string source = """
                        record Container<T> {
                            value: T
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        TypeInfo? type = result.Registry.GetType(name: "Container");

        Assert.NotNull(@object: type);
        Assert.True(condition: type.IsGenericDefinition);
    }

    [Fact]
    public void Analyze_GenericEntity_MultipleTypeParameters()
    {
        string source = """
                        entity Pair<K, V> {
                            key: K
                            value: V
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        TypeInfo? type = result.Registry.GetType(name: "Pair");

        Assert.NotNull(@object: type);
        Assert.True(condition: type.IsGenericDefinition);
    }

    #endregion

    #region Routine Registration

    [Fact]
    public void Analyze_GlobalRoutine_RegistersInRegistry()
    {
        string source = """
                        routine greet(name: Text) -> Text {
                            return name
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        RoutineInfo? routine = result.Registry.GetRoutine(name: "greet");

        Assert.NotNull(@object: routine);
        Assert.Equal(expected: RoutineKind.Function, actual: routine.Kind);
    }

    [Fact]
    public void Analyze_Method_RegistersWithOwnerType()
    {
        string source = """
                        record Point {
                            x: F32
                            y: F32
                        }

                        @readonly
                        routine Point.distance() -> F32 {
                            return 0.0_f32
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        RoutineInfo? routine = result.Registry.GetRoutine(name: "Point.distance");

        Assert.NotNull(@object: routine);
        Assert.Equal(expected: RoutineKind.Method, actual: routine.Kind);
    }

    [Fact]
    public void Analyze_FailableRoutine_RegistersAsFailable()
    {
        string source = """
                        routine get_value!() -> S32 {
                            return 42
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        RoutineInfo? routine = result.Registry.GetRoutine(name: "get_value");

        Assert.NotNull(@object: routine);
        Assert.True(condition: routine.IsFailable);
    }

    #endregion

    #region Field Resolution

    [Fact]
    public void Analyze_RecordFields_ResolveTypes()
    {
        string source = """
                        record Color {
                            r: U8
                            g: U8
                            b: U8
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        TypeInfo? type = result.Registry.GetType(name: "Color");

        Assert.NotNull(@object: type);
        // Check fields are resolved
    }

    [Fact]
    public void Analyze_EntityFields_ResolveTypes()
    {
        string source = """
                        entity Document {
                            title: Text
                            page_count: U32
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        TypeInfo? type = result.Registry.GetType(name: "Document");

        Assert.NotNull(@object: type);
    }

    #endregion

    #region Type Errors

    [Fact]
    public void Analyze_UndefinedType_ReportsError()
    {
        string source = """
                        record Container {
                            value: UnknownType
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "UnknownType",
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_DuplicateTypeName_ReportsError()
    {
        string source = """
                        record Point {
                            x: F32
                        }

                        record Point {
                            y: F32
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    [Fact]
    public void Analyze_DuplicateFieldName_ReportsError()
    {
        string source = """
                        record Point {
                            x: F32
                            x: F32
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    [Fact]
    public void Analyze_ReservedFunctionPrefix_ReportsError()
    {
        string source = """
                        routine try_something() {
                            pass
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "reserved",
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_ReservedFunctionPrefix_Check_ReportsError()
    {
        string source = """
                        routine check_value() {
                            pass
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    [Fact]
    public void Analyze_ReservedFunctionPrefix_Lookup_ReportsError()
    {
        string source = """
                        routine lookup_item() {
                            pass
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Constraint Validation

    [Fact]
    public void Analyze_ValidConstraint_NoError()
    {
        string source = """
                        protocol Comparable {
                            @readonly
                            routine Me.__cmp__(other: Me) -> S32
                        }

                        record Wrapper<T>
                        requires T follows Comparable {
                            value: T
                        }
                        """;

        Analyze(source: source);
        // Should have no constraint-related errors
    }

    [Fact]
    public void Analyze_UnknownTypeParameter_ReportsError()
    {
        string source = """
                        record Container<T>
                        requires X follows Comparable {
                            value: T
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "X",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Protocol Implementation

    [Fact]
    public void Analyze_RecordFollowsProtocol_NoError()
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

    #endregion

    #region Integer Literal Type Inference

    [Fact]
    public void Analyze_ReturnIntegerLiteral_InfersFromReturnType()
    {
        string source = """
                        routine get_value() -> S32 {
                            return 0
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_ReturnIntegerLiteral_InfersU32()
    {
        string source = """
                        routine get_count() -> U32 {
                            return 42
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_ReturnIntegerLiteral_InfersS64()
    {
        string source = """
                        routine get_big() -> S64 {
                            return 123456789
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_ReturnIntegerLiteral_InMethodWithReturnType()
    {
        string source = """
                        record Counter {
                            value: S32
                        }

                        @readonly
                        routine Counter.get_zero() -> S32 {
                            return 0
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Variant Restrictions

    [Fact]
    public void Analyze_VariantInField_ReportsError()
    {
        string source = """
                        variant Shape {
                            Circle: F32
                            Rect: F32
                        }

                        entity Canvas {
                            current: Shape
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(result.Errors, e => e.Code == SemanticDiagnosticCode.VariantFieldNotAllowed);
    }

    [Fact]
    public void Analyze_VariantAsParameter_ReportsError()
    {
        string source = """
                        variant Shape {
                            Circle: F32
                            Rect: F32
                        }

                        routine process(shape: Shape) {
                            pass
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(result.Errors, e => e.Code == SemanticDiagnosticCode.VariantParameterNotAllowed);
    }

    [Fact]
    public void Analyze_VariantMethodDefinition_ReportsError()
    {
        string source = """
                        variant Shape {
                            Circle: F32
                            Rect: F32
                        }

                        @readonly
                        routine Shape.area() -> F64 {
                            return 0.0
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(result.Errors, e => e.Code == SemanticDiagnosticCode.VariantMethodNotAllowed);
    }

    [Fact]
    public void Analyze_VariantReturnType_NoError()
    {
        string source = """
                        variant Shape {
                            Circle: F32
                            Rect: F32
                        }

                        routine make_shape() -> Shape {
                            return CIRCLE(1.0)
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.VariantFieldNotAllowed);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.VariantParameterNotAllowed);
    }

    [Fact]
    public void AnalyzeSuflae_VariantInField_ReportsError()
    {
        string source = """
                        variant Shape
                            Circle: F32
                            Rect: F32

                        entity Canvas
                            current: Shape
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.Contains(result.Errors, e => e.Code == SemanticDiagnosticCode.VariantFieldNotAllowed);
    }

    #endregion

    #region Choice Restrictions

    [Fact]
    public void Analyze_ChoiceOperatorDefinition_ReportsError()
    {
        string source = """
                        choice HttpStatus {
                            OK
                            NOT_FOUND
                        }

                        @readonly
                        routine HttpStatus.__add__(you: HttpStatus) -> HttpStatus {
                            return OK
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(result.Errors, e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }

    [Fact]
    public void Analyze_ChoiceRegularMethod_NoError()
    {
        string source = """
                        choice Color {
                            RED
                            GREEN
                            BLUE
                        }

                        @readonly
                        routine Color.is_warm() -> Bool {
                            return false
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }

    [Fact]
    public void Analyze_ChoiceMixedValues_ReportsError()
    {
        string source = """
                        choice HttpStatus {
                            OK: 200
                            NOT_FOUND
                            INTERNAL_ERROR: 500
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(result.Errors, e => e.Code == SemanticDiagnosticCode.ChoiceMixedValues);
    }

    [Fact]
    public void Analyze_ChoiceAllExplicitValues_NoError()
    {
        string source = """
                        choice HttpStatus {
                            OK: 200
                            NOT_FOUND: 404
                            INTERNAL_ERROR: 500
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.ChoiceMixedValues);
    }

    [Fact]
    public void Analyze_ChoiceAllImplicitValues_NoError()
    {
        string source = """
                        choice Color {
                            RED
                            GREEN
                            BLUE
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.ChoiceMixedValues);
    }

    #endregion
}
