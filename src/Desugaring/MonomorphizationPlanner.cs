namespace Compiler.Desugaring;

using Compiler.CodeGen;
using Compiler.Resolution;
using SemanticVerification;
using SemanticVerification.Enums;
using SemanticVerification.Symbols;
using SemanticVerification.Types;
using SyntaxTree;

// MonomorphizedBody record is defined in src/Desugaring/MonomorphizedBody.cs
// and shared between the synthesis pass and this codegen planner.

/// <summary>
/// Entry for a pending generic monomorphization.
/// Moved from the inline private record in <see cref="LLVMCodeGenerator"/>.
/// </summary>
internal record MonomorphizationEntry(
    RoutineInfo GenericMethod,
    TypeInfo ResolvedOwnerType,
    Dictionary<string, TypeInfo> TypeSubstitutions,
    string GenericAstName,
    Dictionary<string, TypeInfo>? MethodTypeSubstitutions = null);

/// <summary>
/// Collects, pre-rewrites, and provides all generic method monomorphizations needed
/// for a compilation. Owns the pending-monomorphization dictionary that was previously
/// embedded directly in <see cref="LLVMCodeGenerator"/>.
///
/// <para>Lifecycle:</para>
/// <list type="number">
///   <item>Codegen expression emitters call <see cref="Record"/> as they encounter
///         generic method calls (same demand-driven discovery as before).</item>
///   <item>After each round of discovery, <see cref="PreRewriteAll"/> is called to
///         rewrite every pending entry into a concrete <see cref="MonomorphizedBody"/>
///         using <see cref="GenericAstRewriter"/>.</item>
///   <item>Codegen then emits bodies from <see cref="MonomorphizedBodies"/> — no AST
///         search or type-substitution building happens inside the emitter loop.</item>
/// </list>
/// </summary>
internal sealed class MonomorphizationPlanner
{
    private readonly TypeRegistry _registry;
    private readonly IReadOnlyList<(Program Program, string FilePath, string Module)> _userPrograms;
    private readonly IReadOnlyList<(Program Program, string FilePath, string Module)> _stdlibPrograms;

    /// <summary>
    /// Pre-rewritten bodies produced by <see cref="Desugaring.Passes.GenericMonomorphizationPass"/>
    /// at synthesis time, keyed by the concrete <see cref="SemanticVerification.Symbols.RoutineInfo.RegistryKey"/>.
    /// The planner checks this map before doing AST search; if found, it wraps the pre-built body
    /// into a <see cref="MonomorphizedBody"/> entry without rewriting.
    /// </summary>
    private readonly IReadOnlyDictionary<string, MonomorphizedBody> _preMonomorphizedBodies;

    /// <summary>Pending entries keyed by mangled function name.</summary>
    private readonly Dictionary<string, MonomorphizationEntry> _pending = new();

    /// <summary>Pre-rewritten bodies keyed by mangled function name.</summary>
    public Dictionary<string, MonomorphizedBody> MonomorphizedBodies { get; } = new();

    /// <summary>Read-only view of all recorded (but not yet rewritten) entries.</summary>
    public IReadOnlyDictionary<string, MonomorphizationEntry> PendingMonomorphizations => _pending;

    public MonomorphizationPlanner(
        TypeRegistry registry,
        IReadOnlyList<(Program, string, string)> userPrograms,
        IReadOnlyList<(Program, string, string)> stdlibPrograms,
        IReadOnlyDictionary<string, MonomorphizedBody>? preMonomorphizedBodies = null)
    {
        _registry = registry;
        _userPrograms = userPrograms;
        _stdlibPrograms = stdlibPrograms;
        _preMonomorphizedBodies = preMonomorphizedBodies
            ?? new Dictionary<string, MonomorphizedBody>();
    }

    // ─── Recording ───────────────────────────────────────────────────────────

    /// <summary>
    /// Directly adds a <see cref="MonomorphizationEntry"/> by mangled name without going through
    /// the <see cref="Record"/> dispatch logic. Used for non-standard cases (standalone variants,
    /// free-function generic instances, direct owner-resolution entries in expression emitters).
    /// </summary>
    public bool AddDirectEntry(string mangledName, MonomorphizationEntry entry)
    {
        if (_pending.ContainsKey(key: mangledName))
            return false;
        _pending[key: mangledName] = entry;
        return true;
    }

    /// <summary>Returns true if an entry is already pending for the given mangled name.</summary>
    public bool HasEntry(string mangledName) => _pending.ContainsKey(key: mangledName);

    /// <summary>
    /// Records a pending monomorphization for a resolved generic method call.
    /// Corresponds to the four cases in the old <c>RecordMonomorphization</c>:
    /// generic-parameter owner, protocol owner, generic-type owner, method-level generics,
    /// and generated-variant (try_/check_/lookup_) on a concrete owner.
    /// </summary>
    public void Record(string mangledName, RoutineInfo genericMethod,
        TypeInfo resolvedOwnerType, Dictionary<string, TypeInfo>? methodTypeArgs = null)
    {
        if (_pending.ContainsKey(key: mangledName))
            return;

        // Case 1: Generic-parameter owner (e.g. routine T.view() called on Point)
        if (genericMethod.OwnerType is GenericParameterTypeInfo genParam)
        {
            var typeSubs = new Dictionary<string, TypeInfo>
            {
                [key: genParam.Name] = resolvedOwnerType
            };
            MergeMethodTypeArgs(typeSubs, methodTypeArgs);
            string genericAstName = $"{genParam.Name}.{genericMethod.Name}";
            _pending[key: mangledName] = new MonomorphizationEntry(
                GenericMethod: genericMethod,
                ResolvedOwnerType: resolvedOwnerType,
                TypeSubstitutions: typeSubs,
                GenericAstName: genericAstName,
                MethodTypeSubstitutions: methodTypeArgs);
            return;
        }

        // Case 2: Protocol-owned generic methods (e.g. Iterable[T].enumerate())
        if (genericMethod.OwnerType is ProtocolTypeInfo protocolOwner &&
            protocolOwner.GenericParameters is { Count: > 0 })
        {
            var typeSubs = new Dictionary<string, TypeInfo>();
            if (resolvedOwnerType.TypeArguments != null)
            {
                for (int i = 0;
                     i < protocolOwner.GenericParameters.Count &&
                     i < resolvedOwnerType.TypeArguments.Count;
                     i++)
                {
                    typeSubs[key: protocolOwner.GenericParameters[index: i]] =
                        resolvedOwnerType.TypeArguments[index: i];
                }
            }
            MergeMethodTypeArgs(typeSubs, methodTypeArgs);
            string protoParamList =
                string.Join(separator: ", ", values: protocolOwner.GenericParameters);
            string genericAstName = $"{protocolOwner.Name}[{protoParamList}].{genericMethod.Name}";
            _pending[key: mangledName] = new MonomorphizationEntry(
                GenericMethod: genericMethod,
                ResolvedOwnerType: resolvedOwnerType,
                TypeSubstitutions: typeSubs,
                GenericAstName: genericAstName,
                MethodTypeSubstitutions: methodTypeArgs);
            return;
        }

        // Case 3: Generic-type owner (e.g. List[S64].add_last, Snatched[T].$eq)
        TypeInfo? genericDef = resolvedOwnerType switch
        {
            EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
            // WrapperTypeInfo (Snatched, Retained, etc.) stores the inner type in TypeArguments
            // but has no GenericDefinition property — use the method's declared owner type as the def.
            WrapperTypeInfo { TypeArguments: { Count: > 0 } } when
                genericMethod.OwnerType is { IsGenericDefinition: true } =>
                genericMethod.OwnerType,
            _ => null
        };

        if (genericDef?.GenericParameters != null)
        {
            var typeSubs = new Dictionary<string, TypeInfo>();
            if (resolvedOwnerType.TypeArguments != null)
            {
                for (int i = 0;
                     i < genericDef.GenericParameters.Count &&
                     i < resolvedOwnerType.TypeArguments.Count;
                     i++)
                {
                    typeSubs[key: genericDef.GenericParameters[index: i]] =
                        resolvedOwnerType.TypeArguments[index: i];
                }
            }
            MergeMethodTypeArgs(typeSubs, methodTypeArgs);
            string paramList = string.Join(separator: ", ", values: genericDef.GenericParameters);
            string genericAstName = $"{genericDef.Name}[{paramList}].{genericMethod.Name}";
            _pending[key: mangledName] = new MonomorphizationEntry(
                GenericMethod: genericMethod,
                ResolvedOwnerType: resolvedOwnerType,
                TypeSubstitutions: typeSubs,
                GenericAstName: genericAstName,
                MethodTypeSubstitutions: methodTypeArgs);
            return;
        }

        // Case 4: Method-level generics on a non-generic owner (e.g. Text.$create[T])
        if (methodTypeArgs is { Count: > 0 } && genericMethod.IsGenericDefinition)
        {
            _pending[key: mangledName] = new MonomorphizationEntry(
                GenericMethod: genericMethod,
                ResolvedOwnerType: resolvedOwnerType,
                TypeSubstitutions: new Dictionary<string, TypeInfo>(dictionary: methodTypeArgs),
                GenericAstName: genericMethod.BaseName,
                MethodTypeSubstitutions: methodTypeArgs);
            return;
        }

        // Case 5: Generated variant (try_/check_/lookup_) on a concrete owner
        if (genericMethod.OriginalName != null)
        {
            string concreteAstName = $"{resolvedOwnerType.Name}.{genericMethod.Name}";
            _pending[key: mangledName] = new MonomorphizationEntry(
                GenericMethod: genericMethod,
                ResolvedOwnerType: resolvedOwnerType,
                TypeSubstitutions: new Dictionary<string, TypeInfo>(),
                GenericAstName: concreteAstName,
                MethodTypeSubstitutions: null);
        }
    }

    private static void MergeMethodTypeArgs(Dictionary<string, TypeInfo> target,
        Dictionary<string, TypeInfo>? methodTypeArgs)
    {
        if (methodTypeArgs == null) return;
        foreach ((string key, TypeInfo value) in methodTypeArgs)
            target[key: key] = value;
    }

    // ─── Pre-rewriting ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds <see cref="MonomorphizedBodies"/> from all currently-pending entries.
    /// Runs a fixed-point loop because rewriting one entry may expose more (transitive
    /// generic calls inside stdlib bodies are walked and recorded here).
    /// </summary>
    public void PreRewriteAll(IReadOnlyDictionary<string, Statement> synthesizedBodies)
    {
        int prevCount;
        do
        {
            prevCount = MonomorphizedBodies.Count;
            foreach ((string mangledName, MonomorphizationEntry entry) in _pending.ToList())
            {
                if (MonomorphizedBodies.ContainsKey(key: mangledName))
                    continue;

                MonomorphizedBody? body = BuildBody(
                    mangledName: mangledName,
                    entry: entry,
                    synthesizedBodies: synthesizedBodies);
                if (body != null)
                    MonomorphizedBodies[key: mangledName] = body;
            }
        } while (MonomorphizedBodies.Count > prevCount);
    }

    /// <summary>
    /// Public entry point for on-demand body building (used by the codegen fallback path).
    /// </summary>
    public MonomorphizedBody? BuildBodyPublic(string mangledName, MonomorphizationEntry entry,
        IReadOnlyDictionary<string, Statement> synthesizedBodies) =>
        BuildBody(mangledName: mangledName, entry: entry, synthesizedBodies: synthesizedBodies);

    private MonomorphizedBody? BuildBody(string mangledName, MonomorphizationEntry entry,
        IReadOnlyDictionary<string, Statement> synthesizedBodies)
    {
        // ── Fast path: use pre-built body from GenericMonomorphizationPass ────
        // Compute the concrete RoutineInfo so we can look up by RegistryKey.
        RoutineInfo quickInfo = BuildRoutineInfo(entry: entry);
        if (_preMonomorphizedBodies.TryGetValue(key: quickInfo.RegistryKey,
                value: out MonomorphizedBody? preBuilt))
        {
            // The synthesis pass built this body already — use it directly.
            return preBuilt;
        }

        string? firstParamGenericType = null;
        if (entry.GenericMethod is { Name: "$create", Parameters.Count: > 0 })
        {
            TypeInfo paramType = entry.GenericMethod.Parameters[index: 0].Type;
            firstParamGenericType = GetGenericBaseName(type: paramType) ?? paramType.Name;
        }

        RoutineDeclaration? astRoutine = FindAstRoutine(
            genericAstName: entry.GenericAstName,
            expectedParamCount: entry.GenericMethod.Parameters.Count,
            firstParamTypeHint: firstParamGenericType,
            typeSubstitutions: entry.TypeSubstitutions);

        // Fallback: concrete name for non-generic specializations
        if (astRoutine == null && entry.ResolvedOwnerType != null)
        {
            string concreteName = $"{entry.ResolvedOwnerType.Name}.{entry.GenericMethod.Name}";
            astRoutine = FindAstRoutine(
                genericAstName: concreteName,
                expectedParamCount: entry.GenericMethod.Parameters.Count,
                firstParamTypeHint: firstParamGenericType,
                typeSubstitutions: entry.TypeSubstitutions);
        }

        if (astRoutine == null)
        {
            // IsSynthesized: no source AST — emit body directly in codegen
            if (entry.GenericMethod.IsSynthesized)
            {
                RoutineInfo synthInfo = BuildRoutineInfo(entry: entry);

                // Check for a synthesized AST body on the generic definition
                Statement? synthAstBody = null;
                TypeInfo? ownerGenDef = entry.ResolvedOwnerType switch
                {
                    EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
                    RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
                    _ => null
                };
                if (ownerGenDef != null)
                {
                    RoutineInfo? genDefMethod =
                        _registry.LookupMethod(type: ownerGenDef, methodName: synthInfo.Name);
                    if (genDefMethod != null)
                        synthesizedBodies.TryGetValue(key: genDefMethod.RegistryKey,
                            out synthAstBody);
                }

                if (synthAstBody != null)
                {
                    // Build string subs and rewrite the synthesized body
                    var astSubs = BuildStringSubs(entry.TypeSubstitutions);
                    Statement rewritten = GenericAstRewriter.RewriteStatement(synthAstBody, astSubs);
                    // Wrap in a shell RoutineDeclaration so codegen can use EmitSynthesizedBodyFromAst
                    var shellDecl = new RoutineDeclaration(
                        Name: synthInfo.Name,
                        Parameters: [],
                        ReturnType: null,
                        Body: rewritten,
                        Visibility: VisibilityModifier.Open,
                        Annotations: [],
                        Location: synthInfo.Location ?? new SourceLocation("", 0, 0, 0));
                    return new MonomorphizedBody(
                        Ast: shellDecl,
                        Info: synthInfo,
                        TypeSubs: entry.TypeSubstitutions,
                        VariantStatus: null,
                        VariantInnerType: null,
                        IsSynthesized: true);
                }

                // Pure synthesized — no AST, emit body directly (IsSynthesized=true, Ast unused)
                return new MonomorphizedBody(
                    Ast: new RoutineDeclaration(
                        Name: synthInfo.Name,
                        Parameters: [],
                        ReturnType: null,
                        Body: new BlockStatement(Statements: [], Location: synthInfo.Location ?? new SourceLocation("", 0, 0, 0)),
                        Visibility: VisibilityModifier.Open,
                        Annotations: [],
                        Location: synthInfo.Location ?? new SourceLocation("", 0, 0, 0)),
                    Info: synthInfo,
                    TypeSubs: entry.TypeSubstitutions,
                    VariantStatus: null,
                    VariantInnerType: null,
                    IsSynthesized: true);
            }

            // Generated variant fallback: try_/check_/lookup_ — use OriginalName body
            if (entry.GenericMethod.OriginalName != null)
            {
                TypeInfo? variantOwner = entry.GenericMethod.OwnerType;
                if (variantOwner is EntityTypeInfo { IsGenericDefinition: false } eOwner &&
                    eOwner.GenericDefinition != null)
                    variantOwner = eOwner.GenericDefinition;
                else if (variantOwner is RecordTypeInfo { IsGenericDefinition: false } rOwner &&
                         rOwner.GenericDefinition != null)
                    variantOwner = rOwner.GenericDefinition;

                string fallbackAstName;
                if (variantOwner is { IsGenericDefinition: true, GenericParameters: not null })
                {
                    string paramList = string.Join(separator: ", ", values: variantOwner.GenericParameters);
                    fallbackAstName = $"{variantOwner.Name}[{paramList}].{entry.GenericMethod.OriginalName}";
                }
                else if (variantOwner != null)
                    fallbackAstName = $"{variantOwner.Name}.{entry.GenericMethod.OriginalName}";
                else
                    fallbackAstName = entry.GenericMethod.OriginalName;

                astRoutine = FindAstRoutine(
                    genericAstName: fallbackAstName,
                    expectedParamCount: entry.GenericMethod.Parameters.Count,
                    firstParamTypeHint: firstParamGenericType);
            }

            if (astRoutine == null)
                return null;
        }

        RoutineInfo resolvedInfo = BuildRoutineInfo(entry: entry);

        // Carrier-unwrapping for generated variants
        AsyncStatus? variantStatus = null;
        TypeInfo? variantInnerType = null;
        if (resolvedInfo.OriginalName != null && resolvedInfo.ReturnType?.TypeArguments is { Count: > 0 })
        {
            if (LLVMCodeGenerator.GetGenericBaseNameStatic(type: resolvedInfo.ReturnType) == "Lookup")
            {
                variantStatus = AsyncStatus.LookupVariant;
                variantInnerType = resolvedInfo.ReturnType.TypeArguments[index: 0];
            }
            else if (LLVMCodeGenerator.GetGenericBaseNameStatic(type: resolvedInfo.ReturnType) == "Result")
            {
                variantStatus = AsyncStatus.CheckVariant;
                variantInnerType = resolvedInfo.ReturnType.TypeArguments[index: 0];
            }
        }

        if (variantStatus != null && variantInnerType != null)
        {
            resolvedInfo = new RoutineInfo(name: resolvedInfo.Name)
            {
                Kind = resolvedInfo.Kind,
                OwnerType = resolvedInfo.OwnerType,
                Parameters = resolvedInfo.Parameters,
                ReturnType = variantInnerType,
                IsFailable = resolvedInfo.IsFailable,
                DeclaredModification = resolvedInfo.DeclaredModification,
                ModificationCategory = resolvedInfo.ModificationCategory,
                Visibility = resolvedInfo.Visibility,
                Location = resolvedInfo.Location,
                Module = resolvedInfo.Module,
                Annotations = resolvedInfo.Annotations,
                CallingConvention = resolvedInfo.CallingConvention,
                IsVariadic = resolvedInfo.IsVariadic,
                IsDangerous = resolvedInfo.IsDangerous,
                Storage = resolvedInfo.Storage,
                AsyncStatus = variantStatus.Value,
                OriginalName = resolvedInfo.OriginalName
            };
        }

        var stringSubs = BuildStringSubs(entry.TypeSubstitutions);

        // Pre-built variant body from ErrorHandlingVariantPass
        if (resolvedInfo.OriginalName != null &&
            synthesizedBodies.TryGetValue(key: entry.GenericMethod.RegistryKey,
                out Statement? prebuiltVariantBody))
        {
            Statement rewrittenVariant =
                GenericAstRewriter.RewriteStatement(prebuiltVariantBody, stringSubs);
            var variantDecl = new RoutineDeclaration(
                Name: resolvedInfo.Name,
                Parameters: [],
                ReturnType: null,
                Body: rewrittenVariant,
                Visibility: VisibilityModifier.Open,
                Annotations: [],
                Location: resolvedInfo.Location ?? new SourceLocation("", 0, 0, 0));
            return new MonomorphizedBody(
                Ast: variantDecl,
                Info: resolvedInfo,
                TypeSubs: entry.TypeSubstitutions,
                VariantStatus: variantStatus,
                VariantInnerType: variantInnerType,
                IsSynthesized: false);
        }

        RoutineDeclaration rewrittenAst =
            GenericAstRewriter.Rewrite(routine: astRoutine, subs: stringSubs,
                typeSubs: entry.TypeSubstitutions, registry: _registry);

        return new MonomorphizedBody(
            Ast: rewrittenAst,
            Info: resolvedInfo,
            TypeSubs: entry.TypeSubstitutions,
            VariantStatus: variantStatus,
            VariantInnerType: variantInnerType,
            IsSynthesized: false);
    }

    private static Dictionary<string, string> BuildStringSubs(
        Dictionary<string, TypeInfo> typeSubs)
    {
        var stringSubs = new Dictionary<string, string>(capacity: typeSubs.Count);
        foreach ((string paramName, TypeInfo typeInfo) in typeSubs)
            stringSubs[key: paramName] = typeInfo.Name;
        return stringSubs;
    }

    // ─── AST lookup ──────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the AST declaration for a generic routine across user and stdlib programs.
    /// Moved from <c>LLVMCodeGenerator.FindGenericAstRoutine</c>.
    /// </summary>
    public RoutineDeclaration? FindAstRoutine(string genericAstName,
        int expectedParamCount = -1, string? firstParamTypeHint = null,
        IReadOnlyDictionary<string, TypeInfo>? typeSubstitutions = null)
    {
        bool requireGeneric = genericAstName.EndsWith(value: "[generic]");
        string baseName = requireGeneric
            ? genericAstName[..genericAstName.IndexOf(value: "[generic]")]
            : genericAstName;

        RoutineDeclaration? firstMatch = null;

        bool MatchesRoutine(RoutineDeclaration routine)
        {
            // The parser strips '!' from failable method names (IsFailable=true, Name without '!').
            // The baseName passed here may include '!' (e.g. "List[T].$getitem!"), so also match
            // when the routine is failable and its name with '!' appended equals baseName.
            if (routine.Name != baseName && !(routine.IsFailable && routine.Name + "!" == baseName))
                return false;
            if (requireGeneric && routine.GenericParameters is not { Count: > 0 })
                return false;
            if (expectedParamCount >= 0 && routine.Parameters.Count != expectedParamCount)
                return false;
            // Exclude overloads whose explicit generic constraints are violated by the
            // concrete type substitutions. This ensures e.g. Maybe[T].$represent()
            // needs T is RecordType is NOT selected when T is an entity type.
            if (!ConstraintsSatisfied(routine: routine, subs: typeSubstitutions))
                return false;
            return true;
        }

        bool MatchesParamType(RoutineDeclaration routine)
        {
            if (firstParamTypeHint == null || routine.Parameters.Count == 0)
                return true;
            string astParamType = routine.Parameters[index: 0].Type.Name;
            return astParamType.StartsWith(value: firstParamTypeHint);
        }

        foreach ((Program userProgram, _, _) in _userPrograms)
        {
            foreach (IAstNode decl in userProgram.Declarations)
            {
                if (decl is RoutineDeclaration routine && MatchesRoutine(routine))
                {
                    firstMatch ??= routine;
                    if (MatchesParamType(routine))
                        return routine;
                }
            }
        }

        foreach ((Program program, _, _) in _stdlibPrograms)
        {
            foreach (IAstNode decl in program.Declarations)
            {
                if (decl is RoutineDeclaration routine && MatchesRoutine(routine))
                {
                    firstMatch ??= routine;
                    if (MatchesParamType(routine))
                        return routine;
                }
            }
        }

        if (firstMatch != null && firstParamTypeHint == null)
            return firstMatch;

        if (expectedParamCount >= 0)
        {
            return FindAstRoutine(genericAstName: genericAstName,
                expectedParamCount: -1,
                firstParamTypeHint: firstParamTypeHint,
                typeSubstitutions: typeSubstitutions);
        }

        return null;
    }

    /// <summary>
    /// Returns true if all explicit generic constraints on <paramref name="routine"/> are
    /// satisfied by the concrete type substitutions in <paramref name="subs"/>.
    /// Routines with no constraints always satisfy this check.
    /// Structural constraints (ValueType, ReferenceType, ChoiceType, FlagsType, VariantType)
    /// are checked directly. ConstGeneric constraints whose type name is one of the structural
    /// sentinel names ("RecordType", "EntityType", etc.) are also checked — these arise because
    /// stdlib .rf files write <c>needs T is RecordType</c> where <c>RecordType</c> is an
    /// identifier, so the parser produces ConstraintKind.ConstGeneric rather than ValueType.
    /// Protocol (Obeys) and true const-generic (N is Address) constraints are trusted to the SA.
    /// </summary>
    private static bool ConstraintsSatisfied(RoutineDeclaration routine,
        IReadOnlyDictionary<string, TypeInfo>? subs)
    {
        if (routine.GenericConstraints is not { Count: > 0 }) return true;
        if (subs == null || subs.Count == 0) return true;

        foreach (GenericConstraintDeclaration c in routine.GenericConstraints)
        {
            if (!subs.TryGetValue(key: c.ParameterName, value: out TypeInfo? actual)) continue;
            bool ok = c.ConstraintType switch
            {
                ConstraintKind.ValueType     => actual is RecordTypeInfo,
                ConstraintKind.ReferenceType => actual is EntityTypeInfo,
                ConstraintKind.ChoiceType    => actual is ChoiceTypeInfo,
                ConstraintKind.FlagsType     => actual is FlagsTypeInfo,
                ConstraintKind.VariantType   => actual is VariantTypeInfo,
                // "needs T is RecordType/EntityType/..." in stdlib .rf files is parsed as
                // ConstGeneric because RecordType/EntityType are identifiers, not keywords.
                ConstraintKind.ConstGeneric  => CheckStructuralConstGeneric(c: c, actual: actual),
                _                            => true, // Obeys/TypeEquality: trust SA
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// Checks a <see cref="ConstraintKind.ConstGeneric"/> constraint that may encode a
    /// structural type-category requirement (RecordType, EntityType, etc.) rather than a true
    /// const-generic size constraint.  Returns <see langword="false"/> only when the sentinel
    /// name is recognized AND the concrete type does NOT match the category; unrecognized names
    /// (real const generics like <c>N is Address</c>) always return <see langword="true"/>.
    /// </summary>
    private static bool CheckStructuralConstGeneric(GenericConstraintDeclaration c, TypeInfo actual)
    {
        string? typeName = c.ConstraintTypes is { Count: > 0 } ? c.ConstraintTypes[0].Name : null;
        return typeName switch
        {
            "RecordType"  => actual is RecordTypeInfo,
            "EntityType"  => actual is EntityTypeInfo,
            "ChoiceType"  => actual is ChoiceTypeInfo,
            "FlagsType"   => actual is FlagsTypeInfo,
            "VariantType" => actual is VariantTypeInfo,
            _             => true // True const generic (N is Address) — trust SA
        };
    }

    // ─── RoutineInfo building ─────────────────────────────────────────────────

    /// <summary>
    /// Builds the concrete <see cref="RoutineInfo"/> for a monomorphized generic routine.
    /// Moved from <c>LLVMCodeGenerator.BuildResolvedRoutineInfo</c>.
    /// </summary>
    public RoutineInfo BuildRoutineInfo(MonomorphizationEntry entry)
    {
        RoutineInfo generic = entry.GenericMethod;
        Dictionary<string, TypeInfo> subs = entry.TypeSubstitutions;

        var resolvedParams = generic.Parameters
            .Select(selector: p =>
            {
                TypeInfo resolved = ResolveSubstitutedType(type: p.Type, subs: subs);
                return p.WithSubstitutedType(newType: resolved);
            })
            .ToList();

        TypeInfo? resolvedReturnType = generic.ReturnType != null
            ? ResolveSubstitutedType(type: generic.ReturnType, subs: subs)
            : null;

        return new RoutineInfo(name: generic.Name)
        {
            Kind = generic.Kind,
            OwnerType = entry.ResolvedOwnerType,
            Parameters = resolvedParams,
            ReturnType = resolvedReturnType,
            IsFailable = generic.IsFailable,
            DeclaredModification = generic.DeclaredModification,
            ModificationCategory = generic.ModificationCategory,
            Visibility = generic.Visibility,
            Location = generic.Location,
            Module = generic.Module,
            Annotations = generic.Annotations,
            CallingConvention = generic.CallingConvention,
            IsVariadic = generic.IsVariadic,
            IsDangerous = generic.IsDangerous,
            Storage = generic.Storage,
            AsyncStatus = generic.AsyncStatus,
            OriginalName = generic.OriginalName
        };
    }

    /// <summary>
    /// Resolves a type by applying generic substitutions.
    /// Moved from <c>LLVMCodeGenerator.ResolveSubstitutedType</c>.
    /// </summary>
    public TypeInfo ResolveSubstitutedType(TypeInfo type, Dictionary<string, TypeInfo> subs)
    {
        if (subs.TryGetValue(key: type.Name, value: out TypeInfo? sub))
            return sub;

        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            bool anySubstituted = false;
            var substitutedArgs = new List<TypeInfo>();
            foreach (TypeInfo arg in type.TypeArguments)
            {
                TypeInfo resolved = ResolveSubstitutedType(type: arg, subs: subs);
                substitutedArgs.Add(item: resolved);
                if (!ReferenceEquals(objA: resolved, objB: arg))
                    anySubstituted = true;
            }
            if (anySubstituted)
            {
                TypeInfo? genericBase = GetGenericBase(type: type);
                if (genericBase != null)
                    return _registry.GetOrCreateResolution(genericDef: genericBase,
                        typeArguments: substitutedArgs);
            }
        }

        if (type is { IsGenericDefinition: true, GenericParameters: not null } &&
            type.TypeArguments == null)
        {
            var typeArgs = type.GenericParameters
                               .Select(selector: gp =>
                                    subs.TryGetValue(key: gp, value: out TypeInfo? s)
                                        ? s
                                        : _registry.LookupType(name: gp))
                               .Where(predicate: t => t != null)
                               .ToList();
            if (typeArgs.Count == type.GenericParameters.Count)
                return _registry.GetOrCreateResolution(genericDef: type, typeArguments: typeArgs!);
        }

        return type;
    }

    private static TypeInfo? GetGenericBase(TypeInfo type) =>
        LLVMCodeGenerator.GetGenericBaseStatic(type: type);

    private static string? GetGenericBaseName(TypeInfo type) =>
        LLVMCodeGenerator.GetGenericBaseNameStatic(type: type);
}
