using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Types;

namespace Builder.Declaration;

/// <summary>
/// Gives a bare unsuffixed literal (<c>UndecidedInteger</c> / <c>UndecidedDecimal</c>) the concrete literal
/// token of the type analysis resolved it to, so the LLVM emitter never receives an undecided token.
/// Shared by <c>ExpressionLoweringPass</c> (routine bodies) and preset collection (the elements of an
/// aggregate preset, which become a constant global and never pass through lowering).
/// </summary>
internal static class UndecidedLiteralConformance
{
    /// <summary>Returns <paramref name="literal"/> with a concrete literal token when it is undecided.</summary>
    internal static LiteralExpression Conform(LiteralExpression literal)
    {
        return literal.LiteralType switch
        {
            TokenType.UndecidedInteger => literal with
            {
                LiteralType = ResolveInteger(resolved: literal.ResolvedType)
            },
            TokenType.UndecidedDecimal => literal with
            {
                LiteralType = ResolveDecimal(resolved: literal.ResolvedType)
            },
            _ => literal
        };
    }

    /// <summary>Maps an undecided integer literal's resolved type to its concrete integer token.</summary>
    internal static TokenType ResolveInteger(TypeSymbol? resolved)
    {
        return resolved?.Name switch
        {
            "S8" => TokenType.S8Literal,
            "S16" => TokenType.S16Literal,
            "S32" => TokenType.S32Literal,
            "S128" => TokenType.S128Literal,
            "S256" => TokenType.S256Literal,
            "U8" => TokenType.U8Literal,
            "U16" => TokenType.U16Literal,
            "U32" => TokenType.U32Literal,
            "U64" => TokenType.U64Literal,
            "U128" => TokenType.U128Literal,
            "U256" => TokenType.U256Literal,
            "Address" => TokenType.AddressLiteral,
            "Integer" => TokenType.IntegerLiteral,
            _ => TokenType
               .S64Literal // This should be language specific: Suflae should use IntegerLiteral
        };
    }

    /// <summary>Maps an undecided decimal literal's resolved type to its concrete float/decimal token.</summary>
    internal static TokenType ResolveDecimal(TypeSymbol? resolved)
    {
        return resolved?.Name switch
        {
            "B16" => TokenType.B16Literal,
            "B32" => TokenType.B32Literal,
            "B128" => TokenType.B128Literal,
            "D32" => TokenType.D32Literal,
            "D64" => TokenType.D64Literal,
            "D128" => TokenType.D128Literal,
            "Decimal" => TokenType.DecimalLiteral,
            _ => TokenType
               .B64Literal // This should be language specific: Suflae should use DecimalLiteral
        };
    }
}
