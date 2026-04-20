using SemanticVerification.Results;
using TypeModel.Types;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for implicit marker protocol conformance (D3 step 6).
/// Records implicitly conform to RecordType, entities to EntityType, etc.
/// </summary>
public class MarkerConformanceTests
{
    /// <summary>
    /// Tests Analyze_Record_HasRecordTypeConformance.
    /// </summary>
    [Fact]
    public void Analyze_Record_HasRecordTypeConformance()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);

        TypeInfo? pointType = result.Registry.LookupType(name: "Point");
        Assert.NotNull(@object: pointType);
        Assert.IsType<RecordTypeInfo>(@object: pointType);

        var record = (RecordTypeInfo)pointType;
        Assert.Contains(collection: record.ImplementedProtocols,
            filter: p => p.Name == "RecordType");
    }
    /// <summary>
    /// Tests Analyze_Record_HasTransitiveProtocols.
    /// </summary>

    [Fact]
    public void Analyze_Record_HasTransitiveProtocols()
    {
        // RecordType obeys Diagnosable, Equatable, Hashable, Copyable
        // Diagnosable obeys Representable
        string source = """
                        record Point
                          x: S32
                          y: S32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);

        var record = (RecordTypeInfo)result.Registry.LookupType(name: "Point")!;

        // Should have at minimum these transitive protocols
        Assert.Contains(collection: record.ImplementedProtocols,
            filter: p => p.Name == "Equatable");
        Assert.Contains(collection: record.ImplementedProtocols,
            filter: p => p.Name == "Hashable");
        Assert.Contains(collection: record.ImplementedProtocols,
            filter: p => p.Name == "Diagnosable");
    }
    /// <summary>
    /// Tests Analyze_Entity_HasEntityTypeConformance.
    /// </summary>

    [Fact]
    public void Analyze_Entity_HasEntityTypeConformance()
    {
        string source = """
                        entity Widget
                          label: Text
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);

        TypeInfo? widgetType = result.Registry.LookupType(name: "Widget");
        Assert.NotNull(@object: widgetType);
        Assert.IsType<EntityTypeInfo>(@object: widgetType);

        var entity = (EntityTypeInfo)widgetType;
        Assert.Contains(collection: entity.ImplementedProtocols,
            filter: p => p.Name == "EntityType");
        Assert.Contains(collection: entity.ImplementedProtocols,
            filter: p => p.Name == "Identifiable");
    }
    /// <summary>
    /// Tests Analyze_Choice_HasChoiceTypeConformance.
    /// </summary>

    [Fact]
    public void Analyze_Choice_HasChoiceTypeConformance()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);

        TypeInfo? colorType = result.Registry.LookupType(name: "Color");
        Assert.NotNull(@object: colorType);
        Assert.IsType<ChoiceTypeInfo>(@object: colorType);

        var choice = (ChoiceTypeInfo)colorType;
        Assert.Contains(collection: choice.ImplementedProtocols,
            filter: p => p.Name == "ChoiceType");
        Assert.Contains(collection: choice.ImplementedProtocols,
            filter: p => p.Name == "Hashable");
    }
    /// <summary>
    /// Tests Analyze_Flags_HasFlagsTypeConformance.
    /// </summary>

    [Fact]
    public void Analyze_Flags_HasFlagsTypeConformance()
    {
        string source = """
                        flags Permission
                          READ
                          WRITE
                          EXECUTE
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);

        TypeInfo? permType = result.Registry.LookupType(name: "Permission");
        Assert.NotNull(@object: permType);
        Assert.IsType<FlagsTypeInfo>(@object: permType);

        var flags = (FlagsTypeInfo)permType;
        Assert.Contains(collection: flags.ImplementedProtocols,
            filter: p => p.Name == "FlagsType");
        Assert.Contains(collection: flags.ImplementedProtocols,
            filter: p => p.Name == "Hashable");
    }
    /// <summary>
    /// Tests Analyze_Record_ImplicitConformanceDoesNotBreakExplicitObeys.
    /// </summary>

    [Fact]
    public void Analyze_Record_ImplicitConformanceDoesNotBreakExplicitObeys()
    {
        // A record that explicitly declares 'obeys Equatable' should still work
        // even though RecordType also includes Equatable transitively
        string source = """
                        protocol Equatable
                          @readonly
                          routine Me.$eq(you: Me) -> Bool

                        record Point obeys Equatable
                          x: S32
                          y: S32

                        @readonly
                        routine Point.$eq(you: Point) -> Bool
                          return me.x == you.x and me.y == you.y
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests Analyze_InnateOverride_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_InnateOverride_ReportsError()
    {
        // @innate methods cannot be overridden by user code
        string source = """
                        protocol Identifiable
                          @[readonly, innate]
                          routine Me.$same(you: Me) -> Bool

                        entity Widget obeys Identifiable
                          name: Text

                        @readonly
                        routine Widget.$same(you: Widget) -> Bool
                          return me.name == you.name
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0,
            userMessage: "Expected error for overriding @innate routine");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "innate",
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }
}
