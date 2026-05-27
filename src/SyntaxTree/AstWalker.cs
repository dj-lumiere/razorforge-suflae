using System;
using System.Collections.Generic;

namespace SyntaxTree;

/// <summary>
/// Depth-first AST traversal utility. Replaces reflection-based walkers — every concrete
/// Statement, Expression, Declaration, Pattern, and auxiliary node has an explicit case
/// in <see cref="EnumerateChildren"/>. Adding a new AST node type without updating
/// EnumerateChildren means children of that node are silently skipped: keep this file
/// in sync with the records in Statements.cs, Expressions.cs, Expressions.Async.cs,
/// and Declarations.cs.
/// </summary>
public static class AstWalker
{
    /// <summary>
    /// Pre-order DFS: invokes <paramref name="visit"/> on <paramref name="root"/>, then on
    /// every descendant. Null roots are ignored. Walking is unconditional; if a visitor
    /// needs to stop recursion into a subtree, it should track state externally.
    /// </summary>
    public static void Walk(object? root, Action<object> visit)
    {
        if (root == null) return;
        visit(obj: root);
        foreach (object child in EnumerateChildren(node: root))
        {
            Walk(root: child, visit: visit);
        }
    }

    /// <summary>
    /// Convenience overload that only invokes <paramref name="visit"/> on
    /// <see cref="Expression"/> nodes encountered during the walk.
    /// </summary>
    public static void WalkExpressions(object? root, Action<Expression> visit)
    {
        Walk(root: root,
            visit: n =>
            {
                if (n is Expression e) visit(obj: e);
            });
    }

    /// <summary>
    /// Yields the immediate AST children of <paramref name="node"/>. Children include any
    /// Statement, Expression, Declaration, Pattern, or auxiliary record (WhenClause,
    /// Parameter, DestructuringBinding, etc.) reachable from one of <paramref name="node"/>'s
    /// constructor parameters. Non-AST data (strings, enums, primitives, type-system info)
    /// is not yielded.
    /// </summary>
    public static IEnumerable<object> EnumerateChildren(object node)
    {
        switch (node)
        {
            // -------- Program --------
            case Program p:
                foreach (ISyntaxTreeNode d in p.Declarations) yield return d;
                break;

            // -------- Statements --------
            case ExpressionStatement s:
                yield return s.Expression;
                break;
            case DeclarationStatement s:
                yield return s.Declaration;
                break;
            case AssignmentStatement s:
                yield return s.Target;
                yield return s.Value;
                break;
            case DestructuringStatement s:
                yield return s.Pattern;
                yield return s.Initializer;
                break;
            case ReturnStatement s:
                if (s.Value != null) yield return s.Value;
                break;
            case BecomesStatement s:
                yield return s.Value;
                break;
            case ThrowStatement s:
                yield return s.Error;
                break;
            case VariantReturnStatement s:
                if (s.Value != null) yield return s.Value;
                break;
            case DiscardStatement s:
                yield return s.Expression;
                break;
            case IfStatement s:
                yield return s.Condition;
                yield return s.ThenStatement;
                if (s.ElseStatement != null) yield return s.ElseStatement;
                break;
            case WhileStatement s:
                yield return s.Condition;
                yield return s.Body;
                if (s.ElseBranch != null) yield return s.ElseBranch;
                break;
            case LoopStatement s:
                yield return s.Body;
                break;
            case ForStatement s:
                if (s.VariablePattern != null) yield return s.VariablePattern;
                yield return s.Iterable;
                yield return s.Body;
                if (s.ElseBranch != null) yield return s.ElseBranch;
                break;
            case BlockStatement s:
                foreach (Statement child in s.Statements) yield return child;
                break;
            case WhenStatement s:
                yield return s.Expression;
                foreach (WhenClause c in s.Clauses) yield return c;
                break;
            case DangerStatement s:
                yield return s.Body;
                break;
            case UsingStatement s:
                yield return s.Resource;
                yield return s.Body;
                break;
            case AbsentStatement:
            case PassStatement:
            case BreakStatement:
            case ContinueStatement:
                break;

            // -------- Expressions --------
            case InsertedTextExpression e:
                foreach (InsertedTextPart part in e.Parts) yield return part;
                break;
            case ListLiteralExpression e:
                foreach (Expression el in e.Elements) yield return el;
                if (e.ElementType != null) yield return e.ElementType;
                break;
            case SetLiteralExpression e:
                foreach (Expression el in e.Elements) yield return el;
                if (e.ElementType != null) yield return e.ElementType;
                break;
            case DictLiteralExpression e:
                foreach ((Expression Key, Expression Value) pair in e.Pairs)
                {
                    yield return pair.Key;
                    yield return pair.Value;
                }
                if (e.KeyType != null) yield return e.KeyType;
                if (e.ValueType != null) yield return e.ValueType;
                break;
            case TupleLiteralExpression e:
                foreach (Expression el in e.Elements) yield return el;
                break;
            case CompoundAssignmentExpression e:
                yield return e.Target;
                yield return e.Value;
                break;
            case BinaryExpression e:
                yield return e.Left;
                yield return e.Right;
                break;
            case UnaryExpression e:
                yield return e.Operand;
                break;
            case CallExpression e:
                yield return e.Callee;
                foreach (Expression arg in e.Arguments) yield return arg;
                if (e.TypeArguments != null)
                    foreach (TypeExpression t in e.TypeArguments) yield return t;
                break;
            case NamedArgumentExpression e:
                yield return e.Value;
                break;
            case DictEntryLiteralExpression e:
                yield return e.Key;
                yield return e.Value;
                break;
            case CreatorExpression e:
                if (e.TypeArguments != null)
                    foreach (TypeExpression t in e.TypeArguments) yield return t;
                foreach ((string Name, Expression Value) mv in e.MemberVariables)
                    yield return mv.Value;
                break;
            case WithExpression e:
                yield return e.Base;
                foreach ((List<string>? Path, Expression? Index, Expression Value) u in e.Updates)
                {
                    if (u.Index != null) yield return u.Index;
                    yield return u.Value;
                }
                break;
            case MemberExpression e:
                yield return e.Object;
                break;
            case OptionalMemberExpression e:
                yield return e.Object;
                break;
            case IndexExpression e:
                yield return e.Object;
                yield return e.Index;
                break;
            case ConditionalExpression e:
                yield return e.Condition;
                yield return e.TrueExpression;
                yield return e.FalseExpression;
                break;
            case BlockExpression e:
                yield return e.Value;
                break;
            case ChainedComparisonExpression e:
                foreach (Expression op in e.Operands) yield return op;
                break;
            case RangeExpression e:
                yield return e.Start;
                yield return e.End;
                if (e.Step != null) yield return e.Step;
                break;
            case LambdaExpression e:
                foreach (Parameter p in e.Parameters) yield return p;
                yield return e.Body;
                break;
            case TypeExpression e:
                if (e.GenericArguments != null)
                    foreach (TypeExpression t in e.GenericArguments) yield return t;
                break;
            case TypeConversionExpression e:
                yield return e.Expression;
                break;
            case GenericMethodCallExpression e:
                yield return e.Object;
                foreach (TypeExpression t in e.TypeArguments) yield return t;
                foreach (Expression arg in e.Arguments) yield return arg;
                break;
            case GenericMemberExpression e:
                yield return e.Object;
                foreach (TypeExpression t in e.TypeArguments) yield return t;
                break;
            case TypeIdExpression e:
                yield return e.Type;
                break;
            case CarrierPayloadExpression e:
                yield return e.Carrier;
                yield return e.ConcreteType;
                break;
            case IsPatternExpression e:
                yield return e.Expression;
                yield return e.Pattern;
                break;
            case FlagsTestExpression e:
                yield return e.Subject;
                break;
            case WhenExpression e:
                if (e.Expression != null) yield return e.Expression;
                foreach (WhenClause c in e.Clauses) yield return c;
                break;
            case StealExpression e:
                yield return e.Operand;
                break;
            case WaitforExpression e:
                yield return e.Operand;
                if (e.Timeout != null) yield return e.Timeout;
                break;
            case DependentWaitforExpression e:
                foreach (TaskDependency dep in e.Dependencies) yield return dep;
                yield return e.Operand;
                if (e.Timeout != null) yield return e.Timeout;
                break;
            case BackIndexExpression e:
                yield return e.Operand;
                break;
            case LiteralExpression:
            case IdentifierExpression:
            case UninitExpression:
                break;

            // -------- Patterns --------
            case TypePattern p:
                yield return p.Type;
                if (p.Bindings != null)
                    foreach (DestructuringBinding b in p.Bindings) yield return b;
                break;
            case NegatedTypePattern p:
                yield return p.Type;
                break;
            case ExpressionPattern p:
                yield return p.Expression;
                break;
            case ComparisonPattern p:
                yield return p.Value;
                break;
            case VariantPattern p:
                if (p.Bindings != null)
                    foreach (DestructuringBinding b in p.Bindings) yield return b;
                break;
            case GuardPattern p:
                yield return p.InnerPattern;
                yield return p.Guard;
                break;
            case CrashablePattern p:
                if (p.ErrorType != null) yield return p.ErrorType;
                break;
            case DestructuringPattern p:
                foreach (DestructuringBinding b in p.Bindings) yield return b;
                break;
            case TypeDestructuringPattern p:
                yield return p.Type;
                foreach (DestructuringBinding b in p.Bindings) yield return b;
                break;
            case LiteralPattern:
            case IdentifierPattern:
            case FlagsPattern:
            case WildcardPattern:
            case NonePattern:
            case ElsePattern:
                break;

            // -------- Declarations --------
            case VariableDeclaration d:
                if (d.Type != null) yield return d.Type;
                if (d.Initializer != null) yield return d.Initializer;
                break;
            case RoutineDeclaration d:
                foreach (Parameter p in d.Parameters) yield return p;
                if (d.ReturnType != null) yield return d.ReturnType;
                yield return d.Body;
                break;
            case EntityDeclaration d:
                foreach (TypeExpression t in d.Protocols) yield return t;
                foreach (Declaration m in d.Members) yield return m;
                break;
            case RecordDeclaration d:
                foreach (TypeExpression t in d.Protocols) yield return t;
                foreach (Declaration m in d.Members) yield return m;
                break;
            case ChoiceDeclaration d:
                foreach (ChoiceCase c in d.Cases) yield return c;
                foreach (RoutineDeclaration m in d.Methods) yield return m;
                break;
            case CrashableDeclaration d:
                foreach (Declaration m in d.Members) yield return m;
                break;
            case VariantDeclaration d:
                foreach (VariantMember m in d.Members) yield return m;
                break;
            case ProtocolDeclaration d:
                foreach (TypeExpression t in d.ParentProtocols) yield return t;
                foreach (RoutineSignature m in d.Methods) yield return m;
                break;
            case PresetDeclaration d:
                yield return d.Type;
                yield return d.Value;
                break;
            case ExternalDeclaration d:
                foreach (Parameter p in d.Parameters) yield return p;
                if (d.ReturnType != null) yield return d.ReturnType;
                break;
            case ExternalBlockDeclaration d:
                foreach (Declaration child in d.Declarations) yield return child;
                break;
            case PassDeclaration:
            case FlagsDeclaration:
            case ModuleDeclaration:
            case ImportDeclaration:
            case DefineDeclaration:
                break;

            // -------- Auxiliary records --------
            case WhenClause c:
                yield return c.Pattern;
                yield return c.Body;
                break;
            case DestructuringBinding b:
                if (b.NestedPattern != null) yield return b.NestedPattern;
                break;
            case Parameter p:
                if (p.Type != null) yield return p.Type;
                if (p.DefaultValue != null) yield return p.DefaultValue;
                break;
            case ChoiceCase c:
                if (c.Value != null) yield return c.Value;
                break;
            case VariantMember m:
                yield return m.Type;
                break;
            case RoutineSignature r:
                foreach (Parameter p in r.Parameters) yield return p;
                if (r.ReturnType != null) yield return r.ReturnType;
                break;
            case TaskDependency d:
                yield return d.DependencyExpr;
                break;
            case ExpressionPart ep:
                yield return ep.Expression;
                break;
            case TextPart:
                break;
        }
    }
}
