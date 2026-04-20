using Compiler.Lexer;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using SyntaxTree;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Phase 7 pass: lift non-capturing lambdas into synthesized top-level routines after
/// verification is complete. True local captures still require closure lowering and are
/// rejected here instead of leaking into codegen.
/// </summary>
internal sealed class LambdaLiftingPass(PostprocessingContext ctx)
{
    private int _lambdaCounter;
    private readonly List<RoutineDeclaration> _liftedRoutines = [];
    private string? _currentModuleName;

    public void Run(Program program)
    {
        _currentModuleName = program.Declarations
            .OfType<ModuleDeclaration>()
            .LastOrDefault()?.Path;
        _liftedRoutines.Clear();

        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration routine:
                    program.Declarations[i] = RewriteRoutine(
                        routine,
                        scope: BuildRoutineScope(routine, includeMe: false),
                        inheritedGenericParameters: routine.GenericParameters,
                        inheritedGenericConstraints: routine.GenericConstraints,
                        includeMe: false);
                    break;

                case EntityDeclaration entity:
                    RewriteMemberList(entity.Members,
                        ownerGenericParameters: entity.GenericParameters,
                        ownerGenericConstraints: entity.GenericConstraints);
                    break;

                case RecordDeclaration record:
                    RewriteMemberList(record.Members,
                        ownerGenericParameters: record.GenericParameters,
                        ownerGenericConstraints: record.GenericConstraints);
                    break;

                case CrashableDeclaration crashable:
                    RewriteMemberList(crashable.Members,
                        ownerGenericParameters: null,
                        ownerGenericConstraints: null);
                    break;
            }
        }

        foreach (RoutineDeclaration lifted in _liftedRoutines)
        {
            program.Declarations.Add(item: lifted);
        }
    }

    private void RewriteMemberList(List<SyntaxTree.Declaration> members,
        List<string>? ownerGenericParameters,
        List<GenericConstraintDeclaration>? ownerGenericConstraints)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] is not RoutineDeclaration routine)
            {
                continue;
            }

            members[i] = RewriteRoutine(
                routine,
                scope: BuildRoutineScope(routine, includeMe: true),
                inheritedGenericParameters: MergeGenericParameters(ownerGenericParameters,
                    routine.GenericParameters),
                inheritedGenericConstraints: MergeGenericConstraints(ownerGenericConstraints,
                    routine.GenericConstraints),
                includeMe: true);
        }
    }

    private RoutineDeclaration RewriteRoutine(RoutineDeclaration routine,
        HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints,
        bool includeMe)
    {
        Statement body = RewriteStatement(
            routine.Body,
            scope: scope,
            inheritedGenericParameters: inheritedGenericParameters,
            inheritedGenericConstraints: inheritedGenericConstraints,
            includeMe: includeMe);

        return ReferenceEquals(body, routine.Body)
            ? routine
            : routine with { Body = body };
    }

    private Statement RewriteStatement(Statement statement,
        HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints,
        bool includeMe)
    {
        return statement switch
        {
            BlockStatement block => RewriteBlock(block,
                scope: scope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe),
            IfStatement ifs => ifs with
            {
                Condition = RewriteExpression(ifs.Condition,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                ThenStatement = RewriteStatement(ifs.ThenStatement,
                    scope: new HashSet<string>(scope, StringComparer.Ordinal),
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                ElseStatement = ifs.ElseStatement != null
                    ? RewriteStatement(ifs.ElseStatement,
                        scope: new HashSet<string>(scope, StringComparer.Ordinal),
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                    : null
            },
            WhileStatement whileStmt => whileStmt with
            {
                Condition = RewriteExpression(whileStmt.Condition,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                Body = RewriteStatement(whileStmt.Body,
                    scope: new HashSet<string>(scope, StringComparer.Ordinal),
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                ElseBranch = whileStmt.ElseBranch != null
                    ? RewriteStatement(whileStmt.ElseBranch,
                        scope: new HashSet<string>(scope, StringComparer.Ordinal),
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                    : null
            },
            LoopStatement loop => loop with
            {
                Body = RewriteStatement(loop.Body,
                    scope: new HashSet<string>(scope, StringComparer.Ordinal),
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            ForStatement forStmt => RewriteFor(forStmt,
                scope: scope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe),
            WhenStatement whenStmt => RewriteWhen(whenStmt,
                scope: scope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe),
            UsingStatement usingStmt => usingStmt with
            {
                Resource = RewriteExpression(usingStmt.Resource,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                Body = RewriteStatement(usingStmt.Body,
                    scope: [..scope, usingStmt.Name],
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            DangerStatement danger => danger with
            {
                Body = (BlockStatement)RewriteStatement(danger.Body,
                    scope: new HashSet<string>(scope, StringComparer.Ordinal),
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            DeclarationStatement { Declaration: VariableDeclaration variable } decl => decl with
            {
                Declaration = variable.Initializer != null
                    ? variable with
                    {
                        Initializer = RewriteExpression(variable.Initializer,
                            scope: scope,
                            inheritedGenericParameters: inheritedGenericParameters,
                            inheritedGenericConstraints: inheritedGenericConstraints,
                            includeMe: includeMe)
                    }
                    : variable
            },
            AssignmentStatement assignment => assignment with
            {
                Target = RewriteExpression(assignment.Target,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                Value = RewriteExpression(assignment.Value,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            ReturnStatement { Value: not null } ret => ret with
            {
                Value = RewriteExpression(ret.Value,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            ExpressionStatement exprStmt => exprStmt with
            {
                Expression = RewriteExpression(exprStmt.Expression,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            DiscardStatement discard => discard with
            {
                Expression = RewriteExpression(discard.Expression,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            BecomesStatement becomes => becomes with
            {
                Value = RewriteExpression(becomes.Value,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            ThrowStatement throwStmt => throwStmt with
            {
                Error = RewriteExpression(throwStmt.Error,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            VariantReturnStatement { Value: not null } variantReturn => variantReturn with
            {
                Value = RewriteExpression(variantReturn.Value,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            DestructuringStatement destructuring => destructuring with
            {
                Initializer = RewriteExpression(destructuring.Initializer,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            _ => statement
        };
    }

    private Statement RewriteBlock(BlockStatement block,
        HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints,
        bool includeMe)
    {
        var blockScope = new HashSet<string>(scope, StringComparer.Ordinal);
        var statements = new List<Statement>(capacity: block.Statements.Count);

        foreach (Statement statement in block.Statements)
        {
            Statement rewritten = RewriteStatement(statement,
                scope: blockScope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe);
            statements.Add(item: rewritten);
            AddStatementBindings(blockScope, rewritten);
        }

        return block with { Statements = statements };
    }

    private Statement RewriteFor(ForStatement forStmt,
        HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints,
        bool includeMe)
    {
        var bodyScope = new HashSet<string>(scope, StringComparer.Ordinal);
        if (forStmt.Variable != null)
        {
            bodyScope.Add(item: forStmt.Variable);
        }

        foreach (string binding in GetPatternBindings(forStmt.VariablePattern))
        {
            bodyScope.Add(item: binding);
        }

        return forStmt with
        {
            Iterable = RewriteExpression(forStmt.Iterable,
                scope: scope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe),
            Body = RewriteStatement(forStmt.Body,
                scope: bodyScope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe),
            ElseBranch = forStmt.ElseBranch != null
                ? RewriteStatement(forStmt.ElseBranch,
                    scope: new HashSet<string>(scope, StringComparer.Ordinal),
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
                : null
        };
    }

    private Statement RewriteWhen(WhenStatement whenStmt,
        HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints,
        bool includeMe)
    {
        var clauses = new List<WhenClause>(capacity: whenStmt.Clauses.Count);

        foreach (WhenClause clause in whenStmt.Clauses)
        {
            var clauseScope = new HashSet<string>(scope, StringComparer.Ordinal);
            foreach (string binding in GetPatternBindings(clause.Pattern))
            {
                clauseScope.Add(item: binding);
            }

            clauses.Add(item: clause with
            {
                Pattern = RewritePatternExpressions(clause.Pattern,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                Body = RewriteStatement(clause.Body,
                    scope: clauseScope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            });
        }

        return whenStmt with
        {
            Expression = RewriteExpression(whenStmt.Expression,
                scope: scope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe),
            Clauses = clauses
        };
    }

    private Expression RewriteExpression(Expression expression,
        HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints,
        bool includeMe)
    {
        switch (expression)
        {
            case LambdaExpression lambda:
                return LiftLambda(lambda,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe);

            case BinaryExpression binary:
                return CopyResolvedType(binary with
                {
                    Left = RewriteExpression(binary.Left,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Right = RewriteExpression(binary.Right,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, binary);

            case UnaryExpression unary:
                return CopyResolvedType(unary with
                {
                    Operand = RewriteExpression(unary.Operand,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, unary);

            case CallExpression call:
                return CopyResolvedType(call with
                {
                    Callee = RewriteExpression(call.Callee,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Arguments = call.Arguments
                        .Select(arg => RewriteExpression(arg,
                            scope: scope,
                            inheritedGenericParameters: inheritedGenericParameters,
                            inheritedGenericConstraints: inheritedGenericConstraints,
                            includeMe: includeMe))
                        .ToList()
                }, call);

            case MemberExpression member:
                return CopyResolvedType(member with
                {
                    Object = RewriteExpression(member.Object,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, member);

            case OptionalMemberExpression optionalMember:
                return CopyResolvedType(optionalMember with
                {
                    Object = RewriteExpression(optionalMember.Object,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, optionalMember);

            case IndexExpression index:
                return CopyResolvedType(index with
                {
                    Object = RewriteExpression(index.Object,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Index = RewriteExpression(index.Index,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, index);

            case SliceExpression slice:
                return CopyResolvedType(slice with
                {
                    Object = RewriteExpression(slice.Object,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Start = RewriteExpression(slice.Start,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    End = RewriteExpression(slice.End,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, slice);

            case ConditionalExpression conditional:
                return CopyResolvedType(conditional with
                {
                    Condition = RewriteExpression(conditional.Condition,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    TrueExpression = RewriteExpression(conditional.TrueExpression,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    FalseExpression = RewriteExpression(conditional.FalseExpression,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, conditional);

            case RangeExpression range:
                return CopyResolvedType(range with
                {
                    Start = RewriteExpression(range.Start,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    End = RewriteExpression(range.End,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Step = range.Step != null
                        ? RewriteExpression(range.Step,
                            scope: scope,
                            inheritedGenericParameters: inheritedGenericParameters,
                            inheritedGenericConstraints: inheritedGenericConstraints,
                            includeMe: includeMe)
                        : null
                }, range);

            case CreatorExpression creator:
                return CopyResolvedType(creator with
                {
                    MemberVariables = creator.MemberVariables
                        .Select(mv => (mv.Name,
                            RewriteExpression(mv.Value,
                                scope: scope,
                                inheritedGenericParameters: inheritedGenericParameters,
                                inheritedGenericConstraints: inheritedGenericConstraints,
                                includeMe: includeMe)))
                        .ToList()
                }, creator);

            case WithExpression withExpr:
                return CopyResolvedType(withExpr with
                {
                    Base = RewriteExpression(withExpr.Base,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Updates = withExpr.Updates
                        .Select(update => (
                            update.MemberVariablePath,
                            update.Index != null
                                ? RewriteExpression(update.Index,
                                    scope: scope,
                                    inheritedGenericParameters: inheritedGenericParameters,
                                    inheritedGenericConstraints: inheritedGenericConstraints,
                                    includeMe: includeMe)
                                : null,
                            RewriteExpression(update.Value,
                                scope: scope,
                                inheritedGenericParameters: inheritedGenericParameters,
                                inheritedGenericConstraints: inheritedGenericConstraints,
                                includeMe: includeMe)))
                        .ToList()
                }, withExpr);

            case GenericMethodCallExpression genericCall:
                return CopyResolvedType(genericCall with
                {
                    Object = RewriteExpression(genericCall.Object,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Arguments = genericCall.Arguments
                        .Select(arg => RewriteExpression(arg,
                            scope: scope,
                            inheritedGenericParameters: inheritedGenericParameters,
                            inheritedGenericConstraints: inheritedGenericConstraints,
                            includeMe: includeMe))
                        .ToList()
                }, genericCall);

            case GenericMemberExpression genericMember:
                return CopyResolvedType(genericMember with
                {
                    Object = RewriteExpression(genericMember.Object,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, genericMember);

            case NamedArgumentExpression namedArgument:
                return CopyResolvedType(namedArgument with
                {
                    Value = RewriteExpression(namedArgument.Value,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, namedArgument);

            case ListLiteralExpression list:
                return CopyResolvedType(list with
                {
                    Elements = list.Elements
                        .Select(element => RewriteExpression(element,
                            scope: scope,
                            inheritedGenericParameters: inheritedGenericParameters,
                            inheritedGenericConstraints: inheritedGenericConstraints,
                            includeMe: includeMe))
                        .ToList()
                }, list);

            case SetLiteralExpression set:
                return CopyResolvedType(set with
                {
                    Elements = set.Elements
                        .Select(element => RewriteExpression(element,
                            scope: scope,
                            inheritedGenericParameters: inheritedGenericParameters,
                            inheritedGenericConstraints: inheritedGenericConstraints,
                            includeMe: includeMe))
                        .ToList()
                }, set);

            case DictLiteralExpression dict:
                return CopyResolvedType(dict with
                {
                    Pairs = dict.Pairs
                        .Select(pair => (
                            RewriteExpression(pair.Key,
                                scope: scope,
                                inheritedGenericParameters: inheritedGenericParameters,
                                inheritedGenericConstraints: inheritedGenericConstraints,
                                includeMe: includeMe),
                            RewriteExpression(pair.Value,
                                scope: scope,
                                inheritedGenericParameters: inheritedGenericParameters,
                                inheritedGenericConstraints: inheritedGenericConstraints,
                                includeMe: includeMe)))
                        .ToList()
                }, dict);

            case TupleLiteralExpression tuple:
                return CopyResolvedType(tuple with
                {
                    Elements = tuple.Elements
                        .Select(element => RewriteExpression(element,
                            scope: scope,
                            inheritedGenericParameters: inheritedGenericParameters,
                            inheritedGenericConstraints: inheritedGenericConstraints,
                            includeMe: includeMe))
                        .ToList()
                }, tuple);

            case TypeConversionExpression conversion:
                return CopyResolvedType(conversion with
                {
                    Expression = RewriteExpression(conversion.Expression,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, conversion);

            case ChainedComparisonExpression chained:
                return CopyResolvedType(chained with
                {
                    Operands = chained.Operands
                        .Select(operand => RewriteExpression(operand,
                            scope: scope,
                            inheritedGenericParameters: inheritedGenericParameters,
                            inheritedGenericConstraints: inheritedGenericConstraints,
                            includeMe: includeMe))
                        .ToList()
                }, chained);

            case BlockExpression block:
                return CopyResolvedType(block with
                {
                    Value = RewriteExpression(block.Value,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, block);

            case DictEntryLiteralExpression dictEntry:
                return CopyResolvedType(dictEntry with
                {
                    Key = RewriteExpression(dictEntry.Key,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Value = RewriteExpression(dictEntry.Value,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, dictEntry);

            case IsPatternExpression isPattern:
                return CopyResolvedType(isPattern with
                {
                    Expression = RewriteExpression(isPattern.Expression,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Pattern = RewritePatternExpressions(isPattern.Pattern,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, isPattern);

            case FlagsTestExpression flagsTest:
                return CopyResolvedType(flagsTest with
                {
                    Subject = RewriteExpression(flagsTest.Subject,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, flagsTest);

            case InsertedTextExpression inserted:
                return CopyResolvedType(inserted with
                {
                    Parts = inserted.Parts
                        .Select(part => part is ExpressionPart expressionPart
                            ? expressionPart with
                            {
                                Expression = RewriteExpression(expressionPart.Expression,
                                    scope: scope,
                                    inheritedGenericParameters: inheritedGenericParameters,
                                    inheritedGenericConstraints: inheritedGenericConstraints,
                                    includeMe: includeMe)
                            }
                            : part)
                        .ToList()
                }, inserted);

            case StealExpression steal:
                return CopyResolvedType(steal with
                {
                    Operand = RewriteExpression(steal.Operand,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, steal);

            case WaitforExpression waitfor:
                return CopyResolvedType(waitfor with
                {
                    Operand = RewriteExpression(waitfor.Operand,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Timeout = waitfor.Timeout != null
                        ? RewriteExpression(waitfor.Timeout,
                            scope: scope,
                            inheritedGenericParameters: inheritedGenericParameters,
                            inheritedGenericConstraints: inheritedGenericConstraints,
                            includeMe: includeMe)
                        : null
                }, waitfor);

            case DependentWaitforExpression dependentWaitfor:
                return CopyResolvedType(dependentWaitfor with
                {
                    Operand = RewriteExpression(dependentWaitfor.Operand,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Dependencies = dependentWaitfor.Dependencies
                        .Select(dependency => dependency with
                        {
                            DependencyExpr = RewriteExpression(dependency.DependencyExpr,
                                scope: scope,
                                inheritedGenericParameters: inheritedGenericParameters,
                                inheritedGenericConstraints: inheritedGenericConstraints,
                                includeMe: includeMe)
                        })
                        .ToList(),
                    Timeout = dependentWaitfor.Timeout != null
                        ? RewriteExpression(dependentWaitfor.Timeout,
                            scope: scope,
                            inheritedGenericParameters: inheritedGenericParameters,
                            inheritedGenericConstraints: inheritedGenericConstraints,
                            includeMe: includeMe)
                        : null
                }, dependentWaitfor);

            case CarrierPayloadExpression payload:
                return CopyResolvedType(payload with
                {
                    Carrier = RewriteExpression(payload.Carrier,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, payload);

            case BackIndexExpression backIndex:
                return CopyResolvedType(backIndex with
                {
                    Operand = RewriteExpression(backIndex.Operand,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                }, backIndex);

            case WhenExpression whenExpr:
                return CopyResolvedType(whenExpr with
                {
                    Expression = whenExpr.Expression != null
                        ? RewriteExpression(whenExpr.Expression,
                            scope: scope,
                            inheritedGenericParameters: inheritedGenericParameters,
                            inheritedGenericConstraints: inheritedGenericConstraints,
                            includeMe: includeMe)
                        : null,
                    Clauses = whenExpr.Clauses
                        .Select(clause => clause with
                        {
                            Pattern = RewritePatternExpressions(clause.Pattern,
                                scope: scope,
                                inheritedGenericParameters: inheritedGenericParameters,
                                inheritedGenericConstraints: inheritedGenericConstraints,
                                includeMe: includeMe),
                            Body = RewriteStatement(clause.Body,
                                scope: [..scope, ..GetPatternBindings(clause.Pattern)],
                                inheritedGenericParameters: inheritedGenericParameters,
                                inheritedGenericConstraints: inheritedGenericConstraints,
                                includeMe: includeMe)
                        })
                        .ToList()
                }, whenExpr);

            default:
                return expression;
        }
    }

    private Expression LiftLambda(LambdaExpression lambda,
        HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints,
        bool includeMe)
    {
        HashSet<string> localCaptures = CollectLocalCaptures(lambda, scope);
        if (localCaptures.Count > 0)
        {
            string captures = string.Join(", ", localCaptures.OrderBy(name => name));
            throw new InvalidOperationException(
                $"Capturing lambda requires closure lowering and cannot yet be lowered in Phase 7. Captures: {captures}");
        }

        if (includeMe && ContainsIdentifier(lambda.Body, "me"))
        {
            throw new InvalidOperationException(
                "Lambda captures 'me' and requires closure lowering before codegen.");
        }

        if (lambda.ResolvedType is not RoutineTypeInfo routineType)
        {
            throw new InvalidOperationException(
                "Lambda expression reached postprocessing without a resolved RoutineTypeInfo.");
        }

        string liftedName =
            $"__lambda_{lambda.Location.Line}_{lambda.Location.Column}_{_lambdaCounter++}";
        List<string>? genericParameters = inheritedGenericParameters?.ToList();
        List<GenericConstraintDeclaration>? genericConstraints =
            inheritedGenericConstraints?.ToList();

        var lambdaScope = new HashSet<string>(StringComparer.Ordinal);
        foreach (Parameter parameter in lambda.Parameters)
        {
            lambdaScope.Add(item: parameter.Name);
        }

        Expression loweredBody = RewriteExpression(lambda.Body,
            scope: lambdaScope,
            inheritedGenericParameters: genericParameters,
            inheritedGenericConstraints: genericConstraints,
            includeMe: false);

        var liftedRoutine = new RoutineDeclaration(
            Name: liftedName,
            Parameters: BuildLiftedParameters(lambda, routineType),
            ReturnType: routineType.ReturnType != null
                ? TypeInfoToTypeExpression(routineType.ReturnType, lambda.Location)
                : null,
            Body: new BlockStatement(
                Statements:
                [
                    new ReturnStatement(Value: loweredBody, Location: lambda.Location)
                ],
                Location: lambda.Location),
            Visibility: VisibilityModifier.Secret,
            Annotations: [],
            Location: lambda.Location,
            GenericParameters: genericParameters,
            GenericConstraints: genericConstraints,
            IsFailable: false,
            Storage: StorageClass.None,
            Async: AsyncStatus.None,
            IsDangerous: false);
        _liftedRoutines.Add(item: liftedRoutine);

        ctx.Registry.RegisterRoutine(routine: new RoutineInfo(name: liftedName)
        {
            Kind = RoutineKind.Function,
            Parameters = BuildLiftedParameterInfos(lambda, routineType),
            ReturnType = routineType.ReturnType,
            Visibility = VisibilityModifier.Secret,
            Location = lambda.Location,
            Module = _currentModuleName,
            ModulePath = _currentModuleName?.Split('/'),
            GenericParameters = genericParameters,
            GenericConstraints = genericConstraints,
            IsSynthesized = true
        });

        return new IdentifierExpression(Name: liftedName, Location: lambda.Location)
        {
            ResolvedType = lambda.ResolvedType
        };
    }

    private Pattern RewritePatternExpressions(Pattern pattern,
        HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints,
        bool includeMe)
    {
        return pattern switch
        {
            ExpressionPattern expressionPattern => expressionPattern with
            {
                Expression = RewritePatternExpression(expressionPattern.Expression,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            ComparisonPattern comparison => comparison with
            {
                Value = RewritePatternExpression(comparison.Value,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            GuardPattern guard => guard with
            {
                InnerPattern = RewritePatternExpressions(guard.InnerPattern,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                Guard = RewritePatternExpression(guard.Guard,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            _ => pattern
        };
    }

    private Expression RewritePatternExpression(Expression expression,
        HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints,
        bool includeMe)
    {
        return RewriteExpression(expression,
            scope: scope,
            inheritedGenericParameters: inheritedGenericParameters,
            inheritedGenericConstraints: inheritedGenericConstraints,
            includeMe: includeMe);
    }

    private static Expression CopyResolvedType<TExpression>(TExpression rewritten,
        TExpression original)
        where TExpression : Expression
    {
        rewritten.ResolvedType = original.ResolvedType;
        return rewritten;
    }

    private static HashSet<string> BuildRoutineScope(RoutineDeclaration routine, bool includeMe)
    {
        var scope = new HashSet<string>(StringComparer.Ordinal);
        foreach (Parameter parameter in routine.Parameters)
        {
            scope.Add(item: parameter.Name);
        }

        if (includeMe)
        {
            scope.Add(item: "me");
        }

        return scope;
    }

    private static void AddStatementBindings(HashSet<string> scope, Statement statement)
    {
        switch (statement)
        {
            case DeclarationStatement { Declaration: VariableDeclaration variable }:
                scope.Add(item: variable.Name);
                break;

            case DestructuringStatement destructuring:
                foreach (string binding in GetPatternBindings(destructuring.Pattern))
                {
                    scope.Add(item: binding);
                }
                break;
        }
    }

    private static List<string> MergeGenericParameters(List<string>? a, List<string>? b)
    {
        if (a == null && b == null)
        {
            return [];
        }

        var merged = new List<string>();
        foreach (string item in a ?? [])
        {
            if (!merged.Contains(item))
            {
                merged.Add(item);
            }
        }

        foreach (string item in b ?? [])
        {
            if (!merged.Contains(item))
            {
                merged.Add(item);
            }
        }

        return merged;
    }

    private static List<GenericConstraintDeclaration> MergeGenericConstraints(
        List<GenericConstraintDeclaration>? a,
        List<GenericConstraintDeclaration>? b)
    {
        var merged = new List<GenericConstraintDeclaration>();
        foreach (GenericConstraintDeclaration item in a ?? [])
        {
            if (!merged.Contains(item))
            {
                merged.Add(item);
            }
        }

        foreach (GenericConstraintDeclaration item in b ?? [])
        {
            if (!merged.Contains(item))
            {
                merged.Add(item);
            }
        }

        return merged;
    }

    private static List<Parameter> BuildLiftedParameters(LambdaExpression lambda,
        RoutineTypeInfo routineType)
    {
        var parameters = new List<Parameter>(capacity: lambda.Parameters.Count);
        for (int i = 0; i < lambda.Parameters.Count; i++)
        {
            Parameter sourceParam = lambda.Parameters[i];
            TypeInfo paramType = i < routineType.ParameterTypes.Count
                ? routineType.ParameterTypes[i]
                : ErrorTypeInfo.Instance;
            parameters.Add(item: sourceParam with
            {
                Type = TypeInfoToTypeExpression(paramType, sourceParam.Location),
                DefaultValue = null
            });
        }

        return parameters;
    }

    private static List<ParameterInfo> BuildLiftedParameterInfos(LambdaExpression lambda,
        RoutineTypeInfo routineType)
    {
        var parameters = new List<ParameterInfo>(capacity: lambda.Parameters.Count);
        for (int i = 0; i < lambda.Parameters.Count; i++)
        {
            TypeInfo paramType = i < routineType.ParameterTypes.Count
                ? routineType.ParameterTypes[i]
                : ErrorTypeInfo.Instance;
            parameters.Add(item: new ParameterInfo(name: lambda.Parameters[i].Name, type: paramType));
        }

        return parameters;
    }

    private static TypeExpression TypeInfoToTypeExpression(TypeInfo type, SourceLocation location)
    {
        string baseName = type switch
        {
            RecordTypeInfo { GenericDefinition: not null } record => record.GenericDefinition.Name,
            EntityTypeInfo { GenericDefinition: not null } entity => entity.GenericDefinition.Name,
            ProtocolTypeInfo { GenericDefinition: not null } protocol => protocol.GenericDefinition.Name,
            _ => type.IsGenericResolution && type.Name.Contains('[')
                ? type.Name[..type.Name.IndexOf('[')]
                : type.Name
        };

        List<TypeExpression>? args = type.TypeArguments is { Count: > 0 }
            ? type.TypeArguments.Select(arg => TypeInfoToTypeExpression(arg, location)).ToList()
            : null;
        return new TypeExpression(Name: baseName, GenericArguments: args, Location: location);
    }

    private static HashSet<string> CollectLocalCaptures(LambdaExpression lambda,
        HashSet<string> outerScope)
    {
        var captures = new HashSet<string>(StringComparer.Ordinal);
        var parameterNames = lambda.Parameters
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);

        CollectLocalCapturesRecursive(lambda.Body, outerScope, parameterNames, captures);
        return captures;
    }

    private static void CollectLocalCapturesRecursive(Expression expression,
        HashSet<string> outerScope,
        HashSet<string> parameterNames,
        HashSet<string> captures)
    {
        switch (expression)
        {
            case IdentifierExpression identifier when
                outerScope.Contains(identifier.Name) &&
                !parameterNames.Contains(identifier.Name):
                captures.Add(item: identifier.Name);
                break;

            case LambdaExpression:
                return;

            case CompoundAssignmentExpression compound:
                CollectLocalCapturesRecursive(compound.Target, outerScope, parameterNames, captures);
                CollectLocalCapturesRecursive(compound.Value, outerScope, parameterNames, captures);
                break;

            case BinaryExpression binary:
                CollectLocalCapturesRecursive(binary.Left, outerScope, parameterNames, captures);
                CollectLocalCapturesRecursive(binary.Right, outerScope, parameterNames, captures);
                break;

            case UnaryExpression unary:
                CollectLocalCapturesRecursive(unary.Operand, outerScope, parameterNames, captures);
                break;

            case CallExpression call:
                CollectLocalCapturesRecursive(call.Callee, outerScope, parameterNames, captures);
                foreach (Expression argument in call.Arguments)
                {
                    CollectLocalCapturesRecursive(argument, outerScope, parameterNames, captures);
                }
                break;

            case MemberExpression member:
                CollectLocalCapturesRecursive(member.Object, outerScope, parameterNames, captures);
                break;

            case OptionalMemberExpression optionalMember:
                CollectLocalCapturesRecursive(optionalMember.Object, outerScope, parameterNames, captures);
                break;

            case IndexExpression index:
                CollectLocalCapturesRecursive(index.Object, outerScope, parameterNames, captures);
                CollectLocalCapturesRecursive(index.Index, outerScope, parameterNames, captures);
                break;

            case SliceExpression slice:
                CollectLocalCapturesRecursive(slice.Object, outerScope, parameterNames, captures);
                CollectLocalCapturesRecursive(slice.Start, outerScope, parameterNames, captures);
                CollectLocalCapturesRecursive(slice.End, outerScope, parameterNames, captures);
                break;

            case ConditionalExpression conditional:
                CollectLocalCapturesRecursive(conditional.Condition, outerScope, parameterNames, captures);
                CollectLocalCapturesRecursive(conditional.TrueExpression, outerScope, parameterNames, captures);
                CollectLocalCapturesRecursive(conditional.FalseExpression, outerScope, parameterNames, captures);
                break;

            case RangeExpression range:
                CollectLocalCapturesRecursive(range.Start, outerScope, parameterNames, captures);
                CollectLocalCapturesRecursive(range.End, outerScope, parameterNames, captures);
                if (range.Step != null)
                {
                    CollectLocalCapturesRecursive(range.Step, outerScope, parameterNames, captures);
                }
                break;

            case CreatorExpression creator:
                foreach ((_, Expression value) in creator.MemberVariables)
                {
                    CollectLocalCapturesRecursive(value, outerScope, parameterNames, captures);
                }
                break;

            case WithExpression withExpr:
                CollectLocalCapturesRecursive(withExpr.Base, outerScope, parameterNames, captures);
                foreach ((_, Expression? index, Expression value) in withExpr.Updates)
                {
                    CollectLocalCapturesRecursive(value, outerScope, parameterNames, captures);
                    if (index != null)
                    {
                        CollectLocalCapturesRecursive(index, outerScope, parameterNames, captures);
                    }
                }
                break;

            case GenericMethodCallExpression genericCall:
                CollectLocalCapturesRecursive(genericCall.Object, outerScope, parameterNames, captures);
                foreach (Expression argument in genericCall.Arguments)
                {
                    CollectLocalCapturesRecursive(argument, outerScope, parameterNames, captures);
                }
                break;

            case GenericMemberExpression genericMember:
                CollectLocalCapturesRecursive(genericMember.Object, outerScope, parameterNames, captures);
                break;

            case NamedArgumentExpression namedArgument:
                CollectLocalCapturesRecursive(namedArgument.Value, outerScope, parameterNames, captures);
                break;

            case ListLiteralExpression list:
                foreach (Expression element in list.Elements)
                {
                    CollectLocalCapturesRecursive(element, outerScope, parameterNames, captures);
                }
                break;

            case SetLiteralExpression set:
                foreach (Expression element in set.Elements)
                {
                    CollectLocalCapturesRecursive(element, outerScope, parameterNames, captures);
                }
                break;

            case DictLiteralExpression dict:
                foreach ((Expression key, Expression value) in dict.Pairs)
                {
                    CollectLocalCapturesRecursive(key, outerScope, parameterNames, captures);
                    CollectLocalCapturesRecursive(value, outerScope, parameterNames, captures);
                }
                break;

            case TupleLiteralExpression tuple:
                foreach (Expression element in tuple.Elements)
                {
                    CollectLocalCapturesRecursive(element, outerScope, parameterNames, captures);
                }
                break;

            case TypeConversionExpression conversion:
                CollectLocalCapturesRecursive(conversion.Expression, outerScope, parameterNames, captures);
                break;

            case ChainedComparisonExpression chained:
                foreach (Expression operand in chained.Operands)
                {
                    CollectLocalCapturesRecursive(operand, outerScope, parameterNames, captures);
                }
                break;

            case BlockExpression block:
                CollectLocalCapturesRecursive(block.Value, outerScope, parameterNames, captures);
                break;

            case DictEntryLiteralExpression dictEntry:
                CollectLocalCapturesRecursive(dictEntry.Key, outerScope, parameterNames, captures);
                CollectLocalCapturesRecursive(dictEntry.Value, outerScope, parameterNames, captures);
                break;

            case IsPatternExpression isPattern:
                CollectLocalCapturesRecursive(isPattern.Expression, outerScope, parameterNames, captures);
                CollectLocalCapturesInPattern(isPattern.Pattern, outerScope, parameterNames, captures);
                break;

            case FlagsTestExpression flagsTest:
                CollectLocalCapturesRecursive(flagsTest.Subject, outerScope, parameterNames, captures);
                break;

            case InsertedTextExpression inserted:
                foreach (InsertedTextPart part in inserted.Parts)
                {
                    if (part is ExpressionPart expressionPart)
                    {
                        CollectLocalCapturesRecursive(expressionPart.Expression, outerScope, parameterNames, captures);
                    }
                }
                break;

            case StealExpression steal:
                CollectLocalCapturesRecursive(steal.Operand, outerScope, parameterNames, captures);
                break;

            case WaitforExpression waitfor:
                CollectLocalCapturesRecursive(waitfor.Operand, outerScope, parameterNames, captures);
                if (waitfor.Timeout != null)
                {
                    CollectLocalCapturesRecursive(waitfor.Timeout, outerScope, parameterNames, captures);
                }
                break;

            case DependentWaitforExpression dependentWaitfor:
                CollectLocalCapturesRecursive(dependentWaitfor.Operand, outerScope, parameterNames, captures);
                foreach (TaskDependency dependency in dependentWaitfor.Dependencies)
                {
                    CollectLocalCapturesRecursive(dependency.DependencyExpr, outerScope, parameterNames, captures);
                }
                if (dependentWaitfor.Timeout != null)
                {
                    CollectLocalCapturesRecursive(dependentWaitfor.Timeout, outerScope, parameterNames, captures);
                }
                break;

            case CarrierPayloadExpression payload:
                CollectLocalCapturesRecursive(payload.Carrier, outerScope, parameterNames, captures);
                break;

            case BackIndexExpression backIndex:
                CollectLocalCapturesRecursive(backIndex.Operand, outerScope, parameterNames, captures);
                break;

            case WhenExpression whenExpr:
                if (whenExpr.Expression != null)
                {
                    CollectLocalCapturesRecursive(whenExpr.Expression, outerScope, parameterNames, captures);
                }

                foreach (WhenClause clause in whenExpr.Clauses)
                {
                    CollectLocalCapturesInStatement(clause.Body, outerScope, parameterNames, captures);
                }
                break;
        }
    }

    private static void CollectLocalCapturesInStatement(Statement statement,
        HashSet<string> outerScope,
        HashSet<string> parameterNames,
        HashSet<string> captures)
    {
        switch (statement)
        {
            case BlockStatement block:
                foreach (Statement child in block.Statements)
                {
                    CollectLocalCapturesInStatement(child, outerScope, parameterNames, captures);
                }
                break;
            case IfStatement ifs:
                CollectLocalCapturesRecursive(ifs.Condition, outerScope, parameterNames, captures);
                CollectLocalCapturesInStatement(ifs.ThenStatement, outerScope, parameterNames, captures);
                if (ifs.ElseStatement != null)
                {
                    CollectLocalCapturesInStatement(ifs.ElseStatement, outerScope, parameterNames, captures);
                }
                break;
            case WhileStatement whileStmt:
                CollectLocalCapturesRecursive(whileStmt.Condition, outerScope, parameterNames, captures);
                CollectLocalCapturesInStatement(whileStmt.Body, outerScope, parameterNames, captures);
                if (whileStmt.ElseBranch != null)
                {
                    CollectLocalCapturesInStatement(whileStmt.ElseBranch, outerScope, parameterNames, captures);
                }
                break;
            case LoopStatement loop:
                CollectLocalCapturesInStatement(loop.Body, outerScope, parameterNames, captures);
                break;
            case ForStatement forStmt:
                CollectLocalCapturesRecursive(forStmt.Iterable, outerScope, parameterNames, captures);
                CollectLocalCapturesInStatement(forStmt.Body, outerScope, parameterNames, captures);
                if (forStmt.ElseBranch != null)
                {
                    CollectLocalCapturesInStatement(forStmt.ElseBranch, outerScope, parameterNames, captures);
                }
                break;
            case WhenStatement whenStmt:
                CollectLocalCapturesRecursive(whenStmt.Expression, outerScope, parameterNames, captures);
                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    CollectLocalCapturesInStatement(clause.Body, outerScope, parameterNames, captures);
                }
                break;
            case UsingStatement usingStmt:
                CollectLocalCapturesRecursive(usingStmt.Resource, outerScope, parameterNames, captures);
                CollectLocalCapturesInStatement(usingStmt.Body, outerScope, parameterNames, captures);
                break;
            case DangerStatement danger:
                CollectLocalCapturesInStatement(danger.Body, outerScope, parameterNames, captures);
                break;
            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: not null } variable }:
                CollectLocalCapturesRecursive(variable.Initializer, outerScope, parameterNames, captures);
                break;
            case AssignmentStatement assignment:
                CollectLocalCapturesRecursive(assignment.Target, outerScope, parameterNames, captures);
                CollectLocalCapturesRecursive(assignment.Value, outerScope, parameterNames, captures);
                break;
            case ReturnStatement { Value: not null } ret:
                CollectLocalCapturesRecursive(ret.Value, outerScope, parameterNames, captures);
                break;
            case ExpressionStatement expressionStatement:
                CollectLocalCapturesRecursive(expressionStatement.Expression, outerScope, parameterNames, captures);
                break;
            case DiscardStatement discard:
                CollectLocalCapturesRecursive(discard.Expression, outerScope, parameterNames, captures);
                break;
            case BecomesStatement becomes:
                CollectLocalCapturesRecursive(becomes.Value, outerScope, parameterNames, captures);
                break;
            case ThrowStatement throwStmt:
                CollectLocalCapturesRecursive(throwStmt.Error, outerScope, parameterNames, captures);
                break;
            case VariantReturnStatement { Value: not null } variantReturn:
                CollectLocalCapturesRecursive(variantReturn.Value, outerScope, parameterNames, captures);
                break;
            case DestructuringStatement destructuring:
                CollectLocalCapturesRecursive(destructuring.Initializer, outerScope, parameterNames, captures);
                break;
        }
    }

    private static void CollectLocalCapturesInPattern(Pattern pattern,
        HashSet<string> outerScope,
        HashSet<string> parameterNames,
        HashSet<string> captures)
    {
        switch (pattern)
        {
            case ExpressionPattern expressionPattern:
                CollectLocalCapturesRecursive(expressionPattern.Expression, outerScope, parameterNames, captures);
                break;
            case ComparisonPattern comparison:
                CollectLocalCapturesRecursive(comparison.Value, outerScope, parameterNames, captures);
                break;
            case GuardPattern guard:
                CollectLocalCapturesInPattern(guard.InnerPattern, outerScope, parameterNames, captures);
                CollectLocalCapturesRecursive(guard.Guard, outerScope, parameterNames, captures);
                break;
        }
    }

    private static bool ContainsIdentifier(Expression expression, string name)
    {
        var hits = new HashSet<string>(StringComparer.Ordinal);
        CollectLocalCapturesRecursive(expression,
            outerScope: [name],
            parameterNames: [],
            captures: hits);
        return hits.Contains(item: name);
    }

    private static IEnumerable<string> GetPatternBindings(Pattern? pattern)
    {
        if (pattern == null)
        {
            yield break;
        }

        switch (pattern)
        {
            case IdentifierPattern identifier:
                yield return identifier.Name;
                break;

            case TypePattern { VariableName: not null } typePattern:
                yield return typePattern.VariableName;
                foreach (string binding in GetDestructuringBindings(typePattern.Bindings))
                {
                    yield return binding;
                }
                break;

            case VariantPattern variantPattern:
                foreach (string binding in GetDestructuringBindings(variantPattern.Bindings))
                {
                    yield return binding;
                }
                break;

            case CrashablePattern { VariableName: not null } crashablePattern:
                yield return crashablePattern.VariableName;
                break;

            case ElsePattern { VariableName: not null } elsePattern:
                yield return elsePattern.VariableName;
                break;

            case DestructuringPattern destructuring:
                foreach (string binding in GetDestructuringBindings(destructuring.Bindings))
                {
                    yield return binding;
                }
                break;

            case GuardPattern guard:
                foreach (string binding in GetPatternBindings(guard.InnerPattern))
                {
                    yield return binding;
                }
                break;
        }
    }

    private static IEnumerable<string> GetDestructuringBindings(
        IReadOnlyList<DestructuringBinding>? bindings)
    {
        if (bindings == null)
        {
            yield break;
        }

        foreach (DestructuringBinding binding in bindings)
        {
            if (binding.BindingName != null)
            {
                yield return binding.BindingName;
            }

            if (binding.NestedPattern != null)
            {
                foreach (string nested in GetPatternBindings(binding.NestedPattern))
                {
                    yield return nested;
                }
            }
        }
    }
}
