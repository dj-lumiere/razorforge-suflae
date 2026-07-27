using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    /// <summary>
    /// Suflae flow typing: determines whether the VALUE produced by reading <paramref name="expr"/> is a
    /// possibly-none (`E?`) entity reference. Sources of none-ness:
    /// <list type="bullet">
    /// <item>the <c>none</c> literal;</item>
    /// <item>a read of a nullable entity field (<c>obj.optField</c> where the field was declared <c>x: E?</c>);</item>
    /// <item>a read of a nullable local that has not yet been proven non-none in this flow.</item>
    /// </list>
    /// Constructions (<c>E(...)</c>), non-null field reads, and routine returns of entity type are non-none.
    /// Only meaningful for Suflae; always false for RazorForge.
    /// </summary>
    private bool IsNullableEntityRead(Expression expr)
    {
        if (_registry.Language != Language.Suflae)
        {
            return false;
        }

        switch (expr)
        {
            // `none` literal
            case LiteralExpression { LiteralType: TokenType.NoneValue }:
                return true;

            // A nullable local, unless flow analysis has already proven it non-none here.
            case IdentifierExpression id:
                return _registry.LookupVariable(name: id.Name) is { IsNullable: true } &&
                    !_registry.IsVariableProvenNonNull(name: id.Name);

            // A read of an optional entity field (`obj.optField`). The field's IsNullable is set in
            // TypeBodyResolver. At SA time the object type is the bare EntityTypeInfo (Roamed lowering
            // is a later phase), so look the field up directly on the entity.
            case MemberExpression m:
            {
                TypeSymbol objType = m.Object.ResolvedType ?? AnalyzeExpression(expression: m.Object);
                return objType is EntityTypeInfo entity &&
                    entity.LookupMemberVariable(memberVariableName: m.PropertyName) is { IsNullable: true };
            }

            default:
                return false;
        }
    }
}
