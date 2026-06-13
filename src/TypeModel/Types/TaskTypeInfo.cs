using System.Collections.Generic;
using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Type information for an in-flight threaded task handle, <c>Task[T]</c>.
///
/// Produced by calling a <c>threaded routine foo(...) -&gt; T</c> (which spawns an OS
/// thread and yields the handle). The handle is a single opaque <c>rf_task*</c> pointer,
/// so it lowers to LLVM <c>ptr</c>. Phase 1 keeps it single-use: it flows from the spawn
/// call site into a local and then into <c>.waitfor()</c> / <c>.waitfor(Duration)</c>; it
/// is not storable in fields, returnable, or copyable. An unawaited handle is block-joined
/// at scope teardown via an intrinsic <c>$destroy</c>.
/// </summary>
public sealed class TaskTypeInfo : TypeInfo
{
    /// <summary>The completion value type <c>T</c> the task produces.</summary>
    public TypeInfo ValueType { get; }

    /// <summary>
    /// Creates a <c>Task[T]</c> handle type for the given completion value type.
    /// </summary>
    /// <param name="valueType">The type the threaded routine returns.</param>
    public TaskTypeInfo(TypeInfo valueType) : base(name: "Task")
    {
        ValueType = valueType;
        TypeArguments = [valueType];
    }

    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Wrapper;

    /// <inheritdoc/>
    public override TypeInfo CreateInstance(List<TypeInfo> typeArguments)
    {
        return new TaskTypeInfo(valueType: typeArguments[index: 0]);
    }
}
