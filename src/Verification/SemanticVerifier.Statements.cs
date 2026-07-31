using System.Collections.Generic;
using System.Linq;
using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification.Enums;

namespace Verification;

using TypeSymbol = TypeInfo;

/// <summary>
/// Phase 3: Statement analysis.
/// </summary>
public sealed partial class SemanticVerifier
{
    #region Phase 3: Body Analysis

    /// <summary>
    /// Analyzes routine bodies and expressions for type correctness.
    /// </summary>
    /// <param name="program">The program to analyze.</param>
    private void AnalyzeBodies(Program program)
    {
        foreach (ISyntaxTreeNode declaration in program.Declarations)
        {
            AnalyzeDeclaration(node: declaration);
        }
    }

    private void AnalyzeDeclaration(ISyntaxTreeNode node)
    {
        switch (node)
        {
            case RoutineDeclaration func:
                // Skip external/LLVM-only routines — their PassStatement bodies have nothing to analyze
                if (func.Body is not PassStatement)
                    AnalyzeFunctionBody(routine: func);
                break;

            case RecordDeclaration record:
            {
                TypeSymbol? recordType = _registry.LookupType(name: record.Name);
                AnalyzeTypeMembers(members: record.Members, ownerType: recordType);
                break;
            }

            case EntityDeclaration entity:
            {
                TypeSymbol? entityType = _registry.LookupType(name: entity.Name);
                AnalyzeTypeMembers(members: entity.Members, ownerType: entityType);
                break;
            }

            case VariableDeclaration varDecl:
                AnalyzeVariableDeclaration(varDecl: varDecl);
                break;
        }
    }

    private void AnalyzeTypeMembers(List<Declaration> members, TypeSymbol? ownerType)
    {
        TypeSymbol? prevType = _currentType;
        _currentType = ownerType;
        foreach (Declaration member in members)
        {
            if (member is RoutineDeclaration { Body: not PassStatement } method)
            {
                AnalyzeFunctionBody(routine: method);
            }
        }
        _currentType = prevType;
    }

    private void AnalyzeFunctionBody(RoutineDeclaration routine)
    {
        // A user-defined `$destroy` replaces the compiler-generated memory teardown (field
        // recursion + invalidate `me`), so the author owns freeing `me` and its fields. Require
        // `dangerous` so this opt-in to manual memory management is explicit at the declaration.
        bool isDestroyDecl = routine.Name == "destroy"
            || routine.Name.EndsWith(value: ".destroy", comparisonType: StringComparison.Ordinal);
        if (isDestroyDecl && !routine.IsDangerous)
        {
            ReportError(code: SemanticDiagnosticCode.DestroyMustBeDangerous,
                message: "A user-defined `$destroy` must be marked `dangerous` — overriding it " +
                         "makes you responsible for freeing `me` and its owned fields.",
                location: routine.Location);
        }

        // Construct the base name matching how the routine was registered.
        string baseName;
        TypeSymbol? routineOwnerType = null;
        if (_currentType != null)
        {
            // Member routine inside type body: OwnerType.Name + "." + routine.Name
            baseName = $"{RoutineInfo.GetTypeIdentity(type: _currentType)}.{routine.Name}";
            routineOwnerType = _currentType;
        }
        else if (routine.Name.Contains(value: '.'))
        {
            // Extension method syntax (e.g., "List[T].add_last"):
            // Resolve OwnerType to get canonical name, then append method name
            int dotIndex = routine.Name.IndexOf(value: '.');
            string typeName = routine.Name[..dotIndex];
            string methodName = routine.Name[(dotIndex + 1)..];

            // Always strip generic params first (e.g., "Stack[T]" -> "Stack") to look up
            // the generic definition, not a resolution cache entry.
            string lookupName = typeName.Contains(value: '[')
                ? typeName[..typeName.IndexOf(value: '[')]
                : typeName;
            TypeSymbol? ownerType = LookupTypeWithImports(name: lookupName);
            // Protocol-extension decls like `Iterable[Text].join` should have `me` typed as the
            // bracketed owner so the body's `for part in me` resolves `part` from
            // Iterable[Text]'s try_emit() return. Without this, `me` is the bare gen-def
            // `Iterable` and body identifiers (parameters, loop vars) get ErrorTypeInfo.
            // Only override for ProtocolTypeInfo: for records/entities like
            // `List[PQEntry[TPriority, TElement]]` the gen-param resolution must happen through
            // routine.GenericParameters, not via a bracketed-cache lookup that strips the params.
            if (typeName.Contains(value: '[') && ownerType is ProtocolTypeInfo)
            {
                TypeSymbol? bracketed = _registry.LookupType(name: typeName);
                if (bracketed is ProtocolTypeInfo) ownerType = bracketed;
            }
            routineOwnerType = ownerType;

            baseName = ownerType != null
                ? $"{RoutineInfo.GetTypeIdentity(type: ownerType)}.{methodName}"
                : routine.Name;
        }
        else
        {
            // Top-level function: Module.Name (if module set), else just Name
            string? module = GetCurrentModuleName();
            baseName = string.IsNullOrEmpty(value: module)
                ? routine.Name
                : $"{module}.{routine.Name}";
        }

        // Look up by RegistryKey (BaseName + param types) for overload disambiguation,
        // then fall back to BaseName for the first-overload-wins entry.
        // Set up generic parameter context so ResolveType recognizes T, U, etc.
        // (mirrors Phase 2.5 registration in Signatures.cs)
        // Set OwnerType so `Me` in param types resolves to the concrete owner
        // (e.g. `routine SumS64.combine(you: Me) -> Me` needs Me → SumS64 during param-type
        // resolution at line 137, which happens before routineInfo is looked up).
        RoutineInfo? prevRoutine = _currentRoutine;
        _currentRoutine = new RoutineInfo(name: baseName)
        {
            GenericParameters = routine.GenericParameters,
            OwnerType = routineOwnerType
        };

        RoutineInfo? routineInfo = null;
        if (routine.Parameters.Count > 0)
        {
            IEnumerable<string> paramTypeNames = routine.Parameters
                                                        .Select(selector: p =>
                                                         {
                                                             if (p.Type == null)
                                                             {
                                                                 return "";
                                                             }

                                                             TypeSymbol resolved =
                                                                 ResolveType(
                                                                     typeExpr: p.Type);
                                                             if (resolved is ErrorTypeInfo)
                                                             {
                                                                 return p.Type.Name ?? "";
                                                             }

                                                             // Varargs params are stored as List[T] in the registry
                                                             // (mirrors the wrapping in Signatures.cs Phase 2)
                                                             if (p.IsVariadic)
                                                             {
                                                                 TypeSymbol? listDef =
                                                                     _registry.LookupType(
                                                                         name: "List");
                                                                 if (listDef != null)
                                                                 {
                                                                     resolved =
                                                                         _registry
                                                                            .GetOrCreateResolution(
                                                                                 genericDef:
                                                                                 listDef,
                                                                                 typeArguments:
                                                                                 [resolved]);
                                                                 }
                                                             }

                                                             return RoutineInfo.GetTypeIdentity(
                                                                 type: resolved);
                                                         })
                                                        .Where(predicate: n =>
                                                             !string.IsNullOrEmpty(value: n));
            string paramSig = string.Join(separator: ",", values: paramTypeNames);
            string registryKey = $"{baseName}#{paramSig}";
            routineInfo = _registry.LookupRoutine(fullName: registryKey,
                isFailable: routine.IsFailable);

            // Fallback: extension methods on concrete generic specializations
            // (e.g., `List[Byte].create`) register under the concrete owner type,
            // producing a RegistryKey like `Core.List[Core.Byte].create#Core.Bytes`.
            // The first lookup above used the generic-def-normalized owner
            // (`Core.List[T].create`), so it missed. Resolve the concrete owner
            // type from the routine name and rebuild the canonical key.
            if (routineInfo == null && routine.Name.Contains(value: '.'))
            {
                int dotIdx = routine.Name.IndexOf(value: '.');
                string ownerExprText = routine.Name[..dotIdx];
                string mName = routine.Name[(dotIdx + 1)..];
                if (ownerExprText.Contains(value: '['))
                {
                    TypeExpression? ownerExpr =
                        ParseTypeExpressionString(text: ownerExprText,
                                                  location: routine.Location);
                    if (ownerExpr != null)
                    {
                        TypeSymbol resolvedOwner = ResolveType(typeExpr: ownerExpr);
                        if (resolvedOwner is not ErrorTypeInfo)
                        {
                            string ownerIdentity =
                                RoutineInfo.GetTypeIdentity(type: resolvedOwner);
                            string concreteKey =
                                $"{ownerIdentity}.{mName}#{paramSig}";
                            routineInfo =
                                _registry.LookupRoutine(fullName: concreteKey);
                        }
                    }
                }
            }
        }

        _currentRoutine = prevRoutine;

        // Prefer the overload whose failability matches the routine being analyzed, so
        // bodies of failable variants don't get matched against a non-failable first-wins entry.
        routineInfo ??= _registry.LookupRoutine(fullName: baseName,
            isFailable: routine.IsFailable);
        routineInfo ??= _registry.LookupRoutine(fullName: baseName);

        // Fall back to the original concrete-specialization name (e.g., "Core.List[U16].decode_as_utf16")
        // for extension methods registered under the concrete owner type rather than the generic def.
        if (routineInfo == null && routine.Name.Contains(value: '['))
        {
            string? module = GetCurrentModuleName();
            string concreteName = string.IsNullOrEmpty(value: module)
                ? routine.Name
                : $"{module}.{routine.Name}";
            routineInfo = _registry.LookupRoutine(fullName: concreteName)
                ?? _registry.LookupRoutineByQualifiedName(qualifiedName: concreteName);
        }

        // Final fallback: scan all routines for one with the same method name.
        // Tolerates registration/verification key mismatches for overloaded extension methods
        // and concrete generic specializations. Prefer matching IsFailable to disambiguate
        // overloads that share a base name but differ on '!'.
        if (routineInfo == null && routine.Name.Contains(value: '.'))
        {
            int dot = routine.Name.LastIndexOf(value: '.');
            string methodName = routine.Name[(dot + 1)..];
            routineInfo = _registry.LookupAnyByMethodName(methodName: methodName,
                isFailable: routine.IsFailable);
        }

        if (routineInfo == null)
        {
            // @innate routines may be intentionally skipped at registration
            // (e.g., BuilderService closure-cascading stubs synthesized per-type).
            if (routine.Annotations.Contains(item: "innate"))
            {
                _currentRoutine = prevRoutine;
                return;
            }

            ReportError(code: SemanticDiagnosticCode.UnresolvedRoutineBody,
                message:
                $"Routine '{baseName}' body could not be matched to a registered declaration.",
                location: routine.Location);
            return;
        }

        RoutineInfo? previousRoutine = _currentRoutine;
        _currentRoutine = routineInfo;

        // Deadref tracking is per-routine — clear carries from previous routines.
        _deadrefVariables.Clear();

        _registry.EnterScope(kind: ScopeKind.Function, name: routine.Name);

        // Declare parameters in scope
        foreach (ParameterInfo param in routineInfo.Parameters)
        {
            _registry.DeclareVariable(name: param.Name, type: param.Type);
        }

        // #169: dangerous routine implicit danger context
        bool wasDangerImplicit = false;
        if (routineInfo.IsDangerous && _dangerBlockDepth == 0)
        {
            _dangerBlockDepth = 1;
            wasDangerImplicit = true;
        }

        // @innate routines have compiler-supplied bodies — skip analysis entirely.
        if (routine.Annotations.Contains(item: "innate"))
        {
            if (wasDangerImplicit) _dangerBlockDepth = 0;
            _registry.ExitScope();
            _currentRoutine = previousRoutine;
            return;
        }

        // Analyze body statement
        AnalyzeStatement(statement: routine.Body);

        if (wasDangerImplicit)
        {
            _dangerBlockDepth = 0;
        }

        // Infer Blank return type if no annotation was given and no return value was found.
        // null is a transient "not yet inferred" state — after body analysis it must be resolved.
        routineInfo.ReturnType ??= _registry.LookupType(name: "Blank");

        // Validate that all routines terminate explicitly on every path (#144).
        // Blank-returning routines still require an explicit `return` — implicit fall-off
        // is rejected so control-flow analysis remains uniform across return types.
        if (!StatementAlwaysTerminates(statement: routine.Body))
        {
            ReportError(code: SemanticDiagnosticCode.MissingReturn,
                message: routineInfo.ReturnType is { IsBlank: false }
                    ? $"Routine '{routine.Name}' has return type '{routineInfo.ReturnType.Name}' but not all code paths return a value."
                    : $"Routine '{routine.Name}' does not terminate on all paths. Add an explicit 'return' at the end.",
                location: routine.Location);
        }

        // Failable routine with no throw/absent — error: a ! routine that can't fail is misleading.
        if (routineInfo is
            { IsFailable: true, HasThrow: false, HasAbsent: false, HasFailableCalls: false })
        {
            ReportError(code: SemanticDiagnosticCode.FailableWithoutThrowOrAbsent,
                message:
                $"Failable routine '{routine.Name}!' contains neither 'throw' nor 'absent'. " +
                "Remove '!' or add a failure path.",
                location: routine.Location);
        }

        // Store routine body for error handling variant generation (Phase 5).
        // Only store if the body actually has throw/absent/failable-calls — routines
        // implemented via @llvm_ir have no such AST nodes and can't have variants generated.
        if (routineInfo.IsFailable &&
            (routineInfo.HasThrow || routineInfo.HasAbsent || routineInfo.HasFailableCalls))
        {
            StoreRoutineBody(routine: routineInfo, body: routine.Body);
        }

        // #161: Report undismantled Lookup variables at routine scope exit
        foreach ((string Name, SourceLocation Location) pending in _pendingLookupVars)
        {
            ReportError(code: SemanticDiagnosticCode.LookupNotDismantled,
                message:
                $"Lookup variable '{pending.Name}' must be dismantled before end of scope. " +
                "Use 'when', '??', or 'if is' to handle the lookup result.",
                location: pending.Location);
        }

        _pendingLookupVars.Clear();

        // Snapshot the per-routine "out of scope via steal/consumption" set onto the declaration so
        // the scope-exit teardown pass can exclude these bindings from `$destroy` — `steal` takes the
        // variable out of scope (the callee kills the content), and this deadref record survives even
        // after the `steal` AST wrapper is normalized away during arg lowering.
        routine.StolenVariableNames = [.. _deadrefVariables];

        _registry.ExitScope();

        _currentRoutine = previousRoutine;
    }

    private void AnalyzeStatement(Statement statement)
    {
        switch (statement)
        {
            case BlockStatement block:
                AnalyzeBlockStatement(block: block);
                break;

            case DeclarationStatement decl:
                AnalyzeDeclarationStatement(decl: decl);
                break;

            case ExpressionStatement expr:
                AnalyzeExpressionStatement(expr: expr);
                break;

            case AssignmentStatement assign:
                AnalyzeAssignmentStatement(assign: assign);
                break;

            case IfStatement ifStmt:
                AnalyzeIfStatement(ifStmt: ifStmt);
                break;

            case WhileStatement whileStmt:
                AnalyzeWhileStatement(whileStmt: whileStmt);
                break;

            case LoopStatement loopStmt:
                _registry.EnterScope(kind: ScopeKind.Loop, name: "loop");
                AnalyzeStatement(statement: loopStmt.Body);
                _registry.ExitScope();
                break;

            case ForStatement forStmt:
                AnalyzeForStatement(forStmt: forStmt);
                break;

            case WhenStatement whenStmt:
                AnalyzeWhenStatement(whenStmt: whenStmt);
                break;

            case ReturnStatement ret:
                AnalyzeReturnStatement(ret: ret);
                break;

            case VariantReturnStatement variantReturn:
                AnalyzeVariantReturnStatement(variantReturn: variantReturn);
                break;

            case BecomesStatement becomesStmt:
                AnalyzeBecomesStatement(becomesStmt: becomesStmt);
                break;

            case ThrowStatement throwStmt:
                AnalyzeThrowStatement(throwStmt: throwStmt);
                break;

            case AbsentStatement absent:
                AnalyzeAbsentStatement(absent: absent);
                break;

            case BreakStatement breakStmt:
                AnalyzeBreakStatement(breakStmt: breakStmt);
                break;

            case ContinueStatement continueStmt:
                AnalyzeContinueStatement(continueStmt: continueStmt);
                break;

            case PassStatement:
                // Pass is a no-op statement with no type analysis needed
                break;

            case DestructuringStatement destruct:
                AnalyzeDestructuringStatement(destruct: destruct);
                break;

            case DiscardStatement discard:
                AnalyzeDiscardStatement(discard: discard);
                break;

            case DangerStatement danger:
                AnalyzeDangerStatement(danger: danger);
                break;

            case UsingStatement usingStmt:
                AnalyzeUsingStatement(usingStmt: usingStmt);
                break;

            default:
                ReportWarning(code: SemanticWarningCode.UnknownStatementType,
                    message: $"Internal: semantic analyzer has no handler for AST node '{statement.GetType().Name}'. This statement will be skipped; downstream analysis may be incomplete. Please report as a compiler bug.",
                    location: statement.Location);
                break;
        }
    }

    #endregion

    #region Statement Analysis Methods

    private void AnalyzeVariantReturnStatement(VariantReturnStatement variantReturn)
    {
        if (variantReturn.Value != null)
        {
            AnalyzeExpression(expression: variantReturn.Value);
        }
    }

    private void AnalyzeBlockStatement(BlockStatement block)
    {
        if (block.Statements.Count == 0)
        {
            ReportError(code: SemanticDiagnosticCode.EmptyBlockWithoutPass,
                message: "Empty block requires 'pass' keyword.",
                location: block.Location);
            return;
        }

        _registry.EnterScope(kind: ScopeKind.Block, name: null);

        // Once a statement unconditionally diverges (return / throw / absent / break / continue),
        // any following statement in the same block can never execute — report the first such dead
        // statement. (Conditional divergence inside a nested if/when does NOT terminate this block.)
        bool diverged = false;
        foreach (Statement stmt in block.Statements)
        {
            if (diverged)
            {
                ReportError(code: SemanticDiagnosticCode.UnreachableStatement,
                    message:
                    "Unreachable statement: the previous statement always diverges " +
                    "(return / throw / absent / break / continue), so this code can never run.",
                    location: stmt.Location);
                break;
            }

            AnalyzeStatement(statement: stmt);
            diverged = stmt is ReturnStatement or ThrowStatement or AbsentStatement
                or BreakStatement or ContinueStatement;
        }

        _lastDeclaredVariantVar = null;
        _registry.ExitScope();
    }

    private void AnalyzeDeclarationStatement(DeclarationStatement decl)
    {
        switch (decl.Declaration)
        {
            case VariableDeclaration varDecl:
                AnalyzeVariableDeclaration(varDecl: varDecl);
                break;

            case RoutineDeclaration func:
                // Nested function declaration
                AnalyzeFunctionBody(routine: func);
                break;

            default:
                ReportWarning(code: SemanticWarningCode.UnexpectedDeclaration,
                    message:
                    $"Unexpected declaration in statement context: {decl.Declaration.GetType().Name}",
                    location: decl.Location);
                break;
        }
    }

    private void AnalyzeVariableDeclaration(VariableDeclaration varDecl)
    {
        TypeSymbol varType;

        // Suflae: an entity-reference annotation (`x: E` / `x: E?`) stores a `Roamed[E]` handle. Track
        // whether the annotation made this a nullable slot and whether it is a non-null entity slot.
        bool annotatedNullable = false;
        bool annotatedNonNullEntity = false;

        if (varDecl.Type != null)
        {
            // Explicit type annotation
            varType = ResolveType(typeExpr: varDecl.Type);

            (TypeSymbol resolved, bool isNullable, bool isEntitySlot) =
                ResolveSuflaeEntityAnnotation(annotated: varType);
            varType = resolved;
            annotatedNullable = isNullable;
            annotatedNonNullEntity = isEntitySlot && !isNullable;
        }
        else if (varDecl.Initializer != null)
        {
            // Type inference from initializer
            varType = AnalyzeExpression(expression: varDecl.Initializer);

            // Collection literals analyze to the bare entity type (List[T] / Set[T] / Dict[K,V]),
            // which can't be stored bare per the entity-ownership rule (S413). At binding sites,
            // wrap the inferred type in Owned so the variable holds a destructable handle.
            // This mirrors the binding-vs-rvalue distinction: `alert([1,2,3])` keeps the bare
            // entity (and prints List-shaped output), while `var a = [1,2,3]; alert(a)` makes
            // a Owned (and prints the Owned envelope).
            // Post-Owned-retirement: bound `T` (entity lvalue) is record-shaped storage with
            // entity-ownership semantics layered on top. No wrapper synthesis — the slot holds
            // bare entity T directly.
        }
        else
        {
            ReportError(code: SemanticDiagnosticCode.VariableNeedsTypeOrInitializer,
                message:
                $"Variable '{varDecl.Name}' requires either a type annotation or an initializer.",
                location: varDecl.Location);
            varType = ErrorTypeInfo.Instance;
        }

        // #16: Plain `var x: T` without an initializer is disallowed.
        // Use `lateinit var x: T` (eager allocation, late initialization).
        if (_registry.Language == Language.RazorForge &&
            varDecl is { Type: not null, Initializer: null, IsLateInit: false })
        {
            ReportError(code: SemanticDiagnosticCode.VariableNeedsTypeOrInitializer,
                message:
                $"Variable '{varDecl.Name}' has a type annotation but no initializer. " +
                "Use 'lateinit var' to defer initialization.",
                location: varDecl.Location);
        }

        // If we have both type annotation and initializer, verify compatibility
        if (varDecl is { Type: not null, Initializer: not null })
        {
            TypeSymbol initType =
                AnalyzeExpression(expression: varDecl.Initializer, expectedType: varType);
            if (!IsAssignableTo(source: initType, target: varType))
            {
                ReportError(code: SemanticDiagnosticCode.VariableInitializerTypeMismatch,
                    message:
                    $"Cannot assign value of type '{initType.Name}' to variable of type '{varType.Name}'.",
                    location: varDecl.Location);
            }
        }

        // RazorForge: Entity bare assignment prohibition
        // `var b = a` where `a` is an entity is a build error (must use .share() or steal)
        // Only applies to bare identifier references, not creator calls or function returns
        if (_registry.Language == Language.RazorForge &&
            varDecl.Initializer is IdentifierExpression && varType is EntityTypeInfo)
        {
            ReportError(code: SemanticDiagnosticCode.BareEntityAssignment,
                message:
                $"Cannot directly assign entity of type '{varType.Name}' to variable '{varDecl.Name}'. " +
                "Use '.share()' for shared ownership or 'steal' for ownership transfer.",
                location: varDecl.Location);
        }

        // Post-Owned-retirement: bare entity-typed `var x: T = ...` is the normal bound form.
        // Bound `T` is record-shaped pointer storage with entity-ownership semantics layered on;
        // the no-duplicate-handle rule is enforced separately at copy sites (block above).

        // Variant copy prohibition: `var box2 = box1` is not allowed
        // Variants must be dismantled immediately with pattern matching
        // Binding from routine calls (`var result = make_shape()`) is allowed
        if (varDecl.Initializer is IdentifierExpression && varType is VariantTypeInfo)
        {
            ReportError(code: SemanticDiagnosticCode.VariantCopyNotAllowed,
                message:
                $"Variant type '{varType.Name}' cannot be copied to variable '{varDecl.Name}'. " +
                "Variants must be dismantled immediately with pattern matching.",
                location: varDecl.Location);
        }

        // #96: Claiming[T] cannot be copied or aliased — exclusive lock token
        if (varDecl.Initializer is IdentifierExpression && IsClaimingType(type: varType))
        {
            ReportError(code: SemanticDiagnosticCode.ClaimingCopyNotAllowed,
                message: $"Cannot copy or alias 'Claiming[T]' variable to '{varDecl.Name}'. " +
                         "Claiming tokens are exclusive and cannot be duplicated — use the original variable directly.",
                location: varDecl.Location);
        }

        // #81: Result/Lookup cannot be copied from variable to variable
        // `var r = check_parse!(data)` then `when r` is allowed (call result)
        // `var r2 = r1` where r1: Result[T] is not allowed (variable copy)
        if (varDecl.Initializer is IdentifierExpression &&
            IsCarrierType(type: varType) && !IsMaybeType(type: varType))
        {
            ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeStoredInVariable,
                message: $"'{varType.Name}' cannot be copied to another variable. " +
                         "Dismantle it immediately with 'when', '??', or 'if is'.",
                location: varDecl.Location);
        }

        // Scoped access tokens (Viewing / Modifying / Inspecting / Claiming) cannot bind to a
        // var at all — they only exist inline within their producing expression. Use the
        // value inline (`a.view().x`) or open a scope (`using a.view() as v`).
        if (_registry.Language == Language.RazorForge &&
            IsInlineOnlyTokenType(type: varType))
        {
            string wrapperName = varType.BareName;
            ReportError(code: SemanticDiagnosticCode.ImplicitWrapperCopy,
                message:
                $"'{wrapperName}[…]' is a scoped access token and cannot be stored in '{varDecl.Name}'. " +
                $"Use it inline (e.g. 'expr.member'), or open a scope with 'using expr as {varDecl.Name}'.",
                location: varDecl.Location);
        }
        // Phase 1: warn when the initializer is a "borrowed reference" (identifier or member
        // access chain) and the source type is not trivially copyable. Reference-count bumps,
        // ownership transfers, and weak-handle clones must each appear at the copy site as an
        // explicit verb. See RazorForge-Wiki/docs/Records.md#copy-semantics. Promoted to a
        // hard error once stdlib migration completes (Phase 2).
        else if (_registry.Language == Language.RazorForge &&
            varDecl.Initializer is IdentifierExpression or MemberExpression &&
            !IsTriviallyCopyable(type: varType))
        {
            var hint = FindNonTriviallyCopyableWrapper(type: varType);
            if (hint != null)
            {
                string verb = NonTriviallyCopyableWrappers[key: hint.Value.Wrapper];
                string fieldNote = hint.Value.Path == "<value>"
                    ? $"type '{varType.Name}' is a '{hint.Value.Wrapper}[…]' wrapper"
                    : $"field '{hint.Value.Path}' of type '{hint.Value.Wrapper}[…]'";
                ReportError(code: SemanticDiagnosticCode.ImplicitWrapperCopy,
                    message:
                    $"Implicit copy of '{varDecl.Name}': {fieldNote} requires an explicit copy verb. " +
                    $"Spell out '{verb}' at the copy site, or reconstruct the record with each field's verb.",
                    location: varDecl.Location);
            }
        }

// Register variable in current scope
        // A new declaration shadows any prior steal of the same name in this scope.
        _deadrefVariables.Remove(item: varDecl.Name);

        // Suflae flow typing: a local is nullable when it was annotated `E?`, or (with no annotation)
        // inferred from a nullable entity read (`var n = a.optField`) or a `none` literal — so member
        // access on it is gated until a null-check.
        bool varIsNullable = annotatedNullable ||
            (varDecl.Type == null && varDecl.Initializer != null &&
             IsNullableEntityRead(expr: varDecl.Initializer));

        // Suflae: assigning a possibly-none value into a NON-NULL entity variable (`var x: E = <nullable>`
        // or `var x: E = none`) is rejected — declare the variable optional (`x: E?`) to allow none.
        if (annotatedNonNullEntity && varDecl.Initializer != null &&
            IsNullableEntityRead(expr: varDecl.Initializer))
        {
            ReportNullableIntoNonNull(target: $"variable '{varDecl.Name}'",
                value: varDecl.Initializer, optionalHint: $"{varDecl.Name}: <Type>?");
        }

        bool declared = _registry.DeclareVariable(name: varDecl.Name, type: varType,
            isNullable: varIsNullable);

        if (!declared)
        {
            ReportError(code: SemanticDiagnosticCode.VariableRedeclaration,
                message: $"Variable '{varDecl.Name}' is already declared in this scope.",
                location: varDecl.Location);
        }

        // #19: Propagate lock policy from share[Policy]() to the declared variable
        if (_lastSharePolicy != null && varDecl.Initializer is GenericMethodCallExpression
            {
                MethodName: "share"
            })
        {
            _variableLockPolicies[key: varDecl.Name] = _lastSharePolicy.Value.Policy;
            _lastSharePolicy = null;
        }

        // RF-S630: track the controller identity of a Shared/Watched handle so the
        // readers-XOR-writer check keys on the shared DATA, not the variable name — a clone
        // (`var s2 = s.share()`) inherits `s`'s identity and so conflicts with it.
        if (_registry.Language == Language.RazorForge &&
            varType.BareName is Compiler.Resolution.RuntimeContract.Shared or Compiler.Resolution.RuntimeContract.Watched)
        {
            RecordSharedHandleIdentity(name: varDecl.Name, initializer: varDecl.Initializer);
        }

        // #161: Track Lookup variables that must be dismantled before scope exit
        if (GetCarrierBaseName(type: varType) == "Lookup" &&
            varDecl.Initializer is not IdentifierExpression)
        {
            _pendingLookupVars.Add(item: (varDecl.Name, varDecl.Location));
        }
    }

    private void AnalyzeExpressionStatement(ExpressionStatement expr)
    {
        // Analyze the expression for side effects and type validation
        TypeSymbol exprType = AnalyzeExpression(expression: expr.Expression);

        // Note: UnhandledCrashableCall check moved to AnalyzeCallExpression to catch all contexts
        // (return values, assignments, nested expressions — not just expression statements)

        // Check if this is a call expression with a non-Blank return value
        // If so, warn that the return value is unused (use 'discard' to explicitly ignore)
        if (expr.Expression is CallExpression call && !exprType.IsBlank)
        {
            // Get a readable name for the routine being called
            string routineName = call.Callee switch
            {
                IdentifierExpression id => id.Name,
                MemberExpression member => member.MemberName,
                _ => "routine"
            };

            ReportWarning(code: SemanticWarningCode.UnusedRoutineReturnValue,
                message: $"Return value of '{routineName}()' ({exprType.Name}) is unused. " +
                         "Use 'discard' to explicitly ignore the return value, or assign it to a variable.",
                location: call.Location);
        }
    }

    private void AnalyzeAssignmentStatement(AssignmentStatement assign)
    {
        // Re-binding clears prior deadref state for a simple identifier target.
        // Mirrors AnalyzeVariableDeclaration: an assignment establishes a fresh value,
        // so a previously stolen-from binding becomes live again at this point.
        if (assign.Target is IdentifierExpression rebindId)
        {
            _deadrefVariables.Remove(item: rebindId.Name);
        }

        // #173: Tuple assignment destructuring — (a, b) = (b, a)
        if (assign.Target is TupleLiteralExpression tupleLhs)
        {
            TypeSymbol rhsType = AnalyzeExpression(expression: assign.Value);

            // Verify all elements of the LHS tuple are assignable targets
            foreach (Expression element in tupleLhs.Elements)
            {
                AnalyzeExpression(expression: element);
                if (!IsAssignableTarget(target: element))
                {
                    ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                        message:
                        "All elements of tuple destructuring must be assignable targets (variables, member accesses, or indices).",
                        location: element.Location);
                }

                // Check modifiability for identifier elements
                if (element is IdentifierExpression elemId)
                {
                    VariableInfo? varInfo = _registry.LookupVariable(name: elemId.Name);
                    if (varInfo is { IsModifiable: false })
                    {
                        ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                            message: $"Cannot assign to preset variable '{elemId.Name}'.",
                            location: assign.Location);
                    }
                }
            }

            // Check that RHS is a tuple with matching arity
            if (rhsType is TupleTypeInfo tupleType &&
                tupleLhs.Elements.Count != tupleType.ElementTypes.Count)
            {
                ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                    message:
                    $"Tuple destructuring has {tupleLhs.Elements.Count} targets but the value has {tupleType.ElementTypes.Count} elements.",
                    location: assign.Location);
            }

            return;
        }

        TypeSymbol targetType = AnalyzeExpression(expression: assign.Target);
        TypeSymbol valueType = AnalyzeExpression(expression: assign.Value, expectedType: targetType);

        // Check if target is assignable (variable, member variable, or index)
        if (!IsAssignableTarget(target: assign.Target))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                message: "Invalid assignment target. Only variables, member accesses (e.g. obj.field), and indexed expressions (e.g. list[i]) can be assigned to.",
                location: assign.Target.Location);
            return;
        }

        // Check modifiability
        if (assign.Target is IdentifierExpression id)
        {
            VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
            if (varInfo is { IsModifiable: false })
            {
                ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                    message: $"Cannot assign to preset variable '{id.Name}'.",
                    location: assign.Location);
            }
        }

        // Validate member variable write access (setter visibility)
        if (assign.Target is MemberExpression member)
        {
            TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

            // Read-only wrapper types (Viewing, Inspecting) cannot be written through
            if (IsReadOnlyWrapper(type: objectType))
            {
                ReportError(code: SemanticDiagnosticCode.WriteThroughReadOnlyWrapper,
                    message:
                    $"Cannot write to member '{member.MemberName}' through read-only wrapper '{objectType.Name}'. " +
                    "Use Modifying[T] for exclusive write access or Claiming[T] for locked write access.",
                    location: assign.Location);
            }

            ValidateMemberVariableWriteAccess(objectType: objectType,
                memberVariableName: member.MemberName,
                location: assign.Location);

            // Preset enforcement: cannot assign to member variables of preset variables
            if (member.Object is IdentifierExpression memberVariableTarget)
            {
                VariableInfo? targetVar =
                    _registry.LookupVariable(name: memberVariableTarget.Name);
                if (targetVar is { IsModifiable: false })
                {
                    ReportError(code: SemanticDiagnosticCode.MemberVariableAssignmentOnImmutable,
                        message:
                        $"Cannot assign to member variable '{member.MemberName}' of preset variable '{memberVariableTarget.Name}'.",
                        location: assign.Location);
                }
            }

            // Check if we're in a @readonly method trying to mutate 'me'
            if (_currentRoutine is { IsReadOnly: true } &&
                member.Object is IdentifierExpression { Name: "me" })
            {
                ReportError(code: SemanticDiagnosticCode.MutationInReadonlyMethod,
                    message:
                    $"Cannot mutate member variable '{member.MemberName}' in a @readonly method. " +
                    "Use @migratable to allow mutations.",
                    location: assign.Location);
            }
        }

        // #81: Result/Lookup cannot be copied from variable to variable via assignment
        if (assign.Value is IdentifierExpression &&
            IsCarrierType(type: valueType) && !IsMaybeType(type: valueType))
        {
            ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeStoredInVariable,
                message: $"'{valueType.Name}' cannot be copied to another variable. " +
                         "Dismantle it immediately with 'when', '??', or 'if is'.",
                location: assign.Location);
        }

        // Check type compatibility
        if (!IsAssignableTo(source: valueType, target: targetType))
        {
            ReportError(code: SemanticDiagnosticCode.AssignmentTypeMismatch,
                message:
                $"Cannot assign value of type '{valueType.Name}' to target of type '{targetType.Name}'.",
                location: assign.Location);
        }
    }

    #endregion

    #region Type Expression Parsing Helpers

    /// <summary>
    /// Parses a textual type expression like "List[Byte]" or "Dict[K, List[V]]"
    /// into a synthetic <see cref="TypeExpression"/> AST node. Used to recover
    /// owner-type bindings from a routine's name string when the AST does not
    /// carry the owner as a separate node. Returns null on malformed input.
    /// </summary>
    internal static TypeExpression? ParseTypeExpressionString(string text,
        SourceLocation location)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(value: text)) return null;

        int bracketIdx = text.IndexOf(value: '[');
        if (bracketIdx < 0)
        {
            return new TypeExpression(Name: text,
                GenericArguments: null,
                Location: location);
        }

        if (!text.EndsWith(value: ']')) return null;
        string head = text[..bracketIdx];
        string inner = text[(bracketIdx + 1)..^1];

        List<TypeExpression> args = [];
        int depth = 0;
        int start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[index: i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == ',' && depth == 0)
            {
                TypeExpression? arg = ParseTypeExpressionString(
                    text: inner[start..i], location: location);
                if (arg == null) return null;
                args.Add(item: arg);
                start = i + 1;
            }
        }

        TypeExpression? lastArg =
            ParseTypeExpressionString(text: inner[start..], location: location);
        if (lastArg == null) return null;
        args.Add(item: lastArg);

        return new TypeExpression(Name: head,
            GenericArguments: args,
            Location: location);
    }

    #endregion
}
