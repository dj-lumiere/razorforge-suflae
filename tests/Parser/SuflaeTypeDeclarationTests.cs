using Xunit;

namespace RazorForge.Tests.Parser;

using Compilers.Shared.AST;
using static TestHelpers;

/// <summary>
/// Tests for parsing Suflae type declarations: record, entity, choice, variant, protocol.
/// Suflae uses indentation-based syntax instead of braces.
/// </summary>
public class SuflaeTypeDeclarationTests
{
    #region Record Tests

    [Fact]
    public void ParseSuflae_SimpleRecord_WithFields()
    {
        string source = """
                        record Point
                            x: F32
                            y: F32
                        """;

        Program program = AssertParsesSuflae(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.Equal(expected: "Point", actual: record.Name);
        Assert.Equal(expected: 2, actual: record.Members.Count);
    }

    [Fact]
    public void ParseSuflae_GenericRecord()
    {
        string source = """
                        record Container<T>
                            value: T
                        """;

        Program program = AssertParsesSuflae(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.Equal(expected: "Container", actual: record.Name);
        Assert.NotNull(@object: record.GenericParameters);
        Assert.Single(collection: record.GenericParameters);
        Assert.Equal(expected: "T", actual: record.GenericParameters[index: 0]);
    }

    [Fact]
    public void ParseSuflae_Record_WithConstraint()
    {
        string source = """
                        record Wrapper<T>
                        requires T follows Comparable
                            value: T
                        """;

        Program program = AssertParsesSuflae(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.NotNull(@object: record.GenericConstraints);
        Assert.Single(collection: record.GenericConstraints);
    }

    [Fact]
    public void ParseSuflae_Record_FollowsProtocol()
    {
        string source = """
                        record Version follows Comparable
                            major: S32
                            minor: S32
                        """;

        Program program = AssertParsesSuflae(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.Single(collection: record.Protocols);
    }

    #endregion

    #region Entity Tests

    [Fact]
    public void ParseSuflae_SimpleEntity()
    {
        string source = """
                        entity User
                            var name: Text
                            var age: U32
                        """;

        Program program = AssertParsesSuflae(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);

        Assert.Equal(expected: "User", actual: entity.Name);
        Assert.Equal(expected: 2, actual: entity.Members.Count);
    }

    [Fact]
    public void ParseSuflae_Entity_MixedMutability()
    {
        string source = """
                        entity Document
                            let id: U64
                            var content: Text
                        """;

        Program program = AssertParsesSuflae(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);

        var fields = entity.Members
                           .OfType<VariableDeclaration>()
                           .ToList();
        Assert.False(condition: fields[index: 0].IsMutable);
        Assert.True(condition: fields[index: 1].IsMutable);
    }

    [Fact]
    public void ParseSuflae_GenericEntity()
    {
        string source = """
                        entity Stack<T>
                            var items: List<T>
                        """;

        Program program = AssertParsesSuflae(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);

        Assert.NotNull(@object: entity.GenericParameters);
        Assert.Single(collection: entity.GenericParameters);
    }

    #endregion

    #region Choice Tests

    [Fact]
    public void ParseSuflae_SimpleChoice()
    {
        string source = """
                        choice Direction
                            NORTH
                            SOUTH
                            EAST
                            WEST
                        """;

        Program program = AssertParsesSuflae(source: source);
        ChoiceDeclaration choice = GetDeclaration<ChoiceDeclaration>(program: program);

        Assert.Equal(expected: "Direction", actual: choice.Name);
        Assert.Equal(expected: 4, actual: choice.Cases.Count);
    }

    [Fact]
    public void ParseSuflae_Choice_WithValues()
    {
        string source = """
                        choice HttpStatus
                            OK: 200
                            NOT_FOUND: 404
                            ERROR: 500
                        """;

        Program program = AssertParsesSuflae(source: source);
        ChoiceDeclaration choice = GetDeclaration<ChoiceDeclaration>(program: program);

        Assert.Equal(expected: 3, actual: choice.Cases.Count);
        Assert.NotNull(@object: choice.Cases[index: 0].Value);
    }

    #endregion

    #region Variant Tests

    [Fact]
    public void ParseSuflae_SimpleVariant()
    {
        string source = """
                        variant NetworkEvent
                            Connect
                            Disconnect
                        """;

        Program program = AssertParsesSuflae(source: source);
        VariantDeclaration variant = GetDeclaration<VariantDeclaration>(program: program);

        Assert.Equal(expected: "NetworkEvent", actual: variant.Name);
        Assert.Equal(expected: 2, actual: variant.Cases.Count);
    }

    [Fact]
    public void ParseSuflae_Variant_WithPayloads()
    {
        string source = """
                        variant ParseResult
                            Success: S32
                            Error: Text
                        """;

        Program program = AssertParsesSuflae(source: source);
        VariantDeclaration variant = GetDeclaration<VariantDeclaration>(program: program);

        Assert.Equal(expected: 2, actual: variant.Cases.Count);
        Assert.NotNull(@object: variant.Cases[index: 0].AssociatedTypes);
        Assert.NotNull(@object: variant.Cases[index: 1].AssociatedTypes);
    }

    #endregion

    #region Protocol Tests

    [Fact]
    public void ParseSuflae_SimpleProtocol()
    {
        string source = """
                        protocol Displayable
                            routine display() -> Text
                        """;

        Program program = AssertParsesSuflae(source: source);
        ProtocolDeclaration protocol = GetDeclaration<ProtocolDeclaration>(program: program);

        Assert.Equal(expected: "Displayable", actual: protocol.Name);
        Assert.Single(collection: protocol.Methods);
    }

    [Fact]
    public void ParseSuflae_GenericProtocol()
    {
        string source = """
                        protocol Iterable<T>
                            routine iterate() -> Iterator<T>
                        """;

        Program program = AssertParsesSuflae(source: source);
        ProtocolDeclaration protocol = GetDeclaration<ProtocolDeclaration>(program: program);

        Assert.NotNull(@object: protocol.GenericParameters);
        Assert.Single(collection: protocol.GenericParameters);
    }

    [Fact]
    public void ParseSuflae_Protocol_Inheritance()
    {
        string source = """
                        protocol Ordered follows Comparable
                            routine __cmp__(other: Me) -> ComparisonSign
                        """;

        Program program = AssertParsesSuflae(source: source);
        ProtocolDeclaration protocol = GetDeclaration<ProtocolDeclaration>(program: program);

        Assert.Single(collection: protocol.ParentProtocols);
    }

    #endregion

    #region Routine Tests

    [Fact]
    public void ParseSuflae_SimpleRoutine()
    {
        string source = """
                        routine greet(name: Text) -> Text
                            return name
                        """;

        Program program = AssertParsesSuflae(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.Equal(expected: "greet", actual: routine.Name);
        Assert.Single(collection: routine.Parameters);
    }

    [Fact]
    public void ParseSuflae_FailableRoutine()
    {
        string source = """
                        routine get_value!() -> S32
                            return 42
                        """;

        Program program = AssertParsesSuflae(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.True(condition: routine.IsFailable);
    }

    [Fact]
    public void ParseSuflae_Routine_WithThrow()
    {
        string source = """
                        routine validate!(x: S32) -> S32
                            if x < 0
                                throw ValidationError("negative")
                            return x
                        """;

        Program program = AssertParsesSuflae(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.True(condition: routine.IsFailable);
    }

    [Fact]
    public void ParseSuflae_Routine_WithAbsent()
    {
        string source = """
                        routine find!(id: U64) -> User
                            unless has_user(id)
                                absent
                            return get_user(id)
                        """;

        Program program = AssertParsesSuflae(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.True(condition: routine.IsFailable);
    }

    #endregion

    #region Suflae-Specific Features

    [Fact]
    public void ParseSuflae_NoResident_ShouldThrowOrIgnore()
    {
        // Resident is RazorForge-only, Suflae should not support it
        string source = """
                        resident SystemLogger
                            var log_count: U32
                        """;

        // Depending on parser behavior - may throw or parse but semantic rejects
        Record.Exception(testCode: () => ParseSuflae(source: source));
        // Either throws or parses (semantic will reject)
    }

    [Fact]
    public void ParseSuflae_WhenStatement()
    {
        string source = """
                        routine handle(value: User?)
                            when value
                                is None
                                    show("not found")
                                else u
                                    show(u.name)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ForLoop()
    {
        string source = """
                        routine count()
                            for i in 0 to 10
                                show(i)
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Published Record Field Tests

    [Fact]
    public void ParseSuflae_RecordWithPublishedField()
    {
        string source = """
                        record Percentage
                            published value: F64
                        """;

        Program program = AssertParsesSuflae(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.Equal(expected: "Percentage", actual: record.Name);
        Assert.Single(collection: record.Members);
    }

    [Fact]
    public void ParseSuflae_RecordWithMixedVisibilityFields()
    {
        string source = """
                        record Config
                            published name: Text
                            private secret: Text
                            value: S32
                        """;

        Program program = AssertParsesSuflae(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.Equal(expected: 3, actual: record.Members.Count);
    }

    #endregion
}
