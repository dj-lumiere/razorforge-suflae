using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Types;

namespace Builder.Verification;

public sealed partial class SemanticVerifier
{
    /// <summary>
    /// RF-S640. A shared-buffer value (Text, Bytes, Integer, Real, and user types in the same shape) keeps
    /// its refcount in a <c>Hijacked[FrozenController]</c> field and releases one count in <c>destroy</c>.
    /// Putting an existing value's controller (<c>r.ctrl</c>) into a new value (a construction argument or
    /// a field store) makes a second owner of that count, so the routine must take it with
    /// <c>r.ctrl.as_entity().hold()</c> first, the way every <c>assign()</c> of those types does. Without
    /// the hold both values release the same count and the buffer is freed twice. A fresh controller
    /// (<c>fresh_frozen_controller()</c>, a local that holds one) is not a field read and is never flagged.
    /// </summary>
    private void CheckControllerRewraps(RoutineDeclaration routine)
    {
        if (_registry.Language != Language.RazorForge)
        {
            return;
        }

        var held = new HashSet<string>(comparer: StringComparer.Ordinal);
        var rewraps = new List<MemberExpression>();
        AstWalker.Walk(root: routine.Body,
            visit: node =>
            {
                switch (node)
                {
                    // `<path>.as_entity().hold()` takes a count on <path>.
                    case CallExpression
                    {
                        Callee: MemberExpression
                        {
                            MemberName: "hold",
                            Object: CallExpression
                            {
                                Callee: MemberExpression { MemberName: "as_entity", Object: var heldPath }
                            }
                        }
                    }:
                        if (PathText(expr: heldPath) is { } heldText)
                        {
                            held.Add(item: heldText);
                        }

                        break;
                    case CallExpression { ConstructedType: not null } construction:
                        foreach (Expression arg in construction.Arguments)
                        {
                            CollectControllerRead(value: arg is NamedArgumentExpression na
                                    ? na.Value
                                    : arg,
                                rewraps: rewraps);
                        }

                        break;
                    case CreatorExpression creator:
                        foreach ((string _, Expression value) in creator.MemberVariables)
                        {
                            CollectControllerRead(value: value, rewraps: rewraps);
                        }

                        break;
                    case AssignmentStatement { Target: MemberExpression } store:
                        CollectControllerRead(value: store.Value, rewraps: rewraps);
                        break;
                }
            });

        foreach (MemberExpression read in rewraps)
        {
            string path = PathText(expr: read)!;
            if (held.Contains(item: path))
            {
                continue;
            }

            ReportError(code: SemanticDiagnosticCode.ControllerRewrapWithoutHold,
                message:
                $"You are putting '{path}', the refcount of a buffer that '{PathText(expr: read.Object)}' " +
                "still owns, into a new value without holding it, so both values release the same count " +
                $"and the buffer is freed twice. Call '{path}.as_entity().hold()' first (as an 'assign()' " +
                "does), or build the new value through 'assign()'.",
                location: read.Location);
        }
    }

    /// <summary>Records <paramref name="value"/> when it reads a <c>Hijacked[FrozenController]</c> field.</summary>
    private static void CollectControllerRead(Expression value, List<MemberExpression> rewraps)
    {
        if (value is MemberExpression { ResolvedType: { } type } read && IsFrozenControllerHandle(type: type) &&
            PathText(expr: read) != null)
        {
            rewraps.Add(item: read);
        }
    }

    private static bool IsFrozenControllerHandle(TypeSymbol type)
    {
        return type is RecordTypeSymbol
        {
            GenericDefinition.Name: "Hijacked", TypeArguments: [{ Name: "FrozenController" }]
        };
    }

    /// <summary>The dotted text of a plain variable/field path (<c>me.ctrl</c>, <c>r.ctrl</c>), else null.</summary>
    private static string? PathText(Expression expr)
    {
        return expr switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression m when PathText(expr: m.Object) is { } owner => $"{owner}.{m.MemberName}",
            _ => null
        };
    }
}
