using Verification.Results;
using TypeModel.Types;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for marker conformance.
/// </summary>
public class MarkerConformanceTests
{
    /// <summary>
    /// Verifies semantic analysis behavior for record has record type conformance.
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
    /// Verifies semantic analysis behavior for record has transitive protocols.
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
    /// Verifies semantic analysis behavior for entity has entity type conformance.
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
    }
    /// <summary>
    /// Verifies semantic analysis behavior for choice has choice type conformance.
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
    /// Verifies semantic analysis behavior for flags has flags type conformance.
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
    /// Verifies semantic analysis behavior for record implicit conformance does not break explicit obeys.
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
    /// Verifies semantic analysis behavior for innate override and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_InnateOverride_ReportsError()
    {
        // @innate methods cannot be overridden by user code
        string source = """
                        protocol Lockable
                          @[readonly, innate]
                          routine Me.$eq(you: Me) -> Bool

                        entity Widget obeys Lockable
                          name: Text

                        @readonly
                        routine Widget.$eq(you: Widget) -> Bool
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
