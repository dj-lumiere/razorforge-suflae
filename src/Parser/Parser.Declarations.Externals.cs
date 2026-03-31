namespace Compiler.Parser;

using Lexer;
using SyntaxTree;
using Diagnostics;
using SemanticAnalysis.Enums;

public partial class Parser
{
    private ExternalDeclaration ParseExternalDeclaration(string? callingConvention = null,
        List<string>? annotations = null, bool isDangerous = false)
    {
        if (_language == Language.Suflae)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.RFOnlyConstruct,
                message: "External declarations are only available in RazorForge.");
        }

        SourceLocation
            location =
                GetLocation(
                    token: PeekToken(
                        offset: -2)); // -2 because we consumed 'external' and 'routine'

        string name = ConsumeIdentifier(errorMessage: "Expected routine name");

        // Support module-qualified names like Console.print
        while (Match(type: TokenType.Dot))
        {
            string part = ConsumeIdentifier(errorMessage: "Expected identifier after '.'");
            name = name + "." + part;
        }

        // Support ! suffix for failable routines
        if (Match(type: TokenType.Bang))
        {
            name += "!";
        }

        // Check for generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.LeftBracket))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints)
                result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.RightBracket,
                errorMessage: "Expected ']' after generic parameters");
        }

        // Parameters
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after routine name");
        var parameters = new List<Parameter>();
        bool isVariadic = false;

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                // Check for variadic marker (...)
                if (Match(type: TokenType.DotDotDot))
                {
                    isVariadic = true;
                    break; // ... must be last
                }

                string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name");
                Consume(type: TokenType.Colon, errorMessage: "Expected ':' after parameter name");
                TypeExpression paramType = ParseType();
                parameters.Add(item: new Parameter(Name: paramName,
                    Type: paramType,
                    DefaultValue: null,
                    Location: GetLocation()));
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after parameters");

        // Return type
        TypeExpression? returnType = null;
        if (Match(type: TokenType.Arrow))
        {
            returnType = ParseType();
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints =
            ParseGenericConstraints(genericParams: genericParams,
                existingConstraints: inlineConstraints);

        ConsumeStatementTerminator();

        // Default to "C" calling convention if not specified
        string effectiveCallingConvention = callingConvention ?? "C";

        return new ExternalDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Parameters: parameters,
            ReturnType: returnType,
            CallingConvention: effectiveCallingConvention,
            IsVariadic: isVariadic,
            Annotations: annotations,
            IsDangerous: isDangerous,
            Location: location);
    }

    /// <summary>
    /// Parses an external block declaration grouping multiple external routines under one calling convention.
    /// RF-only construct. Syntax: <c>external("C")</c> followed by an indented block of routine declarations.
    /// Uses INDENT/DEDENT for the block structure.
    /// </summary>
    /// <param name="callingConvention">The calling convention (e.g., "C").</param>
    /// <param name="isDangerous">Whether all routines in the block are marked as dangerous.</param>
    /// <returns>An <see cref="ExternalBlockDeclaration"/> AST node.</returns>
    private ExternalBlockDeclaration ParseExternalBlockDeclaration(string? callingConvention,
        bool isDangerous)
    {
        if (_language == Language.Suflae)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.RFOnlyConstruct,
                message: "External block declarations are only available in RazorForge.");
        }

        SourceLocation blockLocation = GetLocation();

        // Expect a newline followed by an indented block
        Consume(type: TokenType.Newline,
            errorMessage: "Expected newline after external block header");

        var declarations = new List<Declaration>();

        if (Check(type: TokenType.Indent))
        {
            ProcessIndentToken();

            while (!Check(type: TokenType.Dedent) && !IsAtEnd)
            {
                if (Match(TokenType.Newline, TokenType.DocComment))
                {
                    continue;
                }

                // Per-routine dangerous modifier inside the block
                bool routineDangerous = isDangerous || Match(type: TokenType.Dangerous);
                Consume(type: TokenType.Routine,
                    errorMessage: "Expected 'routine' inside external block");
                declarations.Add(item: ParseExternalDeclaration(
                    callingConvention: callingConvention,
                    annotations: null,
                    isDangerous: routineDangerous));
            }

            if (Check(type: TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedentAfterBody,
                    message: "Expected dedent after external block");
            }
        }

        return new ExternalBlockDeclaration(Declarations: declarations, Location: blockLocation);
    }

    /// <summary>
    /// Parses visibility and storage class modifiers.
    /// Visibility: posted, secret, external
    /// Storage: common, global
    /// These are orthogonal and can be combined: posted common, secret common, etc.
    /// </summary>
    /// <returns>A tuple of (visibility, storage) modifiers.</returns>
}
