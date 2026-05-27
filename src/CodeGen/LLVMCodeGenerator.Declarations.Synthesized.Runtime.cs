using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Declaration code generation for synthesized runtime-support routines.
/// </summary>
public partial class LlvmCodeGenerator
{
    private string EmitSynthesizedStringLiteral(string value)
    {
        return EmitStringLiteralGlobal(value: value);
    }

    private void EmitSynthesizedBodyFromAst(RoutineInfo routine, string funcName, Statement body)
    {
        var paramList = new List<string>();
        if (routine.OwnerType != null && !IsCreatorRoutine(routine: routine) && !routine.IsCommon)
        {
            string meType =
                GetImplicitMeParameterDeclaration(routine: routine, includeName: true);
            if (!meType.StartsWith(value: "void", comparisonType: StringComparison.Ordinal))
                paramList.Add(item: meType);
        }
        paramList.AddRange(collection:
            from param in routine.Parameters
            let paramType = GetParameterLlvmType(type: param.Type)
            let emittedName = param.Name == "entry" ? "entry_" : param.Name
            select $"{paramType} %{emittedName}");

        string returnType = routine.ReturnType != null ? GetLlvmType(type: routine.ReturnType) : "void";
        string parameters = string.Join(separator: ", ", values: paramList);

        int savedLength = _functionDefinitions.Length;
        int savedTempCounter = _tempCounter;
        EmitLine(sb: _functionDefinitions, line: $"define {returnType} @{funcName}({parameters}) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        var bodyBuilder = new StringBuilder();
        try
        {
            GenerateRoutineBody(sb: bodyBuilder, body: body, routine: routine);
            _functionDefinitions.Append(value: _currentRoutineEntryAllocas);
            _functionDefinitions.Append(value: bodyBuilder);
        }
        catch
        {
            _functionDefinitions.Length = savedLength;
            _tempCounter = savedTempCounter;
            _generatedRoutineDefs.Remove(item: funcName);
            throw;
        }
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

}
