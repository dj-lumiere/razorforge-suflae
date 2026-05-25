using System.Collections.Generic;
using System.Linq;
using System.Text;
using Compiler.Postprocessing;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Coordinates LLVM code generator behavior for this compiler phase.
/// </summary>
public partial class LlvmCodeGenerator
{
    /// <summary>
    /// Carries runtime dispatch target data between compiler phases.
    /// </summary>
    private readonly record struct CrashableDispatchTarget(TypeInfo ConcreteType, string FuncName);

    /// <summary>
    /// Performs the generate runtime dispatch stubs step for this compiler phase.
    /// </summary>
    private void GenerateCrashableDispatchStubs()
    {
        foreach ((string mangledName, CrashableDispatchInfo info) in _pendingCrashableDispatches)
        {
            if (ShouldDeferCrashableDispatchStub(mangledName: mangledName,
                    info: info,
                    out string? returnType,
                    out List<CrashableDispatchTarget>? implementers))
            {
                continue;
            }

            EmitCrashableDispatchStub(mangledName: mangledName,
                returnType: returnType!,
                implementers: implementers!);
            _generatedRoutineDefs.Add(item: mangledName);
        }
    }

    /// <summary>
    /// Emit runtime dispatch stub as part of this compiler phase.
    /// </summary>
    private void EmitCrashableDispatchStub(string mangledName, string returnType,
        List<CrashableDispatchTarget> implementers)
    {
        string defaultLabel = NextLabel(prefix: "dispatch_default");
        var caseLabels = implementers
                        .Select(selector: (_, i) => NextLabel(prefix: $"dispatch_{i}_"))
                        .ToList();

        EmitLine(sb: _functionDefinitions,
            line: $"define {returnType} @{mangledName}(ptr %self, i64 %type_id) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        EmitCrashableDispatchSwitch(sb: _functionDefinitions,
            implementers: implementers,
            defaultLabel: defaultLabel,
            caseLabels: caseLabels);

        for (int i = 0; i < implementers.Count; i++)
        {
            EmitCrashableDispatchCase(sb: _functionDefinitions,
                target: implementers[i],
                caseLabel: caseLabels[index: i],
                returnType: returnType);
        }

        EmitLine(sb: _functionDefinitions, line: $"{defaultLabel}:");
        EmitLine(sb: _functionDefinitions, line: "  unreachable");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emit runtime dispatch switch as part of this compiler phase.
    /// </summary>
    private void EmitCrashableDispatchSwitch(StringBuilder sb,
        List<CrashableDispatchTarget> implementers, string defaultLabel,
        List<string> caseLabels)
    {
        var switchSb = new StringBuilder();
        switchSb.Append($"  switch i64 %type_id, label %{defaultLabel} [");
        for (int i = 0; i < implementers.Count; i++)
        {
            ulong typeId =
                TypeIdHelper.ComputeTypeId(fullName: implementers[i].ConcreteType.FullName);
            switchSb.Append($"\n    i64 {typeId}, label %{caseLabels[index: i]}");
        }

        switchSb.Append("\n  ]");
        EmitLine(sb: sb, line: switchSb.ToString());
    }

    /// <summary>
    /// Emit runtime dispatch case as part of this compiler phase.
    /// </summary>
    private void EmitCrashableDispatchCase(StringBuilder sb, CrashableDispatchTarget target,
        string caseLabel, string returnType)
    {
        EmitLine(sb: sb, line: $"{caseLabel}:");
        if (target.ConcreteType is RecordTypeInfo)
        {
            EmitRecordCrashableDispatchCase(sb: sb, target: target, returnType: returnType);
            return;
        }

        EmitEntityCrashableDispatchCase(sb: sb, target: target, returnType: returnType);
    }

    /// <summary>
    /// Emit record runtime dispatch case as part of this compiler phase.
    /// </summary>
    private void EmitRecordCrashableDispatchCase(StringBuilder sb, CrashableDispatchTarget target,
        string returnType)
    {
        string llvmType = GetLlvmType(type: target.ConcreteType);
        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {llvmType}, ptr %self");

        if (returnType == "void")
        {
            EmitLine(sb: sb, line: $"  call void @{target.FuncName}({llvmType} {loaded})");
            EmitLine(sb: sb, line: "  ret void");
            return;
        }

        string result = NextTemp();
        EmitLine(sb: sb,
            line: $"  {result} = call {returnType} @{target.FuncName}({llvmType} {loaded})");
        EmitLine(sb: sb, line: $"  ret {returnType} {result}");
    }

    /// <summary>
    /// Emit entity runtime dispatch case as part of this compiler phase.
    /// </summary>
    private void EmitEntityCrashableDispatchCase(StringBuilder sb, CrashableDispatchTarget target,
        string returnType)
    {
        if (returnType == "void")
        {
            EmitLine(sb: sb, line: $"  call void @{target.FuncName}(ptr %self)");
            EmitLine(sb: sb, line: "  ret void");
            return;
        }

        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = call {returnType} @{target.FuncName}(ptr %self)");
        EmitLine(sb: sb, line: $"  ret {returnType} {result}");
    }

    /// <summary>
    /// Returns whether should defer runtime dispatch stub applies in the current compiler context.
    /// </summary>
    private bool ShouldDeferCrashableDispatchStub(string mangledName, CrashableDispatchInfo info,
        out string? returnType, out List<CrashableDispatchTarget>? implementers)
    {
        returnType = null;
        implementers = null;

        if (_generatedRoutineDefs.Contains(item: mangledName))
        {
            return true;
        }

        if (!_generatedRoutines.Contains(item: mangledName))
        {
            return true;
        }

        returnType = info.ReturnType;

        int triggered = TriggerAllImplementerCompilations(info: info);
        if (triggered > 0)
        {
            return true;
        }

        implementers = FindAllCompiledImplementers(info: info);
        return implementers.Count == 0;
    }

    /// <summary>
    /// Computes the LLVM return type for a runtime dispatch stub.
    /// Called once at registration time; result stored in CrashableDispatchInfo.ReturnType.
    /// </summary>
    private string ComputeCrashableDispatchReturnType(ProtocolTypeInfo protocol, string methodName)
    {
        ProtocolMethodInfo? protoMethod =
            protocol.Methods.FirstOrDefault(predicate: m => m.Name == methodName && !m.IsFailable);
        protoMethod ??= protocol.Methods.FirstOrDefault(predicate: m => m.Name == methodName);
        return protoMethod?.ReturnType != null
            ? GetLlvmType(type: protoMethod.ReturnType)
            : "void";
    }

    /// <summary>
    /// Returns all concrete implementers that have the named method already compiled
    /// (present in _generatedRoutineDefs). Uses the pre-computed KnownImplementers list.
    /// </summary>
    private List<CrashableDispatchTarget> FindAllCompiledImplementers(CrashableDispatchInfo info)
    {
        var result = new List<CrashableDispatchTarget>();
        foreach (TypeInfo type in info.KnownImplementers)
        {
            if (type.IsGenericDefinition)
            {
                continue;
            }

            string candidateName =
                Q(name: $"{type.FullName}.{SanitizeLlvmName(name: info.MemberRoutineName)}");
            if (_generatedRoutineDefs.Contains(item: candidateName))
            {
                result.Add(item: new CrashableDispatchTarget(type, candidateName));
            }
        }

        return result;
    }

    /// <summary>
    /// Ensures every uncompiled concrete implementer is declared so the next pass can define it.
    /// Returns the number of new declarations triggered.
    /// Uses the pre-computed KnownImplementers list.
    /// </summary>
    private int TriggerAllImplementerCompilations(CrashableDispatchInfo info)
    {
        int count = 0;
        foreach (TypeInfo concreteType in info.KnownImplementers)
        {
            RoutineInfo? concreteMethod =
                _registry.LookupMethod(type: concreteType, methodName: info.MemberRoutineName);
            if (concreteMethod == null)
            {
                continue;
            }

            string candidateName = MangleRoutineName(routine: concreteMethod);
            if (_generatedRoutineDefs.Contains(item: candidateName))
            {
                continue;
            }

            // Only count an implementer as "in-progress" if THIS call actually triggered new
            // state (a fresh declaration). Without this gate, count stays > 0 forever for
            // implementers whose bodies will never be emitted (unreachable Crashable types
            // declared on demand), and ShouldDeferCrashableDispatchStub never returns false →
            // the dispatch stub never gets generated and the linker reports it as undefined.
            // Newly-declared implementers still get their type prepared via Ensure*.
            if (!_generatedRoutines.Contains(item: candidateName))
            {
                GenerateRoutineDeclaration(routine: concreteMethod);
                EnsureCrashableDispatchConcreteTypeReady(concreteType: concreteType);
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Ensure runtime dispatch concrete type ready as part of this compiler phase.
    /// </summary>
    private void EnsureCrashableDispatchConcreteTypeReady(TypeInfo concreteType)
    {
        if (concreteType is EntityTypeInfo entityType)
            GenerateEntityType(entity: entityType);
        else if (concreteType is CrashableTypeInfo crashableType)
            GenerateCrashableType(crashable: crashableType);
    }
}
