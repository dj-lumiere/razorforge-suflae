namespace Compiler.CodeGen;

using SemanticAnalysis.Symbols;

public partial class LLVMCodeGenerator
{
    private void EmitSynthesizedBuilderServiceU64(RoutineInfo routine, string funcName, long value)
    {
        EmitLine(sb: _functionDefinitions, line: $"define i64 @{funcName}() {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        EmitLine(sb: _functionDefinitions, line: $"  ret i64 {value}");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    private void EmitSynthesizedBuilderServiceI32(RoutineInfo routine, string funcName, int value)
    {
        EmitLine(sb: _functionDefinitions, line: $"define i32 @{funcName}() {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        EmitLine(sb: _functionDefinitions, line: $"  ret i32 {value}");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits a BuilderService standalone routine that returns a Text constant.
    /// </summary>
    private void EmitSynthesizedBuilderServiceText(RoutineInfo routine, string funcName,
        string value)
    {
        string strConst = EmitSynthesizedStringLiteral(value: value);

        EmitLine(sb: _functionDefinitions, line: $"define ptr @{funcName}() {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        EmitLine(sb: _functionDefinitions, line: $"  ret ptr {strConst}");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>Returns the target OS name from the current <see cref="TargetConfig"/>.</summary>
    private string DetectTargetOS() => _target.TargetOS;

    /// <summary>Returns the target CPU architecture from the current <see cref="TargetConfig"/>.</summary>
    private string DetectTargetArch() => _target.TargetArch;
}
