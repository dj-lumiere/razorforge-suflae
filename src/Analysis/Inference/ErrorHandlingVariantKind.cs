namespace SemanticAnalysis.Inference;

/// <summary>
/// Kind of generated error handling variant.
/// </summary>
public enum ErrorHandlingVariantKind
{
    /// <summary>try_ variant - returns Maybe&lt;T&gt;, errors become None.</summary>
    Try,

    /// <summary>check_ variant - returns Result&lt;T&gt;, preserves error info.</summary>
    Check,

    /// <summary>lookup_ variant - returns Lookup&lt;T&gt;, distinguishes error from absence.</summary>
    Lookup
}
