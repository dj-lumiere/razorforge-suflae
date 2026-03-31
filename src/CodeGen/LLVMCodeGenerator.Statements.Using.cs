using SemanticAnalysis.Symbols;

namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Enums;
using SemanticAnalysis.Types;
using SyntaxTree;

public partial class LLVMCodeGenerator
{
    private void EmitUsing(StringBuilder sb, UsingStatement usingStmt)
    {
        // Evaluate the resource expression
        string resourceValue = EmitExpression(sb: sb, expr: usingStmt.Resource);
        TypeInfo? resourceType = GetExpressionType(expr: usingStmt.Resource);
        string llvmType = resourceType != null
            ? GetLLVMType(type: resourceType)
            : "ptr";

        // Check if $enter exists for this type — LookupMethod handles generic type fallback
        RoutineInfo? enterMethod = resourceType != null
            ? _registry.LookupMethod(type: resourceType, methodName: "$enter")
            : null;

        string boundValue;
        TypeInfo? boundType;

        if (enterMethod != null)
        {
            // Resource path: call $enter(), bind result (or resource if void)
            GenerateFunctionDeclaration(routine: enterMethod);
            string enterMangled = MangleFunctionName(routine: enterMethod);
            string receiverType = GetParameterLLVMType(type: resourceType!);

            if (enterMethod.ReturnType != null &&
                GetLLVMType(type: enterMethod.ReturnType) != "void")
            {
                string enterResult = NextTemp();
                string returnType = GetLLVMType(type: enterMethod.ReturnType);
                EmitLine(sb: sb,
                    line:
                    $"  {enterResult} = call {returnType} @{enterMangled}({receiverType} {resourceValue})");
                boundValue = enterResult;
                boundType = enterMethod.ReturnType;
            }
            else
            {
                EmitLine(sb: sb,
                    line: $"  call void @{enterMangled}({receiverType} {resourceValue})");
                boundValue = resourceValue;
                boundType = resourceType;
            }
        }
        else
        {
            // Fallback: no $enter found (should not happen for well-typed programs)
            boundValue = resourceValue;
            boundType = resourceType;
        }

        // Allocate and store the bound variable
        string bindLlvmType = boundType != null
            ? GetLLVMType(type: boundType)
            : llvmType;
        string varAddr = $"%{usingStmt.Name}.addr";
        EmitLine(sb: sb, line: $"  {varAddr} = alloca {bindLlvmType}");
        EmitLine(sb: sb, line: $"  store {bindLlvmType} {boundValue}, ptr {varAddr}");

        if (boundType != null)
        {
            _localVariables[key: usingStmt.Name] = boundType;
        }

        // Look up $exit before the body so early exits can call it
        RoutineInfo? exitMethod = enterMethod != null
            ? _registry.LookupMethod(type: resourceType!, methodName: "$exit")
            : null;

        // Push cleanup so early exits (return/throw/break/continue/absent) call $exit
        if (exitMethod != null)
        {
            GenerateFunctionDeclaration(routine: exitMethod);
            _usingCleanupStack.Push(item: (resourceValue, resourceType!, exitMethod));
        }

        // Emit the body
        EmitStatement(sb: sb, stmt: usingStmt.Body);

        // Normal exit: call $exit and pop cleanup
        if (exitMethod != null)
        {
            _usingCleanupStack.Pop();
            string exitMangled = MangleFunctionName(routine: exitMethod);
            string receiverType = GetParameterLLVMType(type: resourceType);
            EmitLine(sb: sb, line: $"  call void @{exitMangled}({receiverType} {resourceValue})");
        }
    }
}
