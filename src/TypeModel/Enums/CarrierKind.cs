namespace TypeModel.Enums;

/// <summary>
/// Whether a record type is a compiler-known error-handling carrier (Maybe, Result, Lookup).
/// </summary>
public enum CarrierKind
{
    /// <summary>Not a carrier type — ordinary record.</summary>
    None = 0,

    /// <summary>Maybe[T] — optional value (None or value).</summary>
    Maybe,

    /// <summary>Result[T] — error or value.</summary>
    Result,

    /// <summary>Lookup[T] — three-way: error, absent, or value.</summary>
    Lookup
}