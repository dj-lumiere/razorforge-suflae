namespace SemanticAnalysis.Symbols;

using Enums;
using SyntaxTree;
using TypeSymbol = Types.TypeInfo;

/// <summary>
/// Information about a routine (standalone routine, member routine, creator).
/// </summary>
public sealed class RoutineInfo
{
    /// <summary>The name of the routine (without type prefix).</summary>
    public string Name { get; }

    /// <summary>Base name for registry lookup (e.g., "Circle.draw", "Math.abs").</summary>
    public string BaseName
    {
        get
        {
            if (OwnerType != null)
            {
                return $"{OwnerType.Name}.{Name}";
            }

            return string.IsNullOrEmpty(value: Module)
                ? Name
                : $"{Module}.{Name}";
        }
    }

    /// <summary>
    /// Rich signature name for display and identity.
    /// Member: "Module.OwnerType[Generics].Name(ParamTypes) -> ReturnType".
    /// Standalone: "Module.Name[Generics](ParamTypes) -> ReturnType".
    /// </summary>
    public string FullName
    {
        get
        {
            string prefix;
            if (OwnerType != null)
            {
                string ownerName = OwnerType.GenericParameters is { Count: > 0 }
                    ? $"{OwnerType.Name}[{string.Join(separator: ", ", values: OwnerType.GenericParameters)}]"
                    : OwnerType.Name;
                prefix = string.IsNullOrEmpty(value: OwnerType.Module)
                    ? $"{ownerName}.{Name}"
                    : $"{OwnerType.Module}.{ownerName}.{Name}";
            }
            else
            {
                string routineName = GenericParameters is { Count: > 0 }
                    ? $"{Name}[{string.Join(separator: ", ", values: GenericParameters)}]"
                    : Name;
                prefix = string.IsNullOrEmpty(value: Module)
                    ? routineName
                    : $"{Module}.{routineName}";
            }

            string paramPart =
                $"({string.Join(separator: ", ", values: Parameters.Select(selector: p => p.Type.Name))})";

            return ReturnType != null
                ? $"{prefix}{paramPart} -> {ReturnType.Name}"
                : $"{prefix}{paramPart}";
        }
    }

    /// <summary>
    /// Stable key for registry lookup: "BaseName#Param1,Param2".
    /// For zero-parameter routines, equals BaseName.
    /// </summary>
    public string RegistryKey
    {
        get
        {
            string baseName = BaseName;
            if (Parameters.Count == 0) return baseName;

            string paramTypes = string.Join(separator: ",",
                values: Parameters.Select(selector: p => p.Type.Name));
            return $"{baseName}#{paramTypes}";
        }
    }

    /// <summary>The module-qualified name (e.g., "Core/S8.$add", "IO/Console.show").</summary>
    public string QualifiedName
    {
        get
        {
            if (OwnerType != null)
            {
                // Member routine: Module/OwnerType.routine (e.g., "Core/S8.$add")
                return $"{OwnerType.FullName}.{Name}";
            }

            // Standalone: Module.Name
            return BaseName;
        }
    }

    /// <summary>The kind of routine.</summary>
    public RoutineKind Kind { get; init; } = RoutineKind.Function;

    /// <summary>The type that owns this routine (for member routines and extension routines).</summary>
    public TypeSymbol? OwnerType { get; init; }

    /// <summary>Parameters of this routine.</summary>
    public IReadOnlyList<ParameterInfo> Parameters { get; init; } = [];

    /// <summary>Return type. Null means "not yet inferred" (transient during analysis). After body analysis, always Blank or a concrete type.</summary>
    public TypeSymbol? ReturnType { get; set; }

    /// <summary>Whether this routine can fail (has ! suffix).</summary>
    public bool IsFailable { get; init; }

    /// <summary>Whether this routine contains throw statements.</summary>
    public bool HasThrow { get; set; }

    /// <summary>Whether this routine contains absent statements.</summary>
    public bool HasAbsent { get; set; }

    /// <summary>Whether this routine calls other failable routines (propagated failability).</summary>
    public bool HasFailableCalls { get; set; }

    /// <summary>The declared modification category for this routine (from source annotation).</summary>
    public ModificationCategory DeclaredModification { get; init; } =
        ModificationCategory.Migratable;

    /// <summary>
    /// The inferred/final modification category for this routine.
    /// Initially set to declared value, then updated by modification inference.
    /// </summary>
    public ModificationCategory ModificationCategory { get; set; } =
        ModificationCategory.Migratable;

    /// <summary>Generic type parameters, if any.</summary>
    public IReadOnlyList<string>? GenericParameters { get; init; }

    /// <summary>Generic constraints on type parameters.</summary>
    public IReadOnlyList<GenericConstraintDeclaration>? GenericConstraints { get; init; }

    /// <summary>Whether this is a generic routine definition.</summary>
    public bool IsGenericDefinition => GenericParameters is { Count: > 0 };

    /// <summary>For resolved generics, the type arguments used.</summary>
    public IReadOnlyList<TypeSymbol>? TypeArguments { get; init; }

    /// <summary>Visibility modifier.</summary>
    public VisibilityModifier Visibility { get; init; } = VisibilityModifier.Open;

    /// <summary>Source location where this routine is defined.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>The module this routine belongs to.</summary>
    public string? Module { get; init; }

    /// <summary>Module path segments (e.g., ["Core", "Memory", "Wrapper"]).</summary>
    public IReadOnlyList<string>? ModulePath { get; init; }

    /// <summary>Annotations on this routine (e.g., @readonly, @inline).</summary>
    public IReadOnlyList<string> Annotations { get; init; } = [];

    /// <summary>Whether this routine is marked @readonly (can be called through Viewed/Inspected).</summary>
    public bool IsReadOnly => Annotations.Contains(value: "readonly");

    /// <summary>
    /// For external("llvm") routines, the LLVM IR template from @llvm_ir annotation.
    /// Extracted from annotations at access time; null if no @llvm_ir annotation.
    /// </summary>
    public string? LlvmIrTemplate
    {
        get
        {
            foreach (string annotation in Annotations)
            {
                if (!annotation.StartsWith(value: "llvm_ir("))
                {
                    continue;
                }

                // Extract template: llvm_ir("template") or llvm_ir(template)
                int start = annotation.IndexOf(value: '"') + 1;
                int end = annotation.LastIndexOf(value: '"');
                if (start > 0 && end > start)
                {
                    return annotation[start..end];
                }

                // Unquoted: strip llvm_ir( prefix and ) suffix
                ReadOnlySpan<char> content = annotation.AsSpan()["llvm_ir(".Length..];
                if (content.Length > 0 && content[^1] == ')')
                {
                    content = content[..^1];
                }

                return content.ToString();
            }

            return null;
        }
    }

    /// <summary>For external routines, the calling convention.</summary>
    public string? CallingConvention { get; init; }

    /// <summary>For external routines, whether it's variadic.</summary>
    public bool IsVariadic { get; init; }

    /// <summary>Whether this routine is marked dangerous (requires danger! block to call).</summary>
    public bool IsDangerous { get; init; }

    /// <summary>Storage class: None (instance/module-level), Common (type-level static).</summary>
    public StorageClass Storage { get; init; } = StorageClass.None;

    /// <summary>Whether this routine is a common (static) routine.</summary>
    public bool IsCommon => Storage == StorageClass.Common;

    /// <summary>Whether this routine was auto-generated (e.g., derived comparison operators).</summary>
    public bool IsSynthesized { get; init; }

    /// <summary>The suspended or threaded status of this routine (None, Suspended, Threaded).</summary>
    public AsyncStatus AsyncStatus { get; init; } = AsyncStatus.None;

    /// <summary>Whether this routine is a suspended routine.</summary>
    public bool IsSuspended => AsyncStatus == AsyncStatus.Suspended;

    /// <summary>Whether this routine is a threaded (OS-thread) routine.</summary>
    public bool IsThreaded => AsyncStatus == AsyncStatus.Threaded;

    /// <summary>Whether this routine is any kind of suspended or threaded routine.</summary>
    public bool IsAsync => AsyncStatus != AsyncStatus.None;

    /// <summary>
    /// For generic definitions, the original generic routine this was resolved from.
    /// </summary>
    public RoutineInfo? GenericDefinition { get; init; }

    /// <summary>
    /// For generated error-handling variants (try_, check_, lookup_), the original routine name
    /// they were generated from (e.g., "$next" for "try_next", "parse" for "try_parse").
    /// </summary>
    public string? OriginalName { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RoutineInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the routine.</param>
    public RoutineInfo(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Creates a resolved version of this generic routine with the given type arguments.
    /// </summary>
    /// <param name="typeArguments">The type arguments to substitute for generic parameters.</param>
    /// <returns>A new <see cref="RoutineInfo"/> with types substituted.</returns>
    /// <exception cref="InvalidOperationException">Thrown if this is not a generic definition.</exception>
    /// <exception cref="ArgumentException">Thrown if the number of type arguments doesn't match.</exception>
    public RoutineInfo CreateInstance(IReadOnlyList<TypeSymbol> typeArguments)
    {
        if (!IsGenericDefinition)
        {
            throw new InvalidOperationException(
                message: $"Routine '{Name}' is not a generic definition.");
        }

        if (typeArguments.Count != GenericParameters!.Count)
        {
            throw new ArgumentException(
                message:
                $"Expected {GenericParameters.Count} type arguments, got {typeArguments.Count}.");
        }

        // Create type parameter substitution map
        var substitution = new Dictionary<string, TypeSymbol>();
        for (int i = 0; i < GenericParameters.Count; i++)
        {
            substitution[key: GenericParameters[index: i]] = typeArguments[index: i];
        }

        // Substitute types in parameters
        var substitutedParams = Parameters
                               .Select(selector: p =>
                                    SubstituteParameterType(param: p, substitution: substitution))
                               .ToList();

        // Substitute return type
        TypeSymbol? substitutedReturnType = ReturnType != null
            ? SubstituteType(type: ReturnType, substitution: substitution)
            : null;

        return new RoutineInfo(name: Name)
        {
            Kind = Kind,
            OwnerType = OwnerType,
            Parameters = substitutedParams,
            ReturnType = substitutedReturnType,
            IsFailable = IsFailable,
            DeclaredModification = DeclaredModification,
            ModificationCategory = ModificationCategory,
            TypeArguments = typeArguments,
            GenericDefinition = this,
            Visibility = Visibility,
            Location = Location,
            Module = Module,
            ModulePath = ModulePath,
            Annotations = Annotations,
            CallingConvention = CallingConvention,
            IsVariadic = IsVariadic,
            IsDangerous = IsDangerous,
            IsSynthesized = IsSynthesized,
            Storage = Storage,
            AsyncStatus = AsyncStatus
        };
    }

    /// <summary>
    /// Substitutes the type in a parameter for generic resolution.
    /// </summary>
    /// <param name="param">The parameter to substitute.</param>
    /// <param name="substitution">The type parameter substitution map.</param>
    /// <returns>A new <see cref="ParameterInfo"/> with the substituted type.</returns>
    internal static ParameterInfo SubstituteParameterType(ParameterInfo param,
        Dictionary<string, TypeSymbol> substitution)
    {
        TypeSymbol substitutedType = SubstituteType(type: param.Type, substitution: substitution);
        return param.WithSubstitutedType(newType: substitutedType);
    }

    /// <summary>
    /// Recursively substitutes type parameters in a type.
    /// </summary>
    /// <param name="type">The type to substitute.</param>
    /// <param name="substitution">The type parameter substitution map.</param>
    /// <returns>The substituted type, or the original if no substitution applies.</returns>
    internal static TypeSymbol SubstituteType(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitution)
    {
        if (substitution.TryGetValue(key: type.Name, value: out TypeSymbol? substituted))
        {
            return substituted;
        }

        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            var newArgs = type.TypeArguments
                              .Select(selector: arg =>
                                   SubstituteType(type: arg, substitution: substitution))
                              .ToList();

            // Use GenericDefinition to create the new resolution (not the resolution itself)
            if (type is Types.EntityTypeInfo { GenericDefinition: not null } entityType)
            {
                return entityType.GenericDefinition.CreateInstance(typeArguments: newArgs);
            }

            if (type is Types.RecordTypeInfo { GenericDefinition: not null } recordType)
            {
                return recordType.GenericDefinition.CreateInstance(typeArguments: newArgs);
            }

            if (type is Types.ProtocolTypeInfo { GenericDefinition: not null } protocolType)
            {
                return protocolType.GenericDefinition.CreateInstance(typeArguments: newArgs);
            }

            return type.CreateInstance(typeArguments: newArgs);
        }

        return type;
    }
}
