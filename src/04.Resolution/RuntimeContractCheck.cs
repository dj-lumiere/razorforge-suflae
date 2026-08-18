using System.Collections.Generic;
using System.Linq;
using SyntaxTree;
using TypeModel.Types;

namespace Compiler.Resolution;

/// <summary>
/// The <c>validate-stdlib</c> resolution check for <see cref="RuntimeContract"/> (Design 1 step 2 of
/// the compiler↔stdlib name-contract work). Turns a SILENT rename miscompile into a LOUD build
/// failure: for every name the compiler hard-codes against the stdlib, assert it still resolves.
///
/// <para>A rename like <c>extract</c>/<c>inject</c> → <c>peek</c>/<c>poke</c> (commit 1480acd) used to
/// compile clean and break at runtime, because the compiler looks these up by literal. With this
/// check wired into the CI-gated <c>validate-stdlib</c> verb, renaming a contract routine/type/field
/// without updating <see cref="RuntimeContract"/> fails immediately, naming the exact broken contract.</para>
///
/// <para>Scope mirrors <see cref="RuntimeContract"/>: it checks the declared-in-stdlib routine names
/// (<see cref="RuntimeContract.StdlibRoutineContracts"/>), the wrapper TYPE names
/// (<see cref="RuntimeContract.WrapperTypes"/>), and the <c>Maybe</c> carrier fields. It deliberately
/// does NOT check compiler-generated / intrinsic names (<c>try_emit</c>, <c>refer</c>/<c>control</c>,
/// BuilderService/<c>data_size</c>) or the native <c>rf_*</c> externs (link-checked C-ABI).</para>
/// </summary>
public static class RuntimeContractCheck
{
    /// <summary>The record type that carries the <c>present</c>/<c>value</c> optional fields.</summary>
    private const string CarrierTypeName = "Maybe";

    /// <summary>Runs the contract-resolution check against a fully-loaded stdlib registry. Returns a
    /// human-readable description for each broken contract; an empty list means every contract holds.</summary>
    public static List<string> Check(TypeRegistry registry)
    {
        var errors = new List<string>();

        // 1. Every bare routine name declared anywhere in the stdlib ASTs. Using the declaration
        //    ground truth (not the liveness-filtered GetAllRoutines) so the check is independent of
        //    which routines a user program happens to reach — validate-stdlib has no user program.
        var declaredRoutines = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach ((Program program, _, _) in registry.StdlibPrograms)
        {
            AstWalker.Walk(root: program, visit: node =>
            {
                switch (node)
                {
                    case RoutineDeclaration d: declaredRoutines.Add(item: BareMemberRoutineName(name: d.Name)); break;
                    case RoutineSignature s: declaredRoutines.Add(item: BareMemberRoutineName(name: s.Name)); break;
                }
            });
        }

        // 2. Routine-name contracts must each resolve to a declared stdlib routine.
        foreach (string name in RuntimeContract.StdlibRoutineContracts)
        {
            if (!declaredRoutines.Contains(item: name))
            {
                errors.Add(item: $"routine contract '{name}' resolves to NO declared stdlib routine "
                                 + "(renamed in stdlib without updating RuntimeContract?)");
            }
        }

        // 3. Wrapper / marker-protocol TYPE-name contracts must each resolve to a registered type.
        foreach (string typeName in RuntimeContract.WrapperTypes.Concat(second: RuntimeContract.StdlibTypeContracts))
        {
            if (registry.LookupType(name: typeName) is null)
            {
                errors.Add(item: $"type contract '{typeName}' resolves to NO registered type");
            }
        }

        // 4. Carrier-field contracts must exist as member variables on the Maybe record.
        TypeInfo? carrier = registry.LookupType(name: CarrierTypeName);
        if (carrier is null)
        {
            errors.Add(item: $"carrier type '{CarrierTypeName}' is not registered "
                             + "(cannot verify the present/value field contracts)");
        }
        else
        {
            HashSet<string> fields = MemberVariableNames(type: carrier);
            foreach (string field in new[] { RuntimeContract.Carrier.PresentField, RuntimeContract.Carrier.ValueField })
            {
                if (!fields.Contains(item: field))
                {
                    errors.Add(item: $"carrier-field contract '{CarrierTypeName}.{field}' resolves to NO member variable");
                }
            }
        }

        return errors;
    }

    /// <summary>Extracts the bare memberRoutine name from a possibly owner-qualified, possibly generic
    /// declaration name: <c>Hijacked[T].peek</c> → <c>peek</c>,
    /// <c>S64.to_width[T]</c> → <c>to_width</c>, <c>make_channel</c> → <c>make_channel</c>. Mirrors the
    /// split StdlibLoader.Registration performs on the same names. The failable `!` is a structured
    /// flag, never part of the name, so it is not stripped here.</summary>
    private static string BareMemberRoutineName(string name)
    {
        int dot = name.IndexOf(value: '.');
        string memberRoutine = dot >= 0 ? name[(dot + 1)..] : name;
        return TypeInfo.StripTypeArgs(name: memberRoutine);
    }

    private static HashSet<string> MemberVariableNames(TypeInfo type)
    {
        IEnumerable<string> names = type switch
        {
            RecordTypeInfo r => r.MemberVariables.Select(selector: m => m.Name),
            EntityTypeInfo e => e.MemberVariables.Select(selector: m => m.Name),
            _ => []
        };
        return new HashSet<string>(collection: names, comparer: StringComparer.Ordinal);
    }
}
