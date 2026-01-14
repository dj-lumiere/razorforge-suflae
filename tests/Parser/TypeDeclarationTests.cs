using Xunit;

namespace RazorForge.Tests.Parser;

using Compilers.Shared.AST;
using static TestHelpers;

/// <summary>
/// Tests for parsing type declarations: record, entity, resident, choice, variant, protocol.
/// </summary>
public class TypeDeclarationTests
{
    #region Record Tests

    [Fact]
    public void Parse_SimpleRecord_WithFields()
    {
        string source = """
                        record Point {
                            x: F32
                            y: F32
                        }
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.Equal(expected: "Point", actual: record.Name);
        Assert.Equal(expected: 2, actual: record.Members.Count);
    }

    [Fact]
    public void Parse_GenericRecord()
    {
        string source = """
                        record Container<T> {
                            value: T
                        }
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.Equal(expected: "Container", actual: record.Name);
        Assert.NotNull(@object: record.GenericParameters);
        Assert.Single(collection: record.GenericParameters);
        Assert.Equal(expected: "T", actual: record.GenericParameters[index: 0]);
    }

    [Fact]
    public void Parse_Record_WithConstraint()
    {
        string source = """
                        record Wrapper<T>
                        requires T follows Comparable {
                            value: T
                        }
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.NotNull(@object: record.GenericConstraints);
        Assert.Single(collection: record.GenericConstraints);
        Assert.Equal(expected: "T", actual: record.GenericConstraints[index: 0].ParameterName);
    }

    [Fact]
    public void Parse_Record_FollowsProtocol()
    {
        string source = """
                        record Version follows Comparable {
                            major: S32
                            minor: S32
                        }
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.Single(collection: record.Protocols);
    }

    [Fact]
    public void Parse_Record_MultipleTypeParameters()
    {
        string source = """
                        record Pair<K, V> {
                            key: K
                            value: V
                        }
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.NotNull(@object: record.GenericParameters);
        Assert.Equal(expected: 2, actual: record.GenericParameters.Count);
        Assert.Equal(expected: "K", actual: record.GenericParameters[index: 0]);
        Assert.Equal(expected: "V", actual: record.GenericParameters[index: 1]);
    }

    #endregion

    #region Entity Tests

    [Fact]
    public void Parse_SimpleEntity()
    {
        string source = """
                        entity User {
                            var name: Text
                            var age: U32
                        }
                        """;

        Program program = AssertParses(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);

        Assert.Equal(expected: "User", actual: entity.Name);
        Assert.Equal(expected: 2, actual: entity.Members.Count);
    }

    [Fact]
    public void Parse_GenericEntity()
    {
        string source = """
                        entity Stack<T> {
                            var items: List<T>
                        }
                        """;

        Program program = AssertParses(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);

        Assert.NotNull(@object: entity.GenericParameters);
        Assert.Single(collection: entity.GenericParameters);
        Assert.Equal(expected: "T", actual: entity.GenericParameters[index: 0]);
    }

    [Fact]
    public void Parse_Entity_MultipleConstraints()
    {
        string source = """
                        entity SortedCache<K, V>
                        requires K follows Comparable
                        requires K follows Hashable
                        requires V is entity {
                            var entries: Dict<K, V>
                        }
                        """;

        Program program = AssertParses(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);

        Assert.NotNull(@object: entity.GenericParameters);
        Assert.Equal(expected: 2, actual: entity.GenericParameters.Count);
        Assert.NotNull(@object: entity.GenericConstraints);
        Assert.Equal(expected: 3, actual: entity.GenericConstraints.Count);
    }

    #endregion

    #region Resident Tests

    [Fact]
    public void Parse_SimpleResident()
    {
        string source = """
                        resident SystemLogger {
                            var log_count: U32
                        }
                        """;

        Program program = AssertParses(source: source);
        ResidentDeclaration resident = GetDeclaration<ResidentDeclaration>(program: program);

        Assert.Equal(expected: "SystemLogger", actual: resident.Name);
    }

    [Fact]
    public void Parse_GenericResident_WithConstGeneric()
    {
        string source = """
                        resident FixedBuffer<T, N>
                        requires N is uaddr {
                            var data: T
                        }
                        """;

        Program program = AssertParses(source: source);
        ResidentDeclaration resident = GetDeclaration<ResidentDeclaration>(program: program);

        Assert.NotNull(@object: resident.GenericParameters);
        Assert.Equal(expected: 2, actual: resident.GenericParameters.Count);
    }

    #endregion

    #region Choice Tests

    [Fact]
    public void Parse_SimpleChoice()
    {
        string source = """
                        choice Direction {
                            NORTH
                            SOUTH
                            EAST
                            WEST
                        }
                        """;

        Program program = AssertParses(source: source);
        ChoiceDeclaration choice = GetDeclaration<ChoiceDeclaration>(program: program);

        Assert.Equal(expected: "Direction", actual: choice.Name);
        Assert.Equal(expected: 4, actual: choice.Cases.Count);
        Assert.Equal(expected: "NORTH", actual: choice.Cases[index: 0].Name);
    }

    [Fact]
    public void Parse_Choice_WithValues()
    {
        string source = """
                        choice HttpStatus {
                            OK: 200
                            NOT_FOUND: 404
                            ERROR: 500
                        }
                        """;

        Program program = AssertParses(source: source);
        ChoiceDeclaration choice = GetDeclaration<ChoiceDeclaration>(program: program);

        Assert.Equal(expected: 3, actual: choice.Cases.Count);
        Assert.NotNull(@object: choice.Cases[index: 0].Value);
    }

    #endregion

    #region Variant Tests

    [Fact]
    public void Parse_SimpleVariant()
    {
        string source = """
                        variant NetworkEvent {
                            Connect
                            Disconnect
                        }
                        """;

        Program program = AssertParses(source: source);
        VariantDeclaration variant = GetDeclaration<VariantDeclaration>(program: program);

        Assert.Equal(expected: "NetworkEvent", actual: variant.Name);
        Assert.Equal(expected: 2, actual: variant.Cases.Count);
    }

    [Fact]
    public void Parse_Variant_WithPayloads()
    {
        string source = """
                        variant ParseResult {
                            Success: S32
                            Error: Text
                        }
                        """;

        Program program = AssertParses(source: source);
        VariantDeclaration variant = GetDeclaration<VariantDeclaration>(program: program);

        Assert.Equal(expected: 2, actual: variant.Cases.Count);
        Assert.NotNull(@object: variant.Cases[index: 0].AssociatedTypes);
        Assert.NotNull(@object: variant.Cases[index: 1].AssociatedTypes);
    }

    [Fact]
    public void Parse_Variant_MixedPayloads()
    {
        string source = """
                        variant Event {
                            Connect
                            Data: Text
                            Error: U32
                            Nothing
                        }
                        """;

        Program program = AssertParses(source: source);
        VariantDeclaration variant = GetDeclaration<VariantDeclaration>(program: program);

        Assert.Equal(expected: 4, actual: variant.Cases.Count);
        Assert.Null(@object: variant.Cases[index: 0].AssociatedTypes); // CONNECT - no payload
        Assert.NotNull(@object: variant.Cases[index: 1].AssociatedTypes); // DATA: Text
        Assert.NotNull(@object: variant.Cases[index: 2].AssociatedTypes); // ERROR: U32
        Assert.Null(@object: variant.Cases[index: 3].AssociatedTypes); // NONE - no payload
    }

    #endregion

    #region Protocol Tests

    [Fact]
    public void Parse_SimpleProtocol()
    {
        string source = """
                        protocol Displayable {
                            @readonly
                            routine Me.display() -> Text
                        }
                        """;

        Program program = AssertParses(source: source);
        ProtocolDeclaration protocol = GetDeclaration<ProtocolDeclaration>(program: program);

        Assert.Equal(expected: "Displayable", actual: protocol.Name);
        Assert.Single(collection: protocol.Methods);
    }

    [Fact]
    public void Parse_Protocol_MultipleMethods()
    {
        string source = """
                        protocol Container {
                            @readonly
                            routine Me.count() -> uaddr

                            @readonly
                            routine Me.is_empty() -> bool
                        }
                        """;

        Program program = AssertParses(source: source);
        ProtocolDeclaration protocol = GetDeclaration<ProtocolDeclaration>(program: program);

        Assert.Equal(expected: 2, actual: protocol.Methods.Count);
    }

    [Fact]
    public void Parse_GenericProtocol()
    {
        string source = """
                        protocol Iterable<T> {
                            @readonly
                            routine Me.iterate() -> Iterator<T>
                        }
                        """;

        Program program = AssertParses(source: source);
        ProtocolDeclaration protocol = GetDeclaration<ProtocolDeclaration>(program: program);

        Assert.NotNull(@object: protocol.GenericParameters);
        Assert.Single(collection: protocol.GenericParameters);
        Assert.Equal(expected: "T", actual: protocol.GenericParameters[index: 0]);
    }

    [Fact]
    public void Parse_Protocol_Inheritance()
    {
        string source = """
                        protocol Ordered follows Comparable {
                            @readonly
                            routine Me.__cmp__(other: Me) -> ComparisonSign
                        }
                        """;

        Program program = AssertParses(source: source);
        ProtocolDeclaration protocol = GetDeclaration<ProtocolDeclaration>(program: program);

        Assert.Single(collection: protocol.ParentProtocols);
    }

    #endregion

    #region Visibility Tests

    [Fact]
    public void Parse_PrivateRecord()
    {
        string source = """
                        private record InternalData {
                            value: S32
                        }
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.Equal(expected: VisibilityModifier.Private, actual: record.Visibility);
    }

    [Fact]
    public void Parse_InternalEntity()
    {
        string source = """
                        internal entity CacheEntry {
                            var data: Text
                        }
                        """;

        Program program = AssertParses(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);

        Assert.Equal(expected: VisibilityModifier.Internal, actual: entity.Visibility);
    }

    #endregion

    #region Routine Tests

    [Fact]
    public void Parse_SimpleRoutine()
    {
        string source = """
                        routine greet(name: Text) -> Text {
                            return name
                        }
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.Equal(expected: "greet", actual: routine.Name);
        Assert.Single(collection: routine.Parameters);
    }

    [Fact]
    public void Parse_FailableRoutine()
    {
        string source = """
                        routine get_value!() -> S32 {
                            return 42
                        }
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.Equal(expected: "get_value", actual: routine.Name);
        Assert.True(condition: routine.IsFailable);
    }

    #endregion
}
