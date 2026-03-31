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

    /// <summary>
    /// Detects the target operating system name.
    /// </summary>
    private static string DetectTargetOS()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                osPlatform: System.Runtime.InteropServices.OSPlatform.Windows))
        {
            return "windows";
        }

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                osPlatform: System.Runtime.InteropServices.OSPlatform.Linux))
        {
            return "linux";
        }

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                osPlatform: System.Runtime.InteropServices.OSPlatform.OSX))
        {
            return "macos";
        }

        return "unknown";
    }

    /// <summary>
    /// Detects the target CPU architecture name.
    /// </summary>
    private static string DetectTargetArch()
    {
        return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            _ => "unknown"
        };
    }
}
