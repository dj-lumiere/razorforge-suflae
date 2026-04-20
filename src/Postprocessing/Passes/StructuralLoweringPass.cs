using SyntaxTree;

using Compiler.Postprocessing;
namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Lowers structural type declarations to plain record/entity forms.
/// Must run BEFORE <see cref="ControlFlowLoweringPass"/> and <see cref="PatternLoweringPass"/>
/// because pattern matching on these types uses the lowered record fields.
///
/// <para>Transformations applied:</para>
/// <list type="bullet">
///   <item><c>choice</c> ??<c>record</c> with a <c>secret _underlying: S64</c> backing field plus choice methods.</item>
///   <item><c>flags</c>  ??<c>record</c> with a <c>secret _bits: U64</c> backing field.</item>
///   <item><c>variant</c> ??<c>record</c> with <c>secret _type_id: U64</c> and <c>secret _payload: Address</c> fields.</item>
///   <item><c>crashable</c> ??<c>entity</c> declaration with the <c>Crashable</c> protocol added to its protocol list.</item>
/// </list>
///
/// <para>The TypeRegistry is intentionally left unchanged ??codegen continues using
/// <c>ChoiceTypeInfo</c>, <c>FlagsTypeInfo</c>, etc. for LLVM emission. This pass
/// only transforms the AST declarations as groundwork for <see cref="WiredRoutinePass"/>
/// (Step 4), which will generate <c>$represent</c>/<c>$diagnose</c> bodies that
/// reference the lowered backing fields.</para>
///
/// <para>Once <see cref="WiredRoutinePass"/> is implemented, the code generator's
/// carrier helpers (<c>IsCarrierType</c>, <c>GetCarrierKind</c>, etc.) in
/// <c>LLVMCodeGenerator.Types.cs</c> can be removed.</para>
/// </summary>
internal sealed class StructuralLoweringPass(PostprocessingContext ctx)
{
    public void Run(Program program)
    {
        List<IAstNode> decls = program.Declarations;
        for (int i = 0; i < decls.Count; i++)
        {
            decls[i] = decls[i] switch
            {
                ChoiceDeclaration choice => LowerChoice(choice),
                FlagsDeclaration flags => LowerFlags(flags),
                VariantDeclaration variant => LowerVariant(variant),
                CrashableDeclaration crashable => LowerCrashable(crashable),
                _ => decls[i]
            };
        }
    }

    /// <summary>
    /// choice Name { ... } ??record Name with secret _underlying: S64 backing field
    /// plus all choice methods carried over as members.
    /// </summary>
    private static RecordDeclaration LowerChoice(ChoiceDeclaration choice)
    {
        var underlyingField = new VariableDeclaration(
            Name: "_underlying",
            Type: new TypeExpression(
                Name: "S64",
                GenericArguments: null,
                Location: choice.Location),
            Initializer: null,
            Visibility: VisibilityModifier.Secret,
            Location: choice.Location);

        var members = new List<SyntaxTree.Declaration>(capacity: 1 + choice.Methods.Count) { underlyingField };
        members.AddRange(choice.Methods);

        return new RecordDeclaration(
            Name: choice.Name,
            GenericParameters: null,
            Protocols: [],
            Members: members,
            Visibility: choice.Visibility,
            Location: choice.Location);
    }

    /// <summary>
    /// flags Name { ... } ??record Name with secret _bits: U64 backing field.
    /// </summary>
    private static RecordDeclaration LowerFlags(FlagsDeclaration flags)
    {
        var bitsField = new VariableDeclaration(
            Name: "_bits",
            Type: new TypeExpression(
                Name: "U64",
                GenericArguments: null,
                Location: flags.Location),
            Initializer: null,
            Visibility: VisibilityModifier.Secret,
            Location: flags.Location);

        return new RecordDeclaration(
            Name: flags.Name,
            GenericParameters: null,
            Protocols: [],
            Members: [bitsField],
            Visibility: flags.Visibility,
            Location: flags.Location);
    }

    /// <summary>
    /// variant Name[T] { ... } ??record Name[T] with secret _type_id: U64 and secret _payload: Address.
    /// Generic parameters and constraints are preserved.
    /// </summary>
    private static RecordDeclaration LowerVariant(VariantDeclaration variant)
    {
        var typeIdField = new VariableDeclaration(
            Name: "_type_id",
            Type: new TypeExpression(
                Name: "U64",
                GenericArguments: null,
                Location: variant.Location),
            Initializer: null,
            Visibility: VisibilityModifier.Secret,
            Location: variant.Location);

        var payloadField = new VariableDeclaration(
            Name: "_payload",
            Type: new TypeExpression(
                Name: "Address",
                GenericArguments: null,
                Location: variant.Location),
            Initializer: null,
            Visibility: VisibilityModifier.Secret,
            Location: variant.Location);

        return new RecordDeclaration(
            Name: variant.Name,
            GenericParameters: variant.GenericParameters,
            Protocols: [],
            Members: [typeIdField, payloadField],
            Visibility: VisibilityModifier.Open,
            Location: variant.Location,
            GenericConstraints: variant.GenericConstraints);
    }

    /// <summary>
    /// crashable Name { ... } ??entity Name obeys Crashable { ... }
    /// Members are carried over unchanged.
    /// </summary>
    private static EntityDeclaration LowerCrashable(CrashableDeclaration crashable)
    {
        var crashableProtocol = new TypeExpression(
            Name: "Crashable",
            GenericArguments: null,
            Location: crashable.Location);

        return new EntityDeclaration(
            Name: crashable.Name,
            GenericParameters: null,
            Protocols: [crashableProtocol],
            Members: crashable.Members,
            Visibility: crashable.Visibility,
            Location: crashable.Location);
    }
}
