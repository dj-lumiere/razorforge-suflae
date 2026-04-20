namespace Compiler.CodeGen;

using System.Text;

public partial class LlvmCodeGenerator
{
    // Node layout (appended once at module end):
    //   !0  = root
    //   !1  = i1    !2  = i8    !3  = i16   !4  = i32
    //   !5  = i64   !6  = float !7  = double !8  = ptr
    //   !9  = half  !10 = fp128 !11 = i128
    //   !12 = i1 access tag    !13 = i8     !14 = i16   !15 = i32
    //   !16 = i64 access tag   !17 = float  !18 = double !19 = ptr
    //   !20 = half access tag  !21 = fp128  !22 = i128

    private static readonly string TbaaMetadataSection =
        "; TBAA metadata\n" +
        "!0 = !{!\"RF TBAA Root\"}\n" +
        "!1 = !{!\"i1\", !0}\n" +
        "!2 = !{!\"i8\", !0}\n" +
        "!3 = !{!\"i16\", !0}\n" +
        "!4 = !{!\"i32\", !0}\n" +
        "!5 = !{!\"i64\", !0}\n" +
        "!6 = !{!\"float\", !0}\n" +
        "!7 = !{!\"double\", !0}\n" +
        "!8 = !{!\"ptr\", !0}\n" +
        "!9 = !{!\"half\", !0}\n" +
        "!10 = !{!\"fp128\", !0}\n" +
        "!11 = !{!\"i128\", !0}\n" +
        "!12 = !{!1,  !1,  i64 0}\n" +
        "!13 = !{!2,  !2,  i64 0}\n" +
        "!14 = !{!3,  !3,  i64 0}\n" +
        "!15 = !{!4,  !4,  i64 0}\n" +
        "!16 = !{!5,  !5,  i64 0}\n" +
        "!17 = !{!6,  !6,  i64 0}\n" +
        "!18 = !{!7,  !7,  i64 0}\n" +
        "!19 = !{!8,  !8,  i64 0}\n" +
        "!20 = !{!9,  !9,  i64 0}\n" +
        "!21 = !{!10, !10, i64 0}\n" +
        "!22 = !{!11, !11, i64 0}\n";

    private static readonly Dictionary<string, string> TbaaTagByLlvmType = new()
    {
        ["i1"]     = ", !tbaa !12",
        ["i8"]     = ", !tbaa !13",
        ["i16"]    = ", !tbaa !14",
        ["i32"]    = ", !tbaa !15",
        ["i64"]    = ", !tbaa !16",
        ["float"]  = ", !tbaa !17",
        ["double"] = ", !tbaa !18",
        ["ptr"]    = ", !tbaa !19",
        ["half"]   = ", !tbaa !20",
        ["fp128"]  = ", !tbaa !21",
        ["i128"]   = ", !tbaa !22",
    };

    private static string ApplyTbaa(string ir)
    {
        var lines = ir.Split('\n');
        var sb = new StringBuilder(capacity: ir.Length + 2048);
        foreach (var line in lines)
            sb.Append(TagLine(line)).Append('\n');
        sb.Append(TbaaMetadataSection);
        return sb.ToString();
    }

    private static string TagLine(string line)
    {
        if (line.Contains("!tbaa")) return line;

        var t = line.AsSpan().TrimStart();

        // load:  "  %x = load TYPE, ptr ..."
        int loadIdx = line.IndexOf(" = load ", StringComparison.Ordinal);
        if (loadIdx >= 0)
        {
            int typeStart = loadIdx + " = load ".Length;
            int comma = line.IndexOf(',', typeStart);
            if (comma > typeStart)
            {
                var llvmType = line[typeStart..comma].Trim();
                if (TbaaTagByLlvmType.TryGetValue(llvmType, out var tag))
                    return line + tag;
            }
            return line;
        }

        // store: "  store TYPE VALUE, ptr ..."
        if (t.StartsWith("store ", StringComparison.Ordinal))
        {
            int storeStart = line.IndexOf("store ", StringComparison.Ordinal) + "store ".Length;
            int space = line.IndexOf(' ', storeStart);
            if (space > storeStart)
            {
                var llvmType = line[storeStart..space].Trim();
                if (TbaaTagByLlvmType.TryGetValue(llvmType, out var tag))
                    return line + tag;
            }
        }

        return line;
    }
}
