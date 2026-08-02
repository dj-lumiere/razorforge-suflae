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
            let byval = ParameterPassedByval(routine: routine, paramType: param.Type)
            let coerce = byval ? null : ParameterCoerceType(routine: routine, paramType: param.Type)
            let paramType = byval ? $"ptr byval({GetLlvmType(type: param.Type)})"
                : coerce ?? GetParameterLlvmType(type: param.Type)
            let emittedName = byval ? $"{param.Name}.addr"
                : param.Name == "entry" ? "entry_" : param.Name
            select $"{paramType} %{emittedName}");

        string returnType = routine.ReturnType != null ? GetLlvmType(type: routine.ReturnType) : "void";

        // Mirror GenerateRoutineDefinition's ABI return handling (sret for Indirect, integer coercion
        // for small structs): this synthesized define path must agree with the declaration
        // GenerateRoutineDeclaration emitted, or the declare/define signature-match invariant trips.
        bool prevReturnViaSret = _currentReturnViaSret;
        string? prevReturnCoerce = _currentReturnCoerceType;
        _currentReturnViaSret = ReturnsViaSret(routine: routine);
        _currentReturnCoerceType = _currentReturnViaSret ? null : ReturnCoerceType(routine: routine);
        if (_currentReturnViaSret)
        {
            paramList.Insert(index: 0, item: $"ptr sret({returnType}) %sret");
        }

        string headerReturnType = _currentReturnViaSret ? "void"
            : _currentReturnCoerceType ?? returnType;
        string parameters = string.Join(separator: ", ", values: paramList);

        int savedLength = _functionDefinitions.Length;
        int savedTempCounter = _tempCounter;
        // Same whole-program-internal treatment as GenerateRoutineDefinition: these are all
        // compiler-synthesized bodies (auto-derived $destroy/$store/copy, wrapper forwarding, …),
        // referenced only within this module, so `internal` linkage lets GlobalDCE strip the uncalled
        // ones and `nounwind` reflects that the runtime never unwinds.
        bool isCompilerGenerated = routine.IsSynthesized || routine.IsWiredMemberRoutine;
        string linkagePrefix = isCompilerGenerated ? "internal " : "";
        string synthAttrs = isCompilerGenerated ? " nounwind" : "";
        string defineHeader =
            $"define {linkagePrefix}{headerReturnType} @{funcName}({parameters}){synthAttrs} {{";
        _generatedRoutineDefHeaders[key: funcName] = defineHeader;
        EmitLine(sb: _functionDefinitions, line: defineHeader);
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
            _generatedRoutineDefHeaders.Remove(key: funcName);
            throw;
        }
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
        _currentReturnViaSret = prevReturnViaSret;
        _currentReturnCoerceType = prevReturnCoerce;
    }

}
