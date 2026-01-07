namespace Compilers.Analysis.Enums;

/// <summary>
/// The kind of scope in the program structure.
/// </summary>
public enum ScopeKind
{
    /// <summary>Global/module level scope.</summary>
    Global,

    /// <summary>Namespace scope.</summary>
    Namespace,

    /// <summary>Type body scope (record, entity, etc.).</summary>
    Type,

    /// <summary>Function/routine body scope.</summary>
    Function,

    /// <summary>Block scope (if, while, for, etc.).</summary>
    Block,

    /// <summary>Danger block scope (RazorForge only).</summary>
    Danger,

    /// <summary>Viewing block scope - read-only token access.</summary>
    Viewing,

    /// <summary>Hijacking block scope - exclusive mutable access.</summary>
    Hijacking,

    /// <summary>Inspecting block scope - read lock for multi-threaded.</summary>
    Inspecting,

    /// <summary>Seizing block scope - write lock for multi-threaded.</summary>
    Seizing,

    /// <summary>Iteration scope - tracks active iterators for migratable checking.</summary>
    Iteration
}
