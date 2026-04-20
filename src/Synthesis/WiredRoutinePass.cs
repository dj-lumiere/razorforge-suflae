using Compiler.Lexer;
using Compiler.Desugaring;
using Compiler.Postprocessing.Passes;
using Compiler.Targeting;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using SyntaxTree;

namespace Compiler.Synthesis;

/// <summary>
/// Generates real AST bodies for <c>IsSynthesized = true</c> stub routines on record, entity,
/// and crashable types. Runs as a global pass after all per-file desugaring (Phase 4a).
///
/// <para>Generated bodies (keyed by <c>RoutineInfo.RegistryKey</c> ??<c>ctx.VariantBodies</c>):</para>
/// <list type="bullet">
///   <item><c>$eq</c>   ??field-by-field <c>==</c> AND-chain for concrete <see cref="RecordTypeInfo"/>.</item>
///   <item><c>$represent</c> / <c>$diagnose</c> ??f-string body for <see cref="RecordTypeInfo"/> and
///         <see cref="EntityTypeInfo"/>, including generic definitions (monomorphization substitutes type params).</item>
///   <item><c>$represent</c> on crashable ??<c>return me.crash_message()</c>.</item>
///   <item><c>$diagnose</c> on crashable ??f-string <c>Module.Name(crash_message, field: val, ...)</c>.</item>
///   <item><c>$hash</c> on concrete multi-field records ??XOR-chain of <c>me.f.$hash()</c> calls.</item>
/// </list>
///
/// <para>Skipped (still handled by <c>EmitSynthesized*</c> in codegen):</para>
/// <list type="bullet">
///   <item><c>$hash</c> on <see cref="ChoiceTypeInfo"/> / <see cref="FlagsTypeInfo"/> / single-member wrappers ??Knuth multiplicative hash; type-conversion not expressible in plain RF AST.</item>
///   <item><see cref="VariantTypeInfo"/>
///         ??pattern dispatch on numeric value; not expressible in plain AST.</item>
///   <item>Records with <c>HasDirectBackendType</c> ??no RF member variables (intrinsic types).</item>
///   <item><c>Maybe[T]</c> ??<c>$represent</c>/<c>$diagnose</c> defined explicitly in <c>Core/Errors/Maybe.rf</c>
///         (not synthesized).</item>
/// </list>
/// </summary>
public sealed class WiredRoutinePass(DesugaringContext ctx)
{
    private static readonly SourceLocation _synthLoc =
        new(FileName: "", Line: 0, Column: 0, Position: 0);

    public void RunGlobal()
    {
        TypeInfo? textType = ctx.Registry.LookupType(name: "Text");
        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
        TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
        TypeInfo? s64Type = ctx.Registry.LookupType(name: "S64");
        TypeInfo? byteSizeType = ctx.Registry.LookupType(name: "ByteSize");
        TypeInfo? logicBreachedErrorType = ctx.Registry.LookupType(name: "LogicBreachedError");
        TypeInfo? typeKindType = ctx.Registry.LookupType(name: "TypeKind");
        TypeInfo? listTypeDef = ctx.Registry.LookupType(name: "List");
        TypeInfo? listTextType = listTypeDef != null && textType != null
            ? ctx.Registry.GetOrCreateResolution(genericDef: listTypeDef, typeArguments: [textType])
            : null;
        if (textType == null || boolType == null)
            return;

        foreach (RoutineInfo routine in ctx.Registry.GetAllRoutines())
        {
            if (!routine.IsSynthesized) continue;
            if (ctx.RoutineBodies.ContainsKey(key: routine.RegistryKey)) continue;

            // Skip if an explicit (non-synthesized) implementation already exists in the registry.
            // This prevents synthesized bodies from overriding custom stdlib implementations
            // such as Marked[T,P].$represent / $diagnose defined in Marked.rf.
            if (routine.OwnerType != null &&
                ctx.Registry.GetMethodsForType(type: routine.OwnerType)
                    .Any(r => r.Name == routine.Name && !r.IsSynthesized))
                continue;

            // BuilderService constant routines apply to all owner types ??check by name first.
            if (routine.OwnerType != null
                && TryHandleBuilderServiceConstant(routine: routine, textType: textType,
                    u64Type: u64Type, s64Type: s64Type, boolType: boolType,
                    typeKindType: typeKindType, listTextType: listTextType,
                    byteSizeType: byteSizeType))
                continue;

            // Standalone BuilderService constants (no owner type): page_size, target_os, etc.
            if (routine.OwnerType == null
                && TryHandleStandaloneBuilderServiceConstant(routine: routine,
                    textType: textType, u64Type: u64Type, s64Type: s64Type,
                    byteSizeType: byteSizeType))
                continue;

            switch (routine.OwnerType)
            {
                case RecordTypeInfo record:
                    HandleRecord(routine: routine, record: record,
                        textType: textType, boolType: boolType);
                    break;

                case EntityTypeInfo entity:
                    HandleEntity(routine: routine, entity: entity, textType: textType);
                    break;

                case CrashableTypeInfo crashable:
                    HandleCrashable(routine: routine, crashable: crashable, textType: textType);
                    break;

                case ChoiceTypeInfo choice:
                    HandleChoice(routine: routine, choice: choice, textType: textType,
                        boolType: boolType, logicBreachedErrorType: logicBreachedErrorType);
                    break;

                case FlagsTypeInfo flags:
                    HandleFlags(routine: routine, flags: flags,
                        textType: textType, boolType: boolType);
                    break;

                case VariantTypeInfo variant:
                    HandleVariant(routine: routine, variant: variant, textType: textType);
                    break;

                case TupleTypeInfo tuple:
                    HandleTuple(routine: routine, tuple: tuple, textType: textType);
                    break;
            }
        }
    }

    // ?�?� Per-type handlers ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�

    private void HandleRecord(RoutineInfo routine, RecordTypeInfo record,
        TypeInfo textType, TypeInfo boolType)
    {
        if (record.HasDirectBackendType) return;

        switch (routine.Name)
        {
            case "$eq":
            {
                // $eq generation requires knowing the concrete field types at body-gen time.
                // Leave generic definitions to the IR fallback (EmitSynthesizedEq).
                if (record.IsGenericDefinition) break;
                Statement? body = BuildEqBody(record: record, boolType: boolType);
                if (body != null)
                    ctx.VariantBodies[key: routine.RegistryKey] = body;
                break;
            }

            case "$copy":
            {
                // $copy for generic definitions cannot correctly decide whether to call $copy()
                // on generic-param-typed fields. Leave them to the IR fallback.
                if (record.IsGenericDefinition) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildCopyBody(record: record);
                break;
            }

            case "$represent":
                // Generic definitions allowed: monomorphization substitutes type params.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildTextBody(ownerType: record, fields: record.MemberVariables,
                        textType: textType, diagnose: false);
                break;

            case "$diagnose":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildTextBody(ownerType: record, fields: record.MemberVariables,
                        textType: textType, diagnose: true);
                break;

            case "$hash":
            {
                // Skip generic definitions: generic-param-typed fields don't have a $hash to call.
                // Skip single-member wrappers: codegen emits Knuth multiplicative hash (me * 2654435761).
                if (record.IsGenericDefinition || record.IsSingleMemberVariableWrapper) break;
                TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
                if (u64Type == null) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildHashBody(record: record, u64Type: u64Type);
                break;
            }
        }
    }

    private void HandleEntity(RoutineInfo routine, EntityTypeInfo entity, TypeInfo textType)
    {
        // Generic definitions allowed: monomorphization substitutes type params via _typeSubstitutions.
        switch (routine.Name)
        {
            case "$represent":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildTextBody(ownerType: entity, fields: entity.MemberVariables,
                        textType: textType, diagnose: false);
                break;

            case "$diagnose":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildTextBody(ownerType: entity, fields: entity.MemberVariables,
                        textType: textType, diagnose: true);
                break;
        }
    }

    private void HandleCrashable(RoutineInfo routine, CrashableTypeInfo crashable,
        TypeInfo textType)
    {
        switch (routine.Name)
        {
            case "$represent":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildCrashableRepresentBody(crashable: crashable);
                break;

            case "$diagnose":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildCrashableDiagnoseBody(crashable: crashable, textType: textType);
                break;

            case "crash_title":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    new ReturnStatement(
                        Value: new LiteralExpression(
                            Value: crashable.CrashTitle,
                            LiteralType: TokenType.TextLiteral,
                            Location: _synthLoc) { ResolvedType = textType },
                        Location: _synthLoc);
                break;
        }
    }

    private void HandleChoice(RoutineInfo routine, ChoiceTypeInfo choice, TypeInfo textType,
        TypeInfo boolType, TypeInfo? logicBreachedErrorType)
    {
        switch (routine.Name)
        {
            case "$eq":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildEqBodyNumeric(ownerType: choice, boolType: boolType);
                break;

            case "$represent":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildChoiceRepresentBody(choice: choice, textType: textType,
                        logicBreachedErrorType: logicBreachedErrorType);
                break;

            case "$diagnose":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildChoiceDiagnoseBody(choice: choice, textType: textType,
                        logicBreachedErrorType: logicBreachedErrorType);
                break;
        }
    }

    // ?�?� $copy ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�

    /// <summary>
    /// Builds the body: <c>return TypeName(field1: me.field1[.$copy()], ...)</c>.
    /// Record-typed fields call <c>$copy()</c> recursively; primitives are copied by value.
    /// </summary>
    private static Statement BuildCopyBody(RecordTypeInfo record)
    {
        var args = new List<(string Name, Expression Value)>(capacity: record.MemberVariables.Count);

        foreach (MemberVariableInfo field in record.MemberVariables)
        {
            var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
                { ResolvedType = record };
            Expression fieldAccess = new MemberExpression(
                Object: meRef,
                PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            // For record-typed fields, call $copy() to handle RC wrappers correctly.
            // Exception: @llvm-annotated records (HasDirectBackendType = true) are LLVM
            // scalars ??plain value copy is correct; no $copy routine is generated for them.
            // For intrinsics, choices, flags, and everything else, plain value copy is correct.
            Expression fieldValue = field.Type is RecordTypeInfo { HasDirectBackendType: false }
                ? new CallExpression(
                    Callee: new MemberExpression(
                        Object: fieldAccess,
                        PropertyName: "$copy",
                        Location: _synthLoc) { ResolvedType = field.Type },
                    Arguments: [],
                    Location: _synthLoc) { ResolvedType = field.Type }
                : fieldAccess;

            args.Add((field.Name, fieldValue));
        }

        var creator = new CreatorExpression(
            TypeName: record.Name,
            TypeArguments: null,
            MemberVariables: args,
            Location: _synthLoc) { ResolvedType = record };

        return new ReturnStatement(Value: creator, Location: _synthLoc);
    }

    // ?�?� $eq ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�

    /// <summary>
    /// Builds the body: <c>return me == you</c> for choice and flags types.
    /// The <c>BinaryExpression(Equal)</c> lowers to <c>icmp eq i32</c> (choice) or
    /// <c>icmp eq i64</c> (flags) in <c>EmitPrimitiveBinaryOp</c>, bypassing
    /// the <c>EmitSynthesizedEq</c> fallback that previously returned <c>false</c>.
    /// </summary>
    private static Statement BuildEqBodyNumeric(TypeInfo ownerType, TypeInfo boolType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = ownerType };
        var youRef = new IdentifierExpression(Name: "you", Location: _synthLoc)
            { ResolvedType = ownerType };
        var cmp = new BinaryExpression(
            Left: meRef,
            Operator: BinaryOperator.Equal,
            Right: youRef,
            Location: _synthLoc) { ResolvedType = boolType };
        return new ReturnStatement(Value: cmp, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body: <c>return me.f1 == you.f1 and me.f2 == you.f2 and ...</c>
    /// Zero-field records: <c>return true</c>.
    /// Returns <c>null</c> if routine has no <c>you</c> parameter.
    /// </summary>
    private static Statement? BuildEqBody(RecordTypeInfo record, TypeInfo boolType)
    {
        if (record.MemberVariables.Count == 0)
        {
            return new ReturnStatement(
                Value: new LiteralExpression(
                    Value: true,
                    LiteralType: TokenType.True,
                    Location: _synthLoc) { ResolvedType = boolType },
                Location: _synthLoc);
        }

        Expression? combined = null;
        foreach (MemberVariableInfo field in record.MemberVariables)
        {
            var lhs = new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc)
                    { ResolvedType = record },
                PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            var rhs = new MemberExpression(
                Object: new IdentifierExpression(Name: "you", Location: _synthLoc)
                    { ResolvedType = record },
                PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            var cmp = new BinaryExpression(
                Left: lhs,
                Operator: BinaryOperator.Equal,
                Right: rhs,
                Location: _synthLoc) { ResolvedType = boolType };

            combined = combined == null
                ? cmp
                : new BinaryExpression(
                    Left: combined,
                    Operator: BinaryOperator.And,
                    Right: cmp,
                    Location: _synthLoc) { ResolvedType = boolType };
        }

        return new ReturnStatement(Value: combined!, Location: _synthLoc);
    }

    // ?�?� $hash ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�

    /// <summary>
    /// Builds the body: <c>return me.f1.$hash() ^ me.f2.$hash() ^ ...</c>.
    /// Zero-field records: <c>return 0_u64</c>.
    /// </summary>
    private static Statement BuildHashBody(RecordTypeInfo record, TypeInfo u64Type)
    {
        if (record.MemberVariables.Count == 0)
        {
            return new ReturnStatement(
                Value: new LiteralExpression(
                    Value: 0UL,
                    LiteralType: TokenType.U64Literal,
                    Location: _synthLoc) { ResolvedType = u64Type },
                Location: _synthLoc);
        }

        Expression? accum = null;
        foreach (MemberVariableInfo field in record.MemberVariables)
        {
            var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
                { ResolvedType = record };
            var fieldAccess = new MemberExpression(
                Object: meRef,
                PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };
            var hashMethod = new MemberExpression(
                Object: fieldAccess,
                PropertyName: "$hash",
                Location: _synthLoc) { ResolvedType = u64Type };
            Expression fieldHash = new CallExpression(
                Callee: hashMethod,
                Arguments: [],
                Location: _synthLoc) { ResolvedType = u64Type };

            accum = accum == null
                ? fieldHash
                : new BinaryExpression(
                    Left: accum,
                    Operator: BinaryOperator.BitwiseXor,
                    Right: fieldHash,
                    Location: _synthLoc) { ResolvedType = u64Type };
        }

        return new ReturnStatement(Value: accum!, Location: _synthLoc);
    }

    // ?�?� $represent / $diagnose (record + entity) ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�

    /// <summary>
    /// Builds the body for <c>$represent</c> or <c>$diagnose</c> on a record or entity.
    /// <list type="bullet">
    ///   <item><c>$represent</c>: <c>return f"TypeName({me.f1}, {me.f2})"</c> ??open+posted fields, positional.</item>
    ///   <item><c>$diagnose</c>:  <c>return f"Module.TypeName(f1: {me.f1}, [secret] f2: {me.f2})"</c> ??all fields named,
    ///         values via <c>$represent</c> (not <c>$diagnose</c>) to avoid cascading verbosity.</item>
    /// </list>
    /// Field access via <see cref="MemberExpression"/> works for both records (extractvalue) and
    /// entities (GEP + load).
    /// </summary>
    private static Statement BuildTextBody(
        TypeInfo ownerType,
        IReadOnlyList<MemberVariableInfo> fields,
        TypeInfo textType,
        bool diagnose)
    {
        var parts = new List<InsertedTextPart>();

        string typePart = diagnose ? ownerType.FullName : ownerType.Name;
        parts.Add(new TextPart(Text: typePart + "(", Location: _synthLoc));

        IEnumerable<MemberVariableInfo> visibleFields = diagnose
            ? (IEnumerable<MemberVariableInfo>)fields
            : fields.Where(predicate: f =>
                f.Visibility is VisibilityModifier.Open or VisibilityModifier.Posted);

        bool first = true;
        foreach (MemberVariableInfo field in visibleFields)
        {
            if (!first)
                parts.Add(new TextPart(Text: ", ", Location: _synthLoc));
            first = false;

            if (diagnose)
            {
                string secretPrefix = field.Visibility == VisibilityModifier.Secret
                    ? "[secret] "
                    : "";
                parts.Add(new TextPart(
                    Text: secretPrefix + field.Name + ": ",
                    Location: _synthLoc));
            }

            var fieldExpr = new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc)
                    { ResolvedType = ownerType },
                PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            // Always use $represent for field values, even inside $diagnose.
            // Using $diagnose recursively would produce exponentially verbose output.
            parts.Add(new ExpressionPart(
                Expression: fieldExpr,
                FormatSpec: null,
                Location: _synthLoc));
        }

        parts.Add(new TextPart(Text: ")", Location: _synthLoc));

        var fstring = new InsertedTextExpression(
            Parts: parts,
            IsRaw: false,
            Location: _synthLoc) { ResolvedType = textType };

        return new ReturnStatement(Value: fstring, Location: _synthLoc);
    }

    // ?�?� $represent / $diagnose (choice) ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�

    /// <summary>
    /// Builds the body: a WhenStatement over <c>me</c> returning the case name string.
    /// </summary>
    private static Statement BuildChoiceRepresentBody(ChoiceTypeInfo choice, TypeInfo textType,
        TypeInfo? logicBreachedErrorType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = choice };

        var clauses = new List<WhenClause>(capacity: choice.Cases.Count + 1);
        foreach (ChoiceCaseInfo c in choice.Cases)
        {
            clauses.Add(new WhenClause(
                Pattern: new LiteralPattern(
                    Value: (int)c.ComputedValue,
                    LiteralType: TokenType.S32Literal,
                    Location: _synthLoc),
                Body: new ReturnStatement(
                    Value: new LiteralExpression(
                        Value: c.Name, LiteralType: TokenType.TextLiteral, Location: _synthLoc)
                        { ResolvedType = textType },
                    Location: _synthLoc),
                Location: _synthLoc));
        }

        clauses.Add(new WhenClause(
            Pattern: new ElsePattern(VariableName: null, Location: _synthLoc),
            Body: BuildBreachStatement(logicBreachedErrorType: logicBreachedErrorType),
            Location: _synthLoc));

        return new WhenStatement(Expression: meRef, Clauses: clauses, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body: a WhenStatement over <c>me</c> returning
    /// <c>"Module.ChoiceName(id: N, CaseName)"</c> per case.
    /// </summary>
    private static Statement BuildChoiceDiagnoseBody(ChoiceTypeInfo choice, TypeInfo textType,
        TypeInfo? logicBreachedErrorType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = choice };

        string prefix = choice.FullName + "(id: ";
        var clauses = new List<WhenClause>(capacity: choice.Cases.Count + 1);
        foreach (ChoiceCaseInfo c in choice.Cases)
        {
            string text = $"{prefix}{c.ComputedValue}, {c.Name})";
            clauses.Add(new WhenClause(
                Pattern: new LiteralPattern(
                    Value: (int)c.ComputedValue,
                    LiteralType: TokenType.S32Literal,
                    Location: _synthLoc),
                Body: new ReturnStatement(
                    Value: new LiteralExpression(
                        Value: text, LiteralType: TokenType.TextLiteral, Location: _synthLoc)
                        { ResolvedType = textType },
                    Location: _synthLoc),
                Location: _synthLoc));
        }

        clauses.Add(new WhenClause(
            Pattern: new ElsePattern(VariableName: null, Location: _synthLoc),
            Body: BuildBreachStatement(logicBreachedErrorType: logicBreachedErrorType),
            Location: _synthLoc));

        return new WhenStatement(Expression: meRef, Clauses: clauses, Location: _synthLoc);
    }

    // ?�?� $represent / $diagnose (flags) ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�

    private void HandleFlags(RoutineInfo routine, FlagsTypeInfo flags,
        TypeInfo textType, TypeInfo boolType)
    {
        switch (routine.Name)
        {
            case "$eq":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildEqBodyNumeric(ownerType: flags, boolType: boolType);
                break;

            case "$represent":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildFlagsRepresentBody(flags: flags, textType: textType, boolType: boolType);
                break;

            case "$diagnose":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildFlagsDiagnoseBody(flags: flags, textType: textType, boolType: boolType);
                break;
        }
    }

    /// <summary>
    /// Builds the common block of statements that computes:
    /// <list type="bullet">
    ///   <item><c>var result: Text = ""</c> ??flag names joined by <c>" and "</c>.</item>
    ///   <item><c>var first: Bool = true</c> ??separator sentinel.</item>
    ///   <item>When <paramref name="computeBits"/> is <c>true</c>: <c>var bits: Text = "%"</c> ??
    ///         binary string in declaration order, e.g. <c>"%110"</c>.</item>
    ///   <item>For each flag in declaration order:
    ///     <c>if (me &amp; mask) != 0 { /* append name to result; first = false; [append "1" to bits] */ }
    ///     else { [append "0" to bits] }</c></item>
    ///   <item><c>if first { result = "&lt;none&gt;" }</c></item>
    /// </list>
    /// Returns the statement list (no trailing <c>return</c>).
    /// </summary>
    private static List<Statement> BuildFlagsComputeBlock(
        FlagsTypeInfo flags, TypeInfo textType, TypeInfo boolType, TypeInfo s64Type,
        bool computeBits)
    {
        var stmts = new List<Statement>();
        var emptyText = new LiteralExpression(
            Value: "", LiteralType: TokenType.TextLiteral, Location: _synthLoc)
            { ResolvedType = textType };
        var trueLit = new LiteralExpression(
            Value: true, LiteralType: TokenType.True, Location: _synthLoc)
            { ResolvedType = boolType };
        var falseLit = new LiteralExpression(
            Value: false, LiteralType: TokenType.False, Location: _synthLoc)
            { ResolvedType = boolType };
        var zeroLit = new LiteralExpression(
            Value: 0L, LiteralType: TokenType.S64Literal, Location: _synthLoc)
            { ResolvedType = s64Type };
        var noneLit = new LiteralExpression(
            Value: "<none>", LiteralType: TokenType.TextLiteral, Location: _synthLoc)
            { ResolvedType = textType };
        var oneLit = new LiteralExpression(
            Value: "1", LiteralType: TokenType.TextLiteral, Location: _synthLoc)
            { ResolvedType = textType };
        var zeroCharLit = new LiteralExpression(
            Value: "0", LiteralType: TokenType.TextLiteral, Location: _synthLoc)
            { ResolvedType = textType };

        // var result: Text = ""
        stmts.Add(new DeclarationStatement(
            Declaration: new VariableDeclaration(
                Name: "result",
                Type: null,
                Initializer: emptyText,
                Visibility: VisibilityModifier.Open,
                Location: _synthLoc),
            Location: _synthLoc));

        // var first: Bool = true
        stmts.Add(new DeclarationStatement(
            Declaration: new VariableDeclaration(
                Name: "first",
                Type: null,
                Initializer: trueLit,
                Visibility: VisibilityModifier.Open,
                Location: _synthLoc),
            Location: _synthLoc));

        if (computeBits)
        {
            // var bits: Text = "%"
            stmts.Add(new DeclarationStatement(
                Declaration: new VariableDeclaration(
                    Name: "bits",
                    Type: null,
                    Initializer: new LiteralExpression(
                        Value: "%", LiteralType: TokenType.TextLiteral, Location: _synthLoc)
                        { ResolvedType = textType },
                    Visibility: VisibilityModifier.Open,
                    Location: _synthLoc),
                Location: _synthLoc));
        }

        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = flags };

        foreach (FlagsMemberInfo member in flags.Members)
        {
            long mask = 1L << member.BitPosition;
            var maskLit = new LiteralExpression(
                Value: mask, LiteralType: TokenType.S64Literal, Location: _synthLoc)
                { ResolvedType = s64Type };

            // (me & mask) != 0
            var bwAnd = new BinaryExpression(
                Left: meRef,
                Operator: BinaryOperator.BitwiseAnd,
                Right: maskLit,
                Location: _synthLoc) { ResolvedType = s64Type };
            var isSet = new BinaryExpression(
                Left: bwAnd,
                Operator: BinaryOperator.NotEqual,
                Right: zeroLit,
                Location: _synthLoc) { ResolvedType = boolType };

            var nameLit = new LiteralExpression(
                Value: member.Name, LiteralType: TokenType.TextLiteral, Location: _synthLoc)
                { ResolvedType = textType };
            var andNameLit = new LiteralExpression(
                Value: " and " + member.Name,
                LiteralType: TokenType.TextLiteral,
                Location: _synthLoc) { ResolvedType = textType };

            // result.$add(other: " and FlagName")
            var appendNameCall = new CallExpression(
                Callee: new MemberExpression(
                    Object: new IdentifierExpression(Name: "result", Location: _synthLoc)
                        { ResolvedType = textType },
                    PropertyName: "$add",
                    Location: _synthLoc),
                Arguments:
                [
                    new NamedArgumentExpression(
                        Name: "other",
                        Value: andNameLit,
                        Location: _synthLoc)
                ],
                Location: _synthLoc) { ResolvedType = textType };

            // if first { result = "FlagName"; first = false } else { result = result.$add(...) }
            var innerNameIf = new IfStatement(
                Condition: new IdentifierExpression(Name: "first", Location: _synthLoc)
                    { ResolvedType = boolType },
                ThenStatement: new BlockStatement(
                    Statements:
                    [
                        new AssignmentStatement(
                            Target: new IdentifierExpression(Name: "result", Location: _synthLoc)
                                { ResolvedType = textType },
                            Value: nameLit,
                            Location: _synthLoc),
                        new AssignmentStatement(
                            Target: new IdentifierExpression(Name: "first", Location: _synthLoc)
                                { ResolvedType = boolType },
                            Value: falseLit,
                            Location: _synthLoc)
                    ],
                    Location: _synthLoc),
                ElseStatement: new BlockStatement(
                    Statements:
                    [
                        new AssignmentStatement(
                            Target: new IdentifierExpression(Name: "result", Location: _synthLoc)
                                { ResolvedType = textType },
                            Value: appendNameCall,
                            Location: _synthLoc)
                    ],
                    Location: _synthLoc),
                Location: _synthLoc);

            if (!computeBits)
            {
                // if (me & mask) != 0 { <name logic> }
                stmts.Add(new IfStatement(
                    Condition: isSet,
                    ThenStatement: new BlockStatement(
                        Statements: [innerNameIf],
                        Location: _synthLoc),
                    ElseStatement: null,
                    Location: _synthLoc));
            }
            else
            {
                // bits.$add(other: "1") ??set branch
                var append1 = new CallExpression(
                    Callee: new MemberExpression(
                        Object: new IdentifierExpression(Name: "bits", Location: _synthLoc)
                            { ResolvedType = textType },
                        PropertyName: "$add",
                        Location: _synthLoc),
                    Arguments:
                    [
                        new NamedArgumentExpression(
                            Name: "other", Value: oneLit, Location: _synthLoc)
                    ],
                    Location: _synthLoc) { ResolvedType = textType };

                // bits.$add(other: "0") ??clear branch
                var append0 = new CallExpression(
                    Callee: new MemberExpression(
                        Object: new IdentifierExpression(Name: "bits", Location: _synthLoc)
                            { ResolvedType = textType },
                        PropertyName: "$add",
                        Location: _synthLoc),
                    Arguments:
                    [
                        new NamedArgumentExpression(
                            Name: "other", Value: zeroCharLit, Location: _synthLoc)
                    ],
                    Location: _synthLoc) { ResolvedType = textType };

                // if (me & mask) != 0 { <name logic>; bits = bits.$add("1") }
                // else               { bits = bits.$add("0") }
                stmts.Add(new IfStatement(
                    Condition: isSet,
                    ThenStatement: new BlockStatement(
                        Statements:
                        [
                            innerNameIf,
                            new AssignmentStatement(
                                Target: new IdentifierExpression(Name: "bits", Location: _synthLoc)
                                    { ResolvedType = textType },
                                Value: append1,
                                Location: _synthLoc)
                        ],
                        Location: _synthLoc),
                    ElseStatement: new BlockStatement(
                        Statements:
                        [
                            new AssignmentStatement(
                                Target: new IdentifierExpression(Name: "bits", Location: _synthLoc)
                                    { ResolvedType = textType },
                                Value: append0,
                                Location: _synthLoc)
                        ],
                        Location: _synthLoc),
                    Location: _synthLoc));
            }
        }

        // if first { result = "<none>" }
        stmts.Add(new IfStatement(
            Condition: new IdentifierExpression(Name: "first", Location: _synthLoc)
                { ResolvedType = boolType },
            ThenStatement: new BlockStatement(
                Statements:
                [
                    new AssignmentStatement(
                        Target: new IdentifierExpression(Name: "result", Location: _synthLoc)
                            { ResolvedType = textType },
                        Value: noneLit,
                        Location: _synthLoc)
                ],
                Location: _synthLoc),
            ElseStatement: null,
            Location: _synthLoc));

        return stmts;
    }

    /// <summary>
    /// Builds the <c>$represent</c> body for a flags type.
    /// Returns <c>"Flag1 and Flag2"</c>, or <c>"&lt;none&gt;"</c> if no bits are set.
    /// </summary>
    private Statement BuildFlagsRepresentBody(
        FlagsTypeInfo flags, TypeInfo textType, TypeInfo boolType)
    {
        TypeInfo? s64Type = ctx.Registry.LookupType(name: "S64");
        if (s64Type == null) return new ReturnStatement(
            Value: new LiteralExpression(
                Value: "<none>", LiteralType: TokenType.TextLiteral, Location: _synthLoc)
                { ResolvedType = textType },
            Location: _synthLoc);

        List<Statement> stmts = BuildFlagsComputeBlock(
            flags: flags, textType: textType, boolType: boolType, s64Type: s64Type,
            computeBits: false);

        stmts.Add(new ReturnStatement(
            Value: new IdentifierExpression(Name: "result", Location: _synthLoc)
                { ResolvedType = textType },
            Location: _synthLoc));

        return new BlockStatement(Statements: stmts, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the <c>$diagnose</c> body for a flags type.
    /// Returns <c>"Module.FlagsName(value: %110, Flag1 and Flag2)"</c> where the binary string
    /// is in declaration order (<c>%</c> prefix, leftmost = first declared flag).
    /// </summary>
    private Statement BuildFlagsDiagnoseBody(
        FlagsTypeInfo flags, TypeInfo textType, TypeInfo boolType)
    {
        TypeInfo? s64Type = ctx.Registry.LookupType(name: "S64");
        if (s64Type == null) return new ReturnStatement(
            Value: new LiteralExpression(
                Value: flags.FullName + "(value: %0, <none>)",
                LiteralType: TokenType.TextLiteral,
                Location: _synthLoc) { ResolvedType = textType },
            Location: _synthLoc);

        List<Statement> stmts = BuildFlagsComputeBlock(
            flags: flags, textType: textType, boolType: boolType, s64Type: s64Type,
            computeBits: true);

        // return f"Module.FlagsName(value: {bits}, {result})"
        // Both result and bits are Text ??EmitRepresentCall returns them directly.
        var resultRef = new IdentifierExpression(Name: "result", Location: _synthLoc)
            { ResolvedType = textType };
        var bitsRef = new IdentifierExpression(Name: "bits", Location: _synthLoc)
            { ResolvedType = textType };
        var fstring = new InsertedTextExpression(
            Parts:
            [
                new TextPart(Text: flags.FullName + "(value: ", Location: _synthLoc),
                new ExpressionPart(Expression: bitsRef, FormatSpec: null, Location: _synthLoc),
                new TextPart(Text: ", ", Location: _synthLoc),
                new ExpressionPart(Expression: resultRef, FormatSpec: null, Location: _synthLoc),
                new TextPart(Text: ")", Location: _synthLoc)
            ],
            IsRaw: false,
            Location: _synthLoc) { ResolvedType = textType };

        stmts.Add(new ReturnStatement(Value: fstring, Location: _synthLoc));

        return new BlockStatement(Statements: stmts, Location: _synthLoc);
    }

    // ?�?� Unreachable helper ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�

    /// <summary>
    /// Builds <c>throw LogicBreachedError()</c> for provably-unreachable else arms
    /// (e.g. the default clause in a synthesized choice <c>$represent</c> body).
    /// Falls back to <c>throw LogicBreachedError()</c> with a null ResolvedType when the
    /// type isn't in the registry yet (shouldn't happen in practice).
    /// </summary>
    private static Statement BuildBreachStatement(TypeInfo? logicBreachedErrorType)
    {
        // Use CallExpression, not CreatorExpression ??crashable constructors in RF source
        // parse as calls (e.g. LogicBreachedError()), and EmitFunctionCall has the
        // crashable-construction path; EmitConstructorCall does not.
        var call = new CallExpression(
            Callee: new IdentifierExpression(Name: "LogicBreachedError", Location: _synthLoc)
                { ResolvedType = logicBreachedErrorType },
            Arguments: [],
            Location: _synthLoc) { ResolvedType = logicBreachedErrorType };
        return new ThrowStatement(Error: call, Location: _synthLoc);
    }

    // ?�?� $represent / $diagnose (crashable) ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�

    /// <summary>
    /// Builds the body: <c>return me.crash_message()</c>.
    /// </summary>
    private static Statement BuildCrashableRepresentBody(CrashableTypeInfo crashable)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = crashable };
        var call = new CallExpression(
            Callee: new MemberExpression(
                Object: meRef,
                PropertyName: "crash_message",
                Location: _synthLoc),
            Arguments: [],
            Location: _synthLoc);
        return new ReturnStatement(Value: call, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body:
    /// <c>return f"Module.CrashableName({me.crash_message()}[, field1: {me.f1}, ...])"</c>.
    /// </summary>
    private static Statement BuildCrashableDiagnoseBody(
        CrashableTypeInfo crashable, TypeInfo textType)
    {
        var parts = new List<InsertedTextPart>();

        // Open with "Module.TypeName("
        parts.Add(new TextPart(Text: crashable.FullName + "(", Location: _synthLoc));

        // First element: crash_message() ??use $represent format (no "?")
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = crashable };
        var crashMsgCall = new CallExpression(
            Callee: new MemberExpression(
                Object: meRef,
                PropertyName: "crash_message",
                Location: _synthLoc),
            Arguments: [],
            Location: _synthLoc);
        parts.Add(new ExpressionPart(
            Expression: crashMsgCall,
            FormatSpec: null,
            Location: _synthLoc));

        // Remaining member-variable fields
        foreach (MemberVariableInfo field in crashable.MemberVariables)
        {
            parts.Add(new TextPart(Text: ", " + field.Name + ": ", Location: _synthLoc));

            var meRef2 = new IdentifierExpression(Name: "me", Location: _synthLoc)
                { ResolvedType = crashable };
            var fieldExpr = new MemberExpression(
                Object: meRef2,
                PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            parts.Add(new ExpressionPart(
                Expression: fieldExpr,
                FormatSpec: null,
                Location: _synthLoc));
        }

        parts.Add(new TextPart(Text: ")", Location: _synthLoc));

        var fstring = new InsertedTextExpression(
            Parts: parts,
            IsRaw: false,
            Location: _synthLoc) { ResolvedType = textType };

        return new ReturnStatement(Value: fstring, Location: _synthLoc);
    }

    // ?�?� BuilderService constant routines ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�

    /// <summary>
    /// Synthesizes AST bodies for BuilderService routines that return a single compile-time
    /// constant value (Text, U64, S64, Bool). Called before the owner-type switch so it handles
    /// all types uniformly.
    /// Returns <c>true</c> if the routine was handled, <c>false</c> otherwise.
    /// </summary>
    private bool TryHandleBuilderServiceConstant(RoutineInfo routine,
        TypeInfo textType, TypeInfo? u64Type, TypeInfo? s64Type, TypeInfo? boolType,
        TypeInfo? typeKindType, TypeInfo? listTextType, TypeInfo? byteSizeType = null)
    {
        if (routine.OwnerType == null) return false;
        TypeInfo owner = routine.OwnerType;

        // Skip compiler-internal/non-synthesizable categories.
        if (owner.Category is TypeCategory.TypeParameter or TypeCategory.Error
            or TypeCategory.Intrinsic or TypeCategory.ProtocolSelf
            or TypeCategory.ConstGenericValue)
            return false;

        switch (routine.Name)
        {
            case "type_name":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: owner.Name, returnType: textType);
                return true;

            case "module_name":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: owner.Module ?? "", returnType: textType);
                return true;

            case "full_type_name":
            {
                string fullName = string.IsNullOrEmpty(value: owner.Module)
                    ? owner.Name
                    : $"{owner.Module}.{owner.Name}";
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: fullName, returnType: textType);
                return true;
            }

            case "type_id" when u64Type != null:
            {
                ulong hash = ComputeVariantMemberTypeId(fullName: owner.FullName);
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: hash, returnType: u64Type);
                return true;
            }

            case "data_size" when byteSizeType != null && u64Type != null:
            {
                ulong size = CalculateDataSizeForType(type: owner);
                ctx.VariantBodies[key: routine.RegistryKey] = new ReturnStatement(
                    Value: BuilderServiceInliningPass.MakeByteSizeCreatorPublic(
                        value: size, u64Type: u64Type, byteSizeType: byteSizeType, loc: _synthLoc),
                    Location: _synthLoc);
                return true;
            }

            case "data_size" when u64Type != null:
            {
                // Fallback when ByteSize is not yet loaded (e.g., compiling ByteSize.rf itself).
                ulong size = CalculateDataSizeForType(type: owner);
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: size, returnType: u64Type);
                return true;
            }

            case "member_variable_count" when s64Type != null:
            {
                long count = owner switch
                {
                    RecordTypeInfo r => r.MemberVariables.Count,
                    EntityTypeInfo e => e.MemberVariables.Count,
                    CrashableTypeInfo c => c.MemberVariables.Count,
                    TupleTypeInfo t => t.MemberVariables.Count,
                    ChoiceTypeInfo ch => ch.Cases.Count,
                    FlagsTypeInfo f => f.Members.Count,
                    VariantTypeInfo v => v.Members.Count,
                    _ => 0
                };
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: count, returnType: s64Type);
                return true;
            }

            case "is_generic" when boolType != null:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: owner.IsGenericDefinition, returnType: boolType);
                return true;

            case "type_kind" when typeKindType is ChoiceTypeInfo typeKindChoice:
            {
                // Map the owner's category to the TypeKind case name, then look up its
                // ComputedValue from the registry ??avoids hardcoding ordinals that could
                // drift out of sync with the BuilderService.rf TypeKind declaration.
                string caseName = owner.Category switch
                {
                    TypeCategory.Record => "RECORD",
                    TypeCategory.Entity => "ENTITY",
                    TypeCategory.Crashable => "CRASHABLE",
                    TypeCategory.Choice => "CHOICE",
                    TypeCategory.Variant => "VARIANT",
                    TypeCategory.Flags => "FLAGS",
                    TypeCategory.Routine => "ROUTINE",
                    TypeCategory.Protocol => "PROTOCOL",
                    TypeCategory.Tuple => "RECORD",
                    _ => throw new InvalidOperationException(
                        $"Unhandled TypeCategory '{owner.Category}' in type_kind BuilderService mapping.")
                };
                ChoiceCaseInfo? found =
                    typeKindChoice.Cases.FirstOrDefault(c => c.Name == caseName);
                if (found == null) return false;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: found.ComputedValue, returnType: typeKindChoice);
                return true;
            }

            case "protocols" when listTextType != null:
            {
                List<string> names = owner switch
                {
                    RecordTypeInfo r => r.ImplementedProtocols.Select(p => p.Name).ToList(),
                    EntityTypeInfo e => e.ImplementedProtocols.Select(p => p.Name).ToList(),
                    CrashableTypeInfo c => c.ImplementedProtocols.Select(p => p.Name).ToList(),
                    _ => []
                };
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeListReturn(values: names, textType: textType, listTextType: listTextType);
                return true;
            }

            case "routine_names" when listTextType != null:
            {
                List<string> names = ctx.Registry.GetMethodsForType(type: owner)
                    .Select(r => r.Name).Distinct().ToList();
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeListReturn(values: names, textType: textType, listTextType: listTextType);
                return true;
            }

            case "generic_args" when listTextType != null:
            {
                List<string> args = owner.TypeArguments?.Select(t => t.Name).ToList()
                    ?? owner.GenericParameters?.ToList() ?? [];
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeListReturn(values: args, textType: textType, listTextType: listTextType);
                return true;
            }

            case "annotations" when listTextType != null:
                // Type-level annotations are not yet tracked on TypeInfo ??return empty list
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeListReturn(values: [], textType: textType, listTextType: listTextType);
                return true;

            case "dependencies" when listTextType != null:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeListReturn(values: [], textType: textType, listTextType: listTextType);
                return true;

            case "protocol_info" when listTextType != null:
                // Full ProtocolInfo entity allocation deferred ??return empty list
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeListReturn(values: [], textType: textType, listTextType: listTextType);
                return true;

            case "routine_info" when listTextType != null:
                // TODO: not yet implemented — full RoutineInfo entity allocation deferred; returns empty list
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeListReturn(values: [], textType: textType, listTextType: listTextType);
                return true;

            case "var_name":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: "<unknown>", returnType: textType);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Handles standalone (non-owner) BuilderService constants derived from
    /// <see cref="DesugaringContext.Target"/> / <see cref="DesugaringContext.BuildMode"/>.
    /// </summary>
    private bool TryHandleStandaloneBuilderServiceConstant(RoutineInfo routine,
        TypeInfo textType, TypeInfo? u64Type, TypeInfo? s64Type, TypeInfo? byteSizeType)
    {
        switch (routine.Name)
        {
            case "page_size":
                return EmitByteSizeOrU64(routine: routine,
                    value: (ulong)ctx.Target.PageSize,
                    u64Type: u64Type, byteSizeType: byteSizeType);

            case "cache_line":
                return EmitByteSizeOrU64(routine: routine,
                    value: (ulong)ctx.Target.CacheLineSize,
                    u64Type: u64Type, byteSizeType: byteSizeType);

            case "word_size":
                return EmitByteSizeOrU64(routine: routine,
                    value: (ulong)(ctx.Target.PointerBitWidth / 8),
                    u64Type: u64Type, byteSizeType: byteSizeType);

            case "target_os":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: ctx.Target.TargetOS, returnType: textType);
                return true;

            case "target_arch":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: ctx.Target.TargetArch, returnType: textType);
                return true;

            case "builder_version":
            {
                string version =
                    typeof(WiredRoutinePass).Assembly.GetName().Version?.ToString(fieldCount: 3)
                    ?? "0.0.0";
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: version, returnType: textType);
                return true;
            }

            case "build_timestamp":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: DateTime.UtcNow.ToString(format: "o"),
                        returnType: textType);
                return true;

            case "build_mode":
            {
                if (routine.ReturnType is ChoiceTypeInfo buildModeChoice)
                {
                    string caseName = ctx.BuildMode switch
                    {
                        RfBuildMode.Debug => "DEBUG",
                        RfBuildMode.Release => "RELEASE",
                        RfBuildMode.ReleaseTime => "RELEASE_TIME",
                        RfBuildMode.ReleaseSpace => "RELEASE_SPACE",
                        _ => throw new InvalidOperationException(
                            $"Unhandled RfBuildMode value '{ctx.BuildMode}'.")
                    };
                    ChoiceCaseInfo? found =
                        buildModeChoice.Cases.FirstOrDefault(c => c.Name == caseName);
                    if (found == null) return false;
                    ctx.VariantBodies[key: routine.RegistryKey] =
                        MakeLiteralReturn(value: found.ComputedValue,
                            returnType: buildModeChoice);
                    return true;
                }
                if (s64Type != null)
                {
                    ctx.VariantBodies[key: routine.RegistryKey] =
                        MakeLiteralReturn(value: (long)ctx.BuildMode, returnType: s64Type);
                    return true;
                }
                return false;
            }

            default:
                return false;
        }
    }

    private bool EmitByteSizeOrU64(RoutineInfo routine, ulong value,
        TypeInfo? u64Type, TypeInfo? byteSizeType)
    {
        if (byteSizeType != null && u64Type != null)
        {
            ctx.VariantBodies[key: routine.RegistryKey] = new ReturnStatement(
                Value: BuilderServiceInliningPass.MakeByteSizeCreatorPublic(
                    value: value, u64Type: u64Type, byteSizeType: byteSizeType, loc: _synthLoc),
                Location: _synthLoc);
            return true;
        }
        if (u64Type != null)
        {
            ctx.VariantBodies[key: routine.RegistryKey] =
                MakeLiteralReturn(value: value, returnType: u64Type);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Builds a <c>return [elem0, elem1, ...]</c> statement using a
    /// <see cref="ListLiteralExpression"/> with the given Text string values.
    /// </summary>
    private static Statement MakeListReturn(IReadOnlyList<string> values,
        TypeInfo textType, TypeInfo listTextType)
    {
        var elements = values
            .Select(v => (Expression)new LiteralExpression(
                Value: v,
                LiteralType: TokenType.TextLiteral,
                Location: _synthLoc) { ResolvedType = textType })
            .ToList();
        return new ReturnStatement(
            Value: new ListLiteralExpression(
                Elements: elements,
                ElementType: null,
                Location: _synthLoc) { ResolvedType = listTextType },
            Location: _synthLoc);
    }

    private static Statement MakeLiteralReturn(string value, TypeInfo returnType) =>
        new ReturnStatement(
            Value: new LiteralExpression(
                Value: value,
                LiteralType: TokenType.TextLiteral,
                Location: _synthLoc) { ResolvedType = returnType },
            Location: _synthLoc);

    private static Statement MakeLiteralReturn(ulong value, TypeInfo returnType) =>
        new ReturnStatement(
            Value: new LiteralExpression(
                Value: value,
                LiteralType: TokenType.U64Literal,
                Location: _synthLoc) { ResolvedType = returnType },
            Location: _synthLoc);

    private static Statement MakeLiteralReturn(long value, TypeInfo returnType) =>
        new ReturnStatement(
            Value: new LiteralExpression(
                Value: value,
                LiteralType: TokenType.S64Literal,
                Location: _synthLoc) { ResolvedType = returnType },
            Location: _synthLoc);

    private static Statement MakeLiteralReturn(bool value, TypeInfo returnType) =>
        new ReturnStatement(
            Value: new LiteralExpression(
                Value: value,
                LiteralType: value ? TokenType.True : TokenType.False,
                Location: _synthLoc) { ResolvedType = returnType },
            Location: _synthLoc);

    /// <summary>
    /// Approximates the in-memory byte size of a type for <c>data_size()</c>.
    /// Primitive types use their natural width; structs sum field sizes (8-byte aligned).
    /// Entity and crashable types return 8 (pointer size on 64-bit ??stored by reference).
    /// Backend-annotated records use the LLVM type width parsed from the @llvm("...") string.
    /// </summary>
    private static ulong CalculateDataSizeForType(TypeInfo type) =>
        type switch
        {
            RecordTypeInfo r when r.HasDirectBackendType => LlvmBackendTypeSize(r.BackendType!),
            RecordTypeInfo r => (ulong)(r.MemberVariables.Count * 8),
            ChoiceTypeInfo => 4,   // i32
            FlagsTypeInfo => 8,    // i64
            TupleTypeInfo t => (ulong)(t.ElementTypes.Count * 8),
            EntityTypeInfo => 8,   // heap-allocated; stored as pointer (8 bytes on 64-bit)
            CrashableTypeInfo => 8, // same ??stored as pointer
            VariantTypeInfo v => (ulong)((v.Members.Count + 1) * 8), // tag + largest payload
            _ => 0
        };

    /// <summary>
    /// Returns the byte size of a scalar LLVM type name as used in @llvm("...") annotations.
    /// Array types like "[4 x i32]" are parsed recursively.
    /// Template strings containing '{' (unresolved generics) return 0.
    /// </summary>
    private static ulong LlvmBackendTypeSize(string llvmType) => llvmType.Trim() switch
    {
        "void" => 0,                // Blank ??zero-sized
        "i1" or "i8" => 1,
        "i16" or "half" => 2,
        "i32" or "float" => 4,
        "i64" or "double" or "ptr" => 8,
        "i128" or "fp128" => 16,
        var s when s.StartsWith('[') => ParseLlvmArraySize(s),
        var s when s.Contains('{') => 0,  // unresolved generic template
        _ => throw new InvalidOperationException(
            $"Unknown LLVM type '{llvmType}' in LlvmBackendTypeSize — cannot determine byte size.")
    };

    /// <summary>
    /// Parses an LLVM array type like "[4 x i32]" ??4 * 4 = 16.
    /// Returns 0 if the format is unrecognised.
    /// </summary>
    private static ulong ParseLlvmArraySize(string arrayType)
    {
        // Expected format: "[ N x elemType ]"
        int xIdx = arrayType.IndexOf(" x ", StringComparison.Ordinal);
        if (xIdx < 0) return 0;

        string countPart = arrayType[1..xIdx].Trim();
        string elemPart = arrayType[(xIdx + 3)..].TrimEnd(']').Trim();

        if (!ulong.TryParse(countPart, out ulong count)) return 0;
        return count * LlvmBackendTypeSize(elemPart);
    }

    // ?�?� $represent / $diagnose (tuple) ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�

    private void HandleTuple(RoutineInfo routine, TupleTypeInfo tuple, TypeInfo textType)
    {
        switch (routine.Name)
        {
            case "$represent":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildTupleTextBody(tuple: tuple, textType: textType, diagnose: false);
                break;

            case "$diagnose":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildTupleTextBody(tuple: tuple, textType: textType, diagnose: true);
                break;
        }
    }

    /// <summary>
    /// Builds the body for <c>$represent</c> or <c>$diagnose</c> on a tuple.
    /// <list type="bullet">
    ///   <item><c>$represent</c>: <c>return f"({me.item0}, {me.item1})"</c></item>
    ///   <item><c>$diagnose</c>: <c>return f"ValueTuple[T1, T2]({me.item0}, {me.item1})"</c></item>
    /// </list>
    /// </summary>
    private static Statement BuildTupleTextBody(TupleTypeInfo tuple, TypeInfo textType,
        bool diagnose)
    {
        var parts = new List<InsertedTextPart>();

        if (diagnose)
        {
            string prefix = tuple.IsValueTuple ? "ValueTuple" : "Tuple";
            string typeArgs = string.Join(
                separator: ", ",
                values: tuple.ElementTypes.Select(selector: t => t.Name));
            parts.Add(new TextPart(
                Text: $"{prefix}[{typeArgs}](",
                Location: _synthLoc));
        }
        else
        {
            parts.Add(new TextPart(Text: "(", Location: _synthLoc));
        }

        bool first = true;
        foreach (MemberVariableInfo field in tuple.MemberVariables)
        {
            if (!first)
                parts.Add(new TextPart(Text: ", ", Location: _synthLoc));
            first = false;

            var fieldExpr = new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc)
                    { ResolvedType = tuple },
                PropertyName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            parts.Add(new ExpressionPart(
                Expression: fieldExpr,
                FormatSpec: null,
                Location: _synthLoc));
        }

        parts.Add(new TextPart(Text: ")", Location: _synthLoc));

        var fstring = new InsertedTextExpression(
            Parts: parts,
            IsRaw: false,
            Location: _synthLoc) { ResolvedType = textType };

        return new ReturnStatement(Value: fstring, Location: _synthLoc);
    }

    // ?�?� $represent / $diagnose (variant) ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�

    private void HandleVariant(RoutineInfo routine, VariantTypeInfo variant, TypeInfo textType)
    {
        // Skip generic definitions ??no concrete member types to dispatch on.
        if (variant.IsGenericDefinition) return;

        switch (routine.Name)
        {
            case "$represent":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildVariantRepresentBody(variant: variant, textType: textType);
                break;

            case "$diagnose":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildVariantDiagnoseBody(variant: variant, textType: textType);
                break;
        }
    }

    /// <summary>
    /// Builds: <c>when me { is Blank => return "Blank", is T as v => return v.$represent(), ... }</c>.
    /// </summary>
    private static Statement BuildVariantRepresentBody(VariantTypeInfo variant, TypeInfo textType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = variant };

        var clauses = new List<WhenClause>(capacity: variant.Members.Count + 1);
        foreach (VariantMemberInfo member in variant.Members)
        {
            string memberName = member.IsNone ? "None" : member.Type!.Name;
            bool isZeroSized = member.IsNone || member.Type?.Name == "Blank";

            var typeExpr = new TypeExpression(
                Name: memberName,
                GenericArguments: null,
                Location: _synthLoc) { ResolvedType = member.Type };

            Pattern pattern;
            Statement clauseBody;

            if (isZeroSized)
            {
                pattern = new TypePattern(
                    Type: typeExpr, VariableName: null, Bindings: null, Location: _synthLoc);
                clauseBody = new ReturnStatement(
                    Value: new LiteralExpression(
                        Value: memberName,
                        LiteralType: TokenType.TextLiteral,
                        Location: _synthLoc) { ResolvedType = textType },
                    Location: _synthLoc);
            }
            else
            {
                pattern = new TypePattern(
                    Type: typeExpr, VariableName: "v", Bindings: null, Location: _synthLoc);

                var vRef = new IdentifierExpression(Name: "v", Location: _synthLoc)
                    { ResolvedType = member.Type };
                var representCall = new CallExpression(
                    Callee: new MemberExpression(
                        Object: vRef,
                        PropertyName: "$represent",
                        Location: _synthLoc) { ResolvedType = textType },
                    Arguments: [],
                    Location: _synthLoc) { ResolvedType = textType };
                clauseBody = new ReturnStatement(Value: representCall, Location: _synthLoc);
            }

            clauses.Add(new WhenClause(
                Pattern: pattern,
                Body: clauseBody,
                Location: _synthLoc));
        }

        clauses.Add(new WhenClause(
            Pattern: new ElsePattern(VariableName: null, Location: _synthLoc),
            Body: new ReturnStatement(
                Value: new LiteralExpression(
                    Value: "<variant>",
                    LiteralType: TokenType.TextLiteral,
                    Location: _synthLoc) { ResolvedType = textType },
                Location: _synthLoc),
            Location: _synthLoc));

        return new WhenStatement(Expression: meRef, Clauses: clauses, Location: _synthLoc);
    }

    /// <summary>
    /// Builds:
    /// <c>when me { is Blank => return "Mod.V(typeid=0, Blank)", is T as v => return f"Mod.V(typeid=N, {v.$represent()})", ... }</c>.
    /// </summary>
    private static Statement BuildVariantDiagnoseBody(VariantTypeInfo variant, TypeInfo textType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = variant };

        var clauses = new List<WhenClause>(capacity: variant.Members.Count + 1);
        foreach (VariantMemberInfo member in variant.Members)
        {
            string memberName = member.IsNone ? "None" : member.Type!.Name;
            bool isZeroSized = member.IsNone || member.Type?.Name == "Blank";
            ulong typeId = isZeroSized ? 0UL : ComputeVariantMemberTypeId(fullName: member.Type!.FullName);

            var typeExpr = new TypeExpression(
                Name: memberName,
                GenericArguments: null,
                Location: _synthLoc) { ResolvedType = member.Type };

            Pattern pattern;
            Statement clauseBody;

            if (isZeroSized)
            {
                pattern = new TypePattern(
                    Type: typeExpr, VariableName: null, Bindings: null, Location: _synthLoc);
                string literal = $"{variant.FullName}(typeid=0, {memberName})";
                clauseBody = new ReturnStatement(
                    Value: new LiteralExpression(
                        Value: literal,
                        LiteralType: TokenType.TextLiteral,
                        Location: _synthLoc) { ResolvedType = textType },
                    Location: _synthLoc);
            }
            else
            {
                pattern = new TypePattern(
                    Type: typeExpr, VariableName: "v", Bindings: null, Location: _synthLoc);

                var vRef = new IdentifierExpression(Name: "v", Location: _synthLoc)
                    { ResolvedType = member.Type };
                var representCall = new CallExpression(
                    Callee: new MemberExpression(
                        Object: vRef,
                        PropertyName: "$represent",
                        Location: _synthLoc) { ResolvedType = textType },
                    Arguments: [],
                    Location: _synthLoc) { ResolvedType = textType };

                string prefix = $"{variant.FullName}(typeid={typeId}, ";
                var parts = new List<InsertedTextPart>
                {
                    new TextPart(Text: prefix, Location: _synthLoc),
                    new ExpressionPart(
                        Expression: representCall,
                        FormatSpec: null,
                        Location: _synthLoc),
                    new TextPart(Text: ")", Location: _synthLoc)
                };
                var fstring = new InsertedTextExpression(
                    Parts: parts,
                    IsRaw: false,
                    Location: _synthLoc) { ResolvedType = textType };
                clauseBody = new ReturnStatement(Value: fstring, Location: _synthLoc);
            }

            clauses.Add(new WhenClause(
                Pattern: pattern,
                Body: clauseBody,
                Location: _synthLoc));
        }

        clauses.Add(new WhenClause(
            Pattern: new ElsePattern(VariableName: null, Location: _synthLoc),
            Body: new ReturnStatement(
                Value: new LiteralExpression(
                    Value: $"{variant.FullName}(typeid=<error>)",
                    LiteralType: TokenType.TextLiteral,
                    Location: _synthLoc) { ResolvedType = textType },
                Location: _synthLoc),
            Location: _synthLoc));

        return new WhenStatement(Expression: meRef, Clauses: clauses, Location: _synthLoc);
    }

    /// <summary>
    /// FNV-1a hash for variant member type identification.
    /// Mirrors <c>LLVMCodeGenerator.ComputeTypeId</c> ??keep in sync.
    /// </summary>
    private static ulong ComputeVariantMemberTypeId(string fullName)
    {
        if (fullName is "Blank" || fullName.EndsWith(value: ".Blank",
                comparisonType: StringComparison.Ordinal))
            return 0UL;

        ulong hash = 14695981039346656037UL; // FNV-1a offset basis
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(s: fullName))
        {
            hash ^= b;
            hash *= 1099511628211UL; // FNV-1a prime
        }

        return hash;
    }
}
