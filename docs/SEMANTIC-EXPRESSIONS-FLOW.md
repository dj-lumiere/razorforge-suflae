# Semantic Analyzer Architecture

## Overview

The semantic analyzer transforms a raw AST into a **fully-typed AST** with all symbols resolved. It runs in multiple
phases and builds a `TypeRegistry` that the code generator can query.

```
Source → Lexer → Parser → AST
                           ↓
                  ┌────────────────────┐
                  │  Semantic Analyzer │
                  │  ┌──────────────┐  │
                  │  │ TypeRegistry │  │
                  │  │ • Types      │  │
                  │  │ • Routines   │  │
                  │  │ • Scopes     │  │
                  │  └──────────────┘  │
                  └────────────────────┘
                           ↓
                     Typed AST + TypeRegistry
                           ↓
                     Code Generator
```

---

## Key Data Structures

### TypeRegistry

Central repository for all type information. The code generator queries this instead of guessing.

```
TypeRegistry
├── _types: Dictionary<string, TypeInfo>         # All types by name
├── _intrinsics: Dictionary<string, IntrinsicTypeInfo>  # LLVM intrinsic types
├── _instantiations: Dictionary<string, TypeInfo>       # Generic instantiations cache
├── _routines: Dictionary<string, RoutineInfo>          # All routines by full name
├── _routinesByOwner: Dictionary<string, List<RoutineInfo>>  # Methods by type
├── _routineInstantiations: Dictionary<string, RoutineInfo>  # Generic routine cache
└── GlobalScope: Scope                           # Root of scope hierarchy
```

### TypeInfo Hierarchy

```
TypeInfo (abstract base)
├── TypeCategory: enum (Record, Entity, Resident, Choice, Variant, Protocol, Intrinsic, Error, TypeParameter)
├── Name: string
├── IsGeneric: bool
├── GenericParameters: List<string>
│
├── RecordTypeInfo      # Value types (copy on assign)
│   ├── Fields: List<FieldInfo>
│   ├── IsSingleField: bool     # True for wrappers like s32, bool
│   └── LlvmType: string        # Direct LLVM type if single-field
│
├── EntityTypeInfo      # Reference types (heap-allocated)
│   └── Fields: List<FieldInfo>
│
├── ResidentTypeInfo    # Fixed-size reference (RazorForge only)
│   └── Fields: List<FieldInfo>
│
├── ChoiceTypeInfo      # Enumerations (CAN have methods)
│   └── Cases: List<ChoiceCaseInfo>
│
├── VariantTypeInfo     # Tagged unions (NO methods, local-only)
│   └── Cases: List<VariantCaseInfo>
│
├── ProtocolTypeInfo    # Interfaces/traits
│   └── Methods: List<ProtocolMethodInfo>
│
├── IntrinsicTypeInfo   # LLVM native types (i8, i32, f64, ptr)
│   └── LlvmType: string
│
├── ErrorTypeInfo       # Placeholder for unresolved types
│
└── GenericParameterTypeInfo  # Type parameter (T in List<T>)
```

### Scope Hierarchy

```
Scope
├── Kind: ScopeKind (Global, Module, Type, Function, Block)
├── Parent: Scope?
├── Variables: Dictionary<string, VariableInfo>
├── Types: Dictionary<string, TypeInfo>
└── Routines: Dictionary<string, RoutineInfo>

# Example scope chain:
GlobalScope
└── ModuleScope ("stdlib/Collections")
    └── TypeScope ("List<T>")
        └── FunctionScope ("push")
            └── BlockScope (if body)
```

---

## Analysis Phases

### Phase 1: Declaration Collection (First Pass)

Collect all type and routine declarations **without** analyzing bodies.

```
For each declaration in AST:
    │
    ├── RecordDeclaration ──► RegisterRecordType(name, fields, generics)
    ├── EntityDeclaration ──► RegisterEntityType(name, fields, generics)
    ├── ResidentDeclaration ► RegisterResidentType(name, fields, generics)
    ├── ChoiceDeclaration ──► RegisterChoiceType(name, cases)
    ├── VariantDeclaration ─► RegisterVariantType(name, cases)
    ├── ProtocolDeclaration ► RegisterProtocolType(name, methods)
    ├── RoutineDeclaration ─► RegisterRoutine(name, params, returnType)
    ├── PresetDeclaration ──► RegisterConstant(name, type, value)
    └── ImportDeclaration ──► LoadModule(path) → merge into registry
```

**Why two passes?** Types can reference each other:

```razorforge
record Node {
    value: s32
    next: Maybe<Node>  # Node not yet defined if single-pass!
}
```

### Phase 2: Type Resolution

Resolve all type references to actual `TypeInfo` objects.

```
ResolveTypeExpression(typeExpr)
    │
    ├── SimpleType ("s32") ──► LookupType("s32") → RecordTypeInfo
    │
    ├── GenericType ("List<s32>")
    │   ├── LookupType("List") → generic EntityTypeInfo
    │   ├── ResolveTypeExpression("s32") → RecordTypeInfo
    │   └── InstantiateGeneric("List", [s32]) → EntityTypeInfo
    │
    ├── MaybeType ("T?")
    │   ├── ResolveTypeExpression(T)
    │   └── WrapInMaybe(T) → VariantTypeInfo
    │
    └── FunctionType ("(s32, s32) -> s32")
        └── Create callable type
```

### Phase 3: Statement Analysis

Analyze each statement in routine bodies.

```
AnalyzeStatement(stmt)
    │
    ├── VariableDeclaration
    │   ├── AnalyzeExpression(initializer) → TypeInfo
    │   ├── If explicit type: ValidateAssignment(declaredType, exprType)
    │   └── RegisterVariable(name, type) in current scope
    │
    ├── AssignmentStatement
    │   ├── LookupVariable(target) → VariableInfo
    │   ├── Check: variable.IsMutable?
    │   ├── AnalyzeExpression(value) → TypeInfo
    │   └── ValidateAssignment(varType, valueType)
    │
    ├── IfStatement / WhenStatement
    │   ├── AnalyzeExpression(condition) → must be bool
    │   ├── PushScope(BlockScope)
    │   ├── AnalyzeStatements(body)
    │   └── PopScope()
    │
    ├── ForStatement
    │   ├── AnalyzeExpression(iterable) → must be Iterable<T>
    │   ├── PushScope(BlockScope)
    │   ├── RegisterVariable(loopVar, T)
    │   ├── AnalyzeStatements(body)
    │   └── PopScope()
    │
    ├── ReturnStatement
    │   ├── AnalyzeExpression(value) → TypeInfo
    │   └── ValidateAssignment(routineReturnType, valueType)
    │
    ├── ThrowStatement (in ! functions only)
    │   ├── AnalyzeExpression(error) → must implement Crashable
    │   └── Mark routine as throwing
    │
    ├── AbsentStatement (in ! functions only)
    │   └── Mark routine as returning None
    │
    └── Token blocks (ViewingStatement, HijackingStatement, etc.)
        ├── AnalyzeExpression(target) → must be entity
        ├── Check token access rules
        ├── PushScope with token variable
        ├── AnalyzeStatements(body)
        └── PopScope()
```

### Phase 4: Expression Analysis

Analyze expressions and determine their types.

```
AnalyzeExpression(expr) → TypeInfo
    │
    ├── LiteralExpression
    │   ├── IntegerLiteral ──► s32 (or infer from suffix/context)
    │   ├── FloatLiteral ────► f64 (or infer)
    │   ├── TextLiteral ─────► Text
    │   ├── BytesLiteral ────► Bytes
    │   ├── LetterLiteral ───► letter
    │   ├── ByteLiteral ─────► byte
    │   └── BoolLiteral ─────► bool
    │
    ├── IdentifierExpression
    │   ├── "me" ──► _currentType (error if not in method)
    │   ├── "None" ► Maybe<T> definition
    │   ├── LookupVariable(name) in scope chain
    │   ├── LookupRoutine(name)
    │   └── LookupType(name) → type reference
    │
    ├── CallExpression (includes desugared operators!)
    │   ├── Resolve callee (function, method, or constructor)
    │   ├── Analyze arguments
    │   ├── Match parameters (including generics)
    │   └── Return routine's return type
    │
    ├── MemberAccessExpression (a.b)
    │   ├── AnalyzeExpression(object) → TypeInfo
    │   ├── LookupField(type, fieldName)
    │   └── Return field type
    │
    ├── IndexAccessExpression (a[i])
    │   ├── Desugared to a.__getitem__(i) by parser
    │   └── Analyze as CallExpression
    │
    ├── ConstructorExpression (TypeName(field: value, ...))
    │   ├── LookupType(typeName) → TypeInfo
    │   ├── Validate all required fields provided
    │   ├── Analyze each field value
    │   └── Return the constructed type
    │
    ├── LambdaExpression
    │   ├── Create FunctionScope
    │   ├── Register parameters
    │   ├── Analyze body
    │   └── Return callable type
    │
    └── BinaryExpression (only for language-level ops: and/or/??)
        ├── "and" / "or": both sides must be bool, returns bool
        └── "??": LHS is Maybe<T>, RHS is T, returns T
```

---

## Operator Desugaring

Operators are desugared to method calls **in the parser**, not the semantic analyzer.

```
# Source:
a + b

# Parser outputs:
CallExpression {
    Callee: MemberAccessExpression { Object: a, Member: "__add__" }
    Arguments: [b]
}

# Semantic analyzer just sees a normal method call!
```

### Operator → Method Mapping

| Operator   | Method                 | Notes           |
|------------|------------------------|-----------------|
| `a + b`    | `a.__add__(b)`         |                 |
| `a - b`    | `a.__sub__(b)`         |                 |
| `a * b`    | `a.__mul__(b)`         |                 |
| `a / b`    | `a.__truediv__(b)`     | True division   |
| `a // b`   | `a.__floordiv__(b)`    | Floor division  |
| `a % b`    | `a.__mod__(b)`         |                 |
| `a ** b`   | `a.__pow__(b)`         |                 |
| `a +% b`   | `a.__add_wrap__(b)`    | Wrapping        |
| `a +^ b`   | `a.__add_sat__(b)`     | Saturating      |
| `a +? b`   | `a.__add_checked__(b)` | Checked (Maybe) |
| `a == b`   | `a.__eq__(b)`          |                 |
| `a != b`   | `a.__ne__(b)`          |                 |
| `a < b`    | `a.__lt__(b)`          |                 |
| `a <= b`   | `a.__le__(b)`          |                 |
| `a > b`    | `a.__gt__(b)`          |                 |
| `a >= b`   | `a.__ge__(b)`          |                 |
| `a <=> b`  | `a.__cmp__(b)`         | Three-way       |
| `a & b`    | `a.__and__(b)`         | Bitwise         |
| `a \| b`   | `a.__or__(b)`          | Bitwise         |
| `a ^ b`    | `a.__xor__(b)`         | Bitwise         |
| `~a`       | `a.__not__()`          | Bitwise not     |
| `a << b`   | `a.__lshift__(b)`      |                 |
| `a >> b`   | `a.__rshift__(b)`      |                 |
| `a[i]`     | `a.__getitem__(i)`     |                 |
| `a[i] = v` | `a.__setitem__(i, v)`  |                 |
| `-a`       | `a.__neg__()`          |                 |

### Language-Level Operators (Not Desugared)

These require special handling:

| Operator       | Reason                                  |
|----------------|-----------------------------------------|
| `and` / `or`   | Short-circuit evaluation                |
| `not`          | Logical not (distinct from bitwise `~`) |
| `is` / `isnot` | Pattern matching                        |
| `===` / `!==`  | Reference identity                      |
| `??`           | None coalescing                         |

---

## Mutation Inference (RazorForge)

After expression analysis, infer mutation categories for `me` parameter.

```
Phase 4: Build Call Graph
    │
    For each routine:
    ├── Create CallGraphNode
    └── Record all callees

Phase 5: Mark Direct Mutations
    │
    For each routine:
    ├── Has assignment to me.field? → Mark MUTATES
    └── Has DynamicSlice operation? → Mark MIGRATABLE

Phase 6: Propagate (Fixpoint)
    │
    Repeat until no changes:
    └── If routine calls MUTATING method → Mark as MUTATING

Phase 7: Validate
    │
    For each routine:
    └── Declared mutation ≥ Inferred mutation? → OK
        Otherwise → Error
```

### Token Access Rules

| Token Type     | Read | Write | Store |
|----------------|------|-------|-------|
| `Viewed<T>`    | ✅    | ❌     | ❌     |
| `Hijacked<T>`  | ✅    | ✅     | ❌     |
| `Inspected<T>` | ✅    | ❌     | ❌     |
| `Seized<T>`    | ✅    | ✅     | ❌     |

---

## Error Handling Variant Generation

Automatically generate safe variants for `!` functions.

```
Analyze ! function:
    │
    ├── Has `throw` only?
    │   └── Generate: try_foo() → Maybe<T>
    │                 check_foo() → Result<T>
    │
    ├── Has `absent` only?
    │   └── Generate: try_foo() → Maybe<T>
    │
    └── Has both `throw` and `absent`?
        └── Generate: try_foo() → Maybe<T>
                      find_foo() → Lookup<T>
```

---

## Generic Instantiation

When a generic type/routine is used with concrete arguments:

```
InstantiateGenericType("List", [s32])
    │
    ├── Check cache: _instantiations["List_s32"]?
    │   └── Found → return cached
    │
    ├── Clone generic TypeInfo
    ├── Substitute T → s32 in all fields
    ├── Cache as "List_s32"
    └── Return instantiated TypeInfo


InstantiateGenericRoutine("List<T>.push", [s32])
    │
    ├── Check cache: _routineInstantiations["List_s32.push"]?
    │   └── Found → return cached
    │
    ├── Clone generic RoutineInfo
    ├── Substitute T → s32 in params and return type
    ├── Cache as "List_s32.push"
    └── Return instantiated RoutineInfo
```

---

## File Organization

```
src/Analysis/
├── SemanticAnalyzer.cs              # Main entry point, Analyze() method
├── SemanticAnalyzer.Declarations.cs # Phase 1 & 2: declaration collection
├── SemanticAnalyzer.TypeResolution.cs # Type expression resolution
├── SemanticAnalyzer.Statements.cs   # Statement analysis
├── SemanticAnalyzer.Expressions.cs  # Expression analysis
├── SemanticAnalyzer.Patterns.cs     # Pattern matching analysis
├── SemanticAnalyzer.Helpers.cs      # Utility methods
├── SemanticAnalyzer.Mutation.cs     # Mutation inference integration
├── SemanticAnalyzer.ErrorHandling.cs # Error variant generation
├── TypeRegistry.cs                  # Central type repository
│
├── Enums/
│   ├── Language.cs          # RazorForge vs Suflae
│   ├── TypeCategory.cs      # Record, Entity, Choice, etc.
│   ├── MutationCategory.cs  # Readonly, Writable, Migratable
│   ├── ScopeKind.cs         # Global, Module, Type, Function, Block
│   ├── RoutineKind.cs       # Function, Method, Constructor
│   ├── ErrorHandlingKind.cs # Maybe, Result, Lookup
│   └── VariantKind.cs       # Try, Check, Find
│
├── Types/
│   ├── TypeInfo.cs          # Base class
│   ├── RecordTypeInfo.cs
│   ├── EntityTypeInfo.cs
│   ├── ResidentTypeInfo.cs
│   ├── ChoiceTypeInfo.cs
│   ├── VariantTypeInfo.cs
│   ├── ProtocolTypeInfo.cs
│   ├── IntrinsicTypeInfo.cs
│   ├── ErrorTypeInfo.cs
│   └── GenericParameterTypeInfo.cs
│
├── Symbols/
│   ├── RoutineInfo.cs       # Function/method metadata
│   ├── VariableInfo.cs      # Variable metadata
│   ├── FieldInfo.cs         # Field metadata
│   └── ParameterInfo.cs     # Parameter metadata
│
├── Scopes/
│   └── Scope.cs             # Scope hierarchy
│
├── Inference/
│   ├── MutationInference.cs
│   ├── MigratableInference.cs
│   ├── CallGraph.cs
│   ├── CallGraphNode.cs
│   ├── CallEdge.cs
│   └── ErrorHandlingGenerator.cs
│
└── Results/
    ├── AnalysisResult.cs    # Analysis output container
    ├── SemanticError.cs
    └── SemanticWarning.cs
```

---

## What the Code Generator Receives

After semantic analysis, the code generator has access to:

1. **TypeRegistry** with all types resolved and instantiated
2. **Every expression has `.ResolvedType`** - no guessing
3. **Every identifier is resolved** to a VariableInfo, RoutineInfo, or TypeInfo
4. **Generic types are instantiated** - `List<s32>` exists as a concrete type
5. **Mutation categories are known** - which methods mutate `me`
6. **Error handling variants are generated** - `try_`, `check_`, `find_` functions exist

The code generator just translates - it never needs to infer types.
