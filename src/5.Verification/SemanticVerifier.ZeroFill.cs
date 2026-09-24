using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

public sealed partial class SemanticVerifier
{
    /// <summary>
    /// RF-S641. <c>Array[T, N]()</c> fills every slot with zero bits. That is a valid value for numbers,
    /// raw pointers, and shared buffers whose null controller means empty (Text, Bytes, Integer, ...), but
    /// not for an entity or a reference-counted handle: a zero slot there is a null handle that crashes
    /// when it is read or torn down at scope exit. Such arrays must be built from their elements.
    /// </summary>
    private void ValidateZeroFilledArray(TypeSymbol constructed, int argumentCount, SourceLocation location)
    {
        if (argumentCount != 0 ||
            constructed is not RecordTypeSymbol { GenericDefinition.Name: "Array", TypeArguments: [var element, ..] } ||
            DescribeNoZeroValue(type: element, seen: []) is not { } blocker)
        {
            return;
        }

        ReportError(code: SemanticDiagnosticCode.ArrayElementHasNoZeroValue,
            message:
            $"'{constructed.Name}()' fills every slot with zero, but '{element.Name}' has no zero value " +
            $"({blocker}), so each slot would be a null handle that crashes when it is read or torn down. " +
            $"Build the array from its elements instead, for example 'Array[2](a, b)'.",
            location: location);
    }

    /// <summary>
    /// Why <paramref name="type"/> has no valid all-zero value, or null when zero bits are a real value of
    /// it. Records, tuples and variants have one when every part does.
    /// </summary>
    private static string? DescribeNoZeroValue(TypeSymbol type, HashSet<string> seen)
    {
        if (!seen.Add(item: type.FullName))
        {
            return null;
        }

        switch (type)
        {
            case EntityTypeSymbol or CrashableTypeSymbol:
                return $"'{type.Name}' is an entity";
            case WrapperTypeSymbol or RecordTypeSymbol
                when Declaration.TypeRegistry.GetRcWrapperBaseName(type: type) != null:
                return $"'{type.Name}' is a reference-counted handle";
            case TupleTypeSymbol tuple:
                return tuple.ElementTypes
                            .Select(selector: t => DescribeNoZeroValue(type: t, seen: seen))
                            .FirstOrDefault(predicate: b => b != null);
            case VariantTypeSymbol variant:
                return variant.Members
                              .Where(predicate: m => m.Type != null)
                              .Select(selector: m => DescribeNoZeroValue(type: m.Type!, seen: seen))
                              .FirstOrDefault(predicate: b => b != null);
            case RecordTypeSymbol { GenericDefinition.Name: "Array", TypeArguments: [var inner, ..] }:
                return DescribeNoZeroValue(type: inner, seen: seen);
            case RecordTypeSymbol record:
                foreach (MemberVariableInfo field in record.MemberVariables)
                {
                    if (DescribeNoZeroValue(type: field.Type, seen: seen) is { } fieldBlocker)
                    {
                        return $"its '{field.Name}': {fieldBlocker}";
                    }
                }

                return null;
            default:
                return null;
        }
    }
}
