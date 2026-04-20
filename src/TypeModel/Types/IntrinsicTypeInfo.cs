namespace TypeModel.Types;

using TypeModel.Enums;

/// <summary>
/// LLVM intrinsic types (@intrinsic.*) - internal implementation details only.
/// These are never exposed to user code, only used inside stdlib record definitions.
/// </summary>
// TODO: This whole thing is dead and we use @llvm annotation for this
public sealed class IntrinsicTypeInfo : TypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Intrinsic;

    /// <summary>The corresponding LLVM IR type string (e.g., "i32", "float", "double").</summary>
    public string LlvmType { get; }

    /// <summary>Size in bits (e.g., 32 for i32, 64 for double).</summary>
    public int BitSize { get; }

    /// <summary>Whether this is a floating-point type.</summary>
    public bool IsFloatingPoint { get; }

    /// <summary>Whether this is a pointer-sized type (iptr/uptr).</summary>
    public bool IsPointerSized { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="IntrinsicTypeInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the intrinsic type (e.g., "@intrinsic.i32").</param>
    /// <param name="llvmType">The corresponding LLVM IR type string.</param>
    /// <param name="bitSize">The size in bits.</param>
    /// <param name="isFloatingPoint">Whether this is a floating-point type.</param>
    /// <param name="isPointerSized">Whether this is a pointer-sized type.</param>
    public IntrinsicTypeInfo(string name, string llvmType, int bitSize,
        bool isFloatingPoint = false, bool isPointerSized = false) : base(name: name)
    {
        LlvmType = llvmType;
        BitSize = bitSize;
        IsFloatingPoint = isFloatingPoint;
        IsPointerSized = isPointerSized;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always thrown as intrinsic types cannot be generic.</exception>
    public override TypeInfo CreateInstance(IReadOnlyList<TypeInfo> typeArguments)
    {
        // Intrinsic types are never generic
        throw new InvalidOperationException(
            message: $"Intrinsic type '{Name}' cannot be resolved with type arguments.");
    }

    /// <summary>
    /// Well-known intrinsic types.
    /// </summary>
    // TODO: This is dead
    public static class WellKnown
    {
        // Boolean
        /// <summary>
        /// Represents a 1-bit LLVM intrinsic type (e.g., "i1"). This type is typically used for representing boolean values at the LLVM IR level.
        /// </summary>
        public static readonly IntrinsicTypeInfo I1 = new(name: "@intrinsic.i1",
            llvmType: "i1",
            bitSize: 1);

        // Integers
        /// <summary>
        /// Represents an 8-bit LLVM intrinsic type (e.g., "i8"). This type is typically used for representing integer values at the LLVM IR level.
        /// </summary>
        public static readonly IntrinsicTypeInfo I8 = new(name: "@intrinsic.i8",
            llvmType: "i8",
            bitSize: 8);

        /// <summary>
        /// Represents a 16-bit LLVM intrinsic type (e.g., "i16"). This type is typically used for representing integer values at the LLVM IR level.
        /// </summary>
        public static readonly IntrinsicTypeInfo I16 = new(name: "@intrinsic.i16",
            llvmType: "i16",
            bitSize: 16);

        /// <summary>
        /// Represents a 32-bit LLVM intrinsic type (e.g., "i32"). This type is typically used for representing integer values at the LLVM IR level.
        /// </summary>
        public static readonly IntrinsicTypeInfo I32 = new(name: "@intrinsic.i32",
            llvmType: "i32",
            bitSize: 32);

        /// <summary>
        /// Represents a 64-bit LLVM intrinsic type (e.g., "i64"). This type is typically used for representing integer values at the LLVM IR level.
        /// </summary>
        public static readonly IntrinsicTypeInfo I64 = new(name: "@intrinsic.i64",
            llvmType: "i64",
            bitSize: 64);

        /// <summary>
        /// Represents a 128-bit LLVM intrinsic type (e.g., "i128"). This type is typically used for representing integer values at the LLVM IR level.
        /// </summary>
        public static readonly IntrinsicTypeInfo I128 = new(name: "@intrinsic.i128",
            llvmType: "i128",
            bitSize: 128);

        // Pointer-sized integers
        /// <summary>
        /// Represents a pointer-sized integer type (e.g., "iptr").
        /// </summary>
        public static readonly IntrinsicTypeInfo Iptr = new(name: "@intrinsic.iptr",
            llvmType: "iptr",
            bitSize: 0,
            isPointerSized: true);

        /// <summary>
        /// Represents an unsigned pointer-sized integer type (e.g., "uptr").
        /// </summary>
        public static readonly IntrinsicTypeInfo Uptr = new(name: "@intrinsic.uptr",
            llvmType: "uptr",
            bitSize: 0,
            isPointerSized: true);

        // Floating point
        /// <summary>
        /// Represents a 16-bit floating-point type (e.g., "half").
        /// </summary>
        public static readonly IntrinsicTypeInfo F16 = new(name: "@intrinsic.f16",
            llvmType: "half",
            bitSize: 16,
            isFloatingPoint: true);

        /// <summary>
        /// Represents a 32-bit floating-point type (e.g., "float").
        /// </summary>
        public static readonly IntrinsicTypeInfo F32 = new(name: "@intrinsic.f32",
            llvmType: "float",
            bitSize: 32,
            isFloatingPoint: true);

        /// <summary>
        /// Represents a 64-bit floating-point type (e.g., "double").
        /// </summary>
        public static readonly IntrinsicTypeInfo F64 = new(name: "@intrinsic.f64",
            llvmType: "double",
            bitSize: 64,
            isFloatingPoint: true);

        /// <summary>
        /// Represents a 128-bit floating-point type (e.g., "fp128").
        /// </summary>
        public static readonly IntrinsicTypeInfo F128 = new(name: "@intrinsic.f128",
            llvmType: "fp128",
            bitSize: 128,
            isFloatingPoint: true);

        // Raw pointer
        /// <summary>
        /// Represents a raw pointer type (e.g., "ptr").
        /// </summary>
        public static readonly IntrinsicTypeInfo Ptr = new(name: "@intrinsic.ptr",
            llvmType: "ptr",
            bitSize: 0,
            isPointerSized: true);

        /// <summary>All well-known intrinsic types.</summary>
        public static IEnumerable<IntrinsicTypeInfo> All =>
        [
            I1,
            I8,
            I16,
            I32,
            I64,
            I128,
            Iptr,
            Uptr,
            F16,
            F32,
            F64,
            F128,
            Ptr
        ];
    }
}
