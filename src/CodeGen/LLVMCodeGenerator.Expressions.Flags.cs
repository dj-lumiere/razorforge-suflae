namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Types;
using SyntaxTree;

public partial class LLVMCodeGenerator
{
    private string EmitFlagsTest(StringBuilder sb, FlagsTestExpression flagsTest)
    {
        string subject = EmitExpression(sb: sb, expr: flagsTest.Subject);
        TypeInfo? subjectType = GetExpressionType(expr: flagsTest.Subject);

        var flagsType = subjectType as FlagsTypeInfo;

        // Build the combined test mask from TestFlags
        ulong testMask = 0;
        foreach (string flagName in flagsTest.TestFlags)
        {
            testMask |= ResolveFlagBit(flagName: flagName, flagsType: flagsType);
        }

        // Build the excluded mask from ExcludedFlags (if present)
        ulong excludedMask = 0;
        if (flagsTest.ExcludedFlags != null)
        {
            foreach (string flagName in flagsTest.ExcludedFlags)
            {
                excludedMask |= ResolveFlagBit(flagName: flagName, flagsType: flagsType);
            }
        }

        string maskStr = testMask.ToString();

        return flagsTest.Kind switch
        {
            FlagsTestKind.Is => EmitFlagsIsTest(sb: sb,
                subject: subject,
                mask: maskStr,
                connective: flagsTest.Connective,
                excludedMask: excludedMask),
            FlagsTestKind.IsNot => EmitFlagsIsNotTest(sb: sb, subject: subject, mask: maskStr),
            FlagsTestKind.IsOnly => EmitFlagsIsOnlyTest(sb: sb, subject: subject, mask: maskStr),
            _ => throw new InvalidOperationException(
                message: $"Unknown flags test kind: {flagsTest.Kind}")
        };
    }

    /// <summary>
    /// Resolves a flag member name to its bit value (1UL &lt;&lt; BitPosition).
    /// Falls back to 0 if not found.
    /// </summary>
    private static ulong ResolveFlagBit(string flagName, FlagsTypeInfo? flagsType)
    {
        if (flagsType == null)
        {
            return 0;
        }

        foreach (FlagsMemberInfo member in flagsType.Members)
        {
            if (member.Name == flagName)
            {
                return 1UL << member.BitPosition;
            }
        }

        return 0;
    }

    /// <summary>
    /// x is A and B → (x &amp; mask) == mask (all flags set)
    /// x is A or B  → (x &amp; mask) != 0 (any flag set)
    /// x is A and B but C → ((x &amp; mask) == mask) &amp;&amp; ((x &amp; excludedMask) == 0)
    /// </summary>
    private string EmitFlagsIsTest(StringBuilder sb, string subject, string mask,
        FlagsTestConnective connective, ulong excludedMask)
    {
        string andResult = NextTemp();
        EmitLine(sb: sb, line: $"  {andResult} = and i64 {subject}, {mask}");

        string cmpResult;
        if (connective == FlagsTestConnective.Or)
        {
            // Any flag set: (subject & mask) != 0
            cmpResult = NextTemp();
            EmitLine(sb: sb, line: $"  {cmpResult} = icmp ne i64 {andResult}, 0");
        }
        else
        {
            // All flags set: (subject & mask) == mask
            cmpResult = NextTemp();
            EmitLine(sb: sb, line: $"  {cmpResult} = icmp eq i64 {andResult}, {mask}");
        }

        // Handle 'but' exclusion
        if (excludedMask > 0)
        {
            string exclAnd = NextTemp();
            EmitLine(sb: sb, line: $"  {exclAnd} = and i64 {subject}, {excludedMask}");
            string exclCmp = NextTemp();
            EmitLine(sb: sb, line: $"  {exclCmp} = icmp eq i64 {exclAnd}, 0");
            // Combined: cmpResult && exclCmp
            string combined = NextTemp();
            EmitLine(sb: sb, line: $"  {combined} = and i1 {cmpResult}, {exclCmp}");
            return combined;
        }

        return cmpResult;
    }

    /// <summary>
    /// x isnot A → (x &amp; mask) != mask (flag not fully set)
    /// </summary>
    private string EmitFlagsIsNotTest(StringBuilder sb, string subject, string mask)
    {
        string andResult = NextTemp();
        EmitLine(sb: sb, line: $"  {andResult} = and i64 {subject}, {mask}");
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = icmp ne i64 {andResult}, {mask}");
        return result;
    }

    /// <summary>
    /// x isonly A and B → x == mask (exact match)
    /// </summary>
    private string EmitFlagsIsOnlyTest(StringBuilder sb, string subject, string mask)
    {
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = icmp eq i64 {subject}, {mask}");
        return result;
    }
}
