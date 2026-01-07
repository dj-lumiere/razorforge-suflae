# Semantic Analysis & Code Generation Flowchart

## Overview

```
Source Code (.rf/.sf)
        │
        ▼
┌───────────────┐
│  TOKENIZER    │
└───────────────┘
        │
        ▼
    [Tokens]
        │
        ▼
┌───────────────┐
│    PARSER     │
└───────────────┘
        │
        ▼
      [AST]
        │
        ▼
┌───────────────┐
│   SEMANTIC    │
│   ANALYZER    │
└───────────────┘
        │
        ▼
 [Typed AST + Registry]
        │
        ▼
┌───────────────┐
│  LLVM CODEGEN │
└───────────────┘
        │
        ▼
   [LLVM IR (.ll)]
```

---

## Semantic Analyzer Phases

```
                              ┌─────────────────────────────────┐
                              │     TypeRegistry Created        │
                              │  ┌───────────────────────────┐  │
                              │  │ 1. RegisterIntrinsics()   │  │
                              │  │    @intrinsic.i1, i8...   │  │
                              │  ├───────────────────────────┤  │
                              │  │ 2. LoadCoreNamespace()    │  │
                              │  │    ├─ Parse NativeDataTypes│ │
                              │  │    ├─ Parse Core/          │  │
                              │  │    └─ Register types+routines│
                              │  ├───────────────────────────┤  │
                              │  │ 3. RegisterErrorHandling()│  │
                              │  │    Maybe, Result, Lookup   │  │
                              │  └───────────────────────────┘  │
                              └─────────────────────────────────┘
                                            │
                                            ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                         SemanticAnalyzer.Analyze(AST)                        │
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ PHASE 1: Import Resolution (Namespace-Based)                        │    │
│  │                                                                      │    │
│  │   for each ImportDeclaration:                                        │    │
│  │       ┌──────────────────────────────────────────────────────┐      │    │
│  │       │ import Collections.List   (dot = namespace separator) │      │    │
│  │       │    │                                                  │      │    │
│  │       │    ▼                                                  │      │    │
│  │       │ TypeRegistry.LoadModule("Collections.List")          │      │    │
│  │       │    │                                                  │      │    │
│  │       │    ├─► ModuleResolver finds stdlib/Collections/List.rf│     │    │
│  │       │    ├─► StdlibLoader parses and registers types       │      │    │
│  │       │    ├─► Uses file's namespace declaration              │      │    │
│  │       │    └─► Recursively loads transitive imports          │      │    │
│  │       └──────────────────────────────────────────────────────┘      │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                    │                                         │
│                                    ▼                                         │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ PHASE 2: Declaration Registration                                    │    │
│  │                                                                      │    │
│  │   for each Declaration in AST:                                       │    │
│  │       ┌────────────────────────────────────────────────┐            │    │
│  │       │ RecordDeclaration  → RegisterRecordType()      │            │    │
│  │       │ EntityDeclaration  → RegisterEntityType()      │            │    │
│  │       │ ChoiceDeclaration  → RegisterChoiceType()      │            │    │
│  │       │ VariantDeclaration → RegisterVariantType()     │            │    │
│  │       │ ProtocolDeclaration→ RegisterProtocolType()    │            │    │
│  │       │ RoutineDeclaration → RegisterRoutine()         │            │    │
│  │       └────────────────────────────────────────────────┘            │    │
│  │                                                                      │    │
│  │   Result: All type names and routine signatures known                │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                    │                                         │
│                                    ▼                                         │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ PHASE 3: Body Analysis                                               │    │
│  │                                                                      │    │
│  │   for each RoutineDeclaration:                                       │    │
│  │       ┌────────────────────────────────────────────────────────┐    │    │
│  │       │ 1. EnterScope(Function)                                 │    │    │
│  │       │ 2. Declare parameters in scope                          │    │    │
│  │       │ 3. AnalyzeStatement(body)                               │    │    │
│  │       │    │                                                    │    │    │
│  │       │    ├─► VariableDeclaration: resolve type, declare var   │    │    │
│  │       │    ├─► Assignment: check target, check type match       │    │    │
│  │       │    ├─► IfStatement: check condition is Bool             │    │    │
│  │       │    ├─► ReturnStatement: check type matches return type  │    │    │
│  │       │    ├─► Expression: resolve types, check operators       │    │    │
│  │       │    └─► ... etc                                          │    │    │
│  │       │ 4. ExitScope()                                          │    │    │
│  │       └────────────────────────────────────────────────────────┘    │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                    │                                         │
│                                    ▼                                         │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ PHASE 4: Protocol Conformance Check (Partial)                        │    │
│  │                                                                      │    │
│  │   for each type implementing a protocol:                             │    │
│  │       Check all required methods are implemented                     │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                    │                                         │
│                                    ▼                                         │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ PHASE 5: Error Handling Variant Generation (Partial)                 │    │
│  │                                                                      │    │
│  │   for each failable routine (name!):                                 │    │
│  │       Generate try_name() → T?                                       │    │
│  │       Generate check_name() → Result<T>                              │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                              │
└──────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
                        ┌───────────────────┐
                        │  AnalysisResult   │
                        │  ├─ Registry      │
                        │  ├─ Errors[]      │
                        │  └─ Warnings[]    │
                        └───────────────────┘
```

---

## Type Registry Structure

```
┌─────────────────────────────────────────────────────────────────┐
│                        TypeRegistry                              │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  _types: Dictionary<string, TypeInfo>                           │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ "Core.S32"     → RecordTypeInfo { Name="S32", ... }     │    │
│  │ "Core.Bool"    → RecordTypeInfo { Name="Bool", ... }    │    │
│  │ "Core.Blank"   → RecordTypeInfo { Name="Blank", ... }   │    │
│  │ "MyApp.Point"  → RecordTypeInfo { Name="Point", ... }   │    │
│  └─────────────────────────────────────────────────────────┘    │
│                                                                  │
│  _intrinsics: Dictionary<string, IntrinsicTypeInfo>             │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ "@intrinsic.i1"   → i1 (1-bit boolean)                  │    │
│  │ "@intrinsic.i32"  → i32 (32-bit integer)                │    │
│  │ "@intrinsic.f64"  → f64 (64-bit float)                  │    │
│  └─────────────────────────────────────────────────────────┘    │
│                                                                  │
│  _routines: Dictionary<string, RoutineInfo>                     │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ "start"           → RoutineInfo { Params=[], Ret=Blank }│    │
│  │ "factorial"       → RoutineInfo { Params=[n:S64], Ret=S64}│  │
│  └─────────────────────────────────────────────────────────┘    │
│                                                                  │
│  _routinesByOwner: Dictionary<string, List<RoutineInfo>>        │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ "S32" → [__add__, __sub__, __mul__, __eq__, abs, ...]   │    │
│  │ "S64" → [__add__, __sub__, __mul__, __eq__, abs, ...]   │    │
│  │ "Bool"→ [__eq__, __ne__, to_text, ...]                  │    │
│  └─────────────────────────────────────────────────────────┘    │
│                                                                  │
│  _scopes: Stack<Scope>  (for variable lookup during analysis)   │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ [Global] → [Function:start] → [Block:if] → ...          │    │
│  └─────────────────────────────────────────────────────────┘    │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

---

## Type Lookup Flow

```
LookupType("S32")
       │
       ▼
┌──────────────────────┐     Found?     ┌─────────────────┐
│ Check _types["S32"]  │ ───────────────►│ Return TypeInfo │
└──────────────────────┘       Yes       └─────────────────┘
       │ No
       ▼
┌──────────────────────┐     Found?     ┌─────────────────┐
│ Check _intrinsics    │ ───────────────►│ Return Intrinsic│
│ ["S32"]              │       Yes       └─────────────────┘
└──────────────────────┘
       │ No
       ▼
┌──────────────────────┐     Found?     ┌─────────────────┐
│ Check _instantiations│ ───────────────►│ Return Instance │
│ ["S32"]              │       Yes       └─────────────────┘
└──────────────────────┘
       │ No
       ▼
┌──────────────────────┐     Found?     ┌─────────────────┐
│ Check _types         │ ───────────────►│ Return TypeInfo │
│ ["Core.S32"]         │       Yes       └─────────────────┘
└──────────────────────┘
       │ No
       ▼
┌──────────────────────┐
│ Return null          │
└──────────────────────┘
```

---

## Code Generation Flow

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                      LLVMCodeGenerator.Generate()                            │
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ PHASE 1: Type Declarations                                           │    │
│  │                                                                      │    │
│  │   for each type in Registry:                                         │    │
│  │       Entity  → %Entity.Name = type { field1, field2, ... }         │    │
│  │       Record  → %Record.Name = type { field1, field2, ... }         │    │
│  │       Choice  → %Choice.Name = type { i32 }  ; enum tag             │    │
│  │       Variant → %Variant.Name = type { i8, [max_size x i8] }        │    │
│  │                                                                      │    │
│  │   Output:                                                            │    │
│  │       %Record.Blank = type { }                                       │    │
│  │       %Record.S32 = type { i32 }  ; single-field wrapper            │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                    │                                         │
│                                    ▼                                         │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ PHASE 2: Function Declarations                                       │    │
│  │                                                                      │    │
│  │   for each routine in Registry (skip error types):                   │    │
│  │       declare ReturnType @FunctionName(ParamTypes...)               │    │
│  │                                                                      │    │
│  │   Output:                                                            │    │
│  │       declare i64 @S64.__add__(i64, i64)                            │    │
│  │       declare void @start()                                          │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                    │                                         │
│                                    ▼                                         │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ PHASE 3: Function Definitions                                        │    │
│  │                                                                      │    │
│  │   for each RoutineDeclaration in AST:                                │    │
│  │       ┌────────────────────────────────────────────────────────┐    │    │
│  │       │ define ReturnType @FunctionName(params) {              │    │    │
│  │       │ entry:                                                  │    │    │
│  │       │     ; Allocate locals                                   │    │    │
│  │       │     %x.addr = alloca i64                                │    │    │
│  │       │                                                         │    │    │
│  │       │     ; Generate body                                     │    │    │
│  │       │     EmitStatement(body)                                 │    │    │
│  │       │         ├─► VariableDecl: alloca + store                │    │    │
│  │       │         ├─► Assignment: load + compute + store          │    │    │
│  │       │         ├─► If: br i1 %cond, then, else                 │    │    │
│  │       │         ├─► Return: ret Type %value                     │    │    │
│  │       │         └─► Call: %result = call @func(args)            │    │    │
│  │       │                                                         │    │    │
│  │       │     ret void  ; or ret Type %value                      │    │    │
│  │       │ }                                                       │    │    │
│  │       └────────────────────────────────────────────────────────┘    │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                    │                                         │
│                                    ▼                                         │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ PHASE 4: Runtime Support                                             │    │
│  │                                                                      │    │
│  │   declare ptr @rf_alloc(i64)           ; heap allocation            │    │
│  │   declare void @rf_free(ptr)           ; deallocation               │    │
│  │   declare void @rf_console_print(...)  ; I/O                        │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                              │
└──────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
                          ┌─────────────────┐
                          │  output.ll      │
                          │  (LLVM IR text) │
                          └─────────────────┘
```

---

## Import Resolution (Implemented)

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                     On-Demand Module Loading                                 │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│   import Collections.List    (namespace.type syntax)                         │
│          │                                                                   │
│          ▼                                                                   │
│   ┌─────────────────────────────────────────────────────────────────┐       │
│   │ 1. Check if "Collections.List" already loaded                    │       │
│   │    └─► If yes, skip                                              │       │
│   └─────────────────────────────────────────────────────────────────┘       │
│          │ No                                                                │
│          ▼                                                                   │
│   ┌─────────────────────────────────────────────────────────────────┐       │
│   │ 2. ModuleResolver: convert dots to path separators               │       │
│   │    "Collections.List" → "Collections/List"                       │       │
│   │    Dynamically detect stdlib directories                         │       │
│   │    Resolve to: stdlib/Collections/List.rf                        │       │
│   └─────────────────────────────────────────────────────────────────┘       │
│          │                                                                   │
│          ▼                                                                   │
│   ┌─────────────────────────────────────────────────────────────────┐       │
│   │ 3. StdlibLoader parses the file                                  │       │
│   │    └─► Handle recursive imports in that file                     │       │
│   └─────────────────────────────────────────────────────────────────┘       │
│          │                                                                   │
│          ▼                                                                   │
│   ┌─────────────────────────────────────────────────────────────────┐       │
│   │ 4. Use file's namespace declaration for registration             │       │
│   │    (e.g., "namespace Collections" in List.rf)                    │       │
│   └─────────────────────────────────────────────────────────────────┘       │
│          │                                                                   │
│          ▼                                                                   │
│   ┌─────────────────────────────────────────────────────────────────┐       │
│   │ 5. Register types and routines under that namespace              │       │
│   └─────────────────────────────────────────────────────────────────┘       │
│          │                                                                   │
│          ▼                                                                   │
│   ┌─────────────────────────────────────────────────────────────────┐       │
│   │ 6. Mark module as loaded (prevent re-loading)                    │       │
│   └─────────────────────────────────────────────────────────────────┘       │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘

Module Dependencies Example:
────────────────────────────

    user_code.rf
         │
         │ import Collections.List
         ▼
    Collections/List.rf
         │
         │ import Core (implicit - loaded at startup)
         │ import memory.DynamicSlice
         │ import errors.IndexOutOfBoundsError
         ▼
    memory/DynamicSlice.rf
         │
         │ import Core (implicit)
         ▼
    (already loaded)
```

---

## Module Loading Summary

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           MODULE LOADING                                     │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  LOADED AT STARTUP (Eager):                                                  │
│  ├─ stdlib/NativeDataTypes/*.rf                                             │
│  │   S8, S16, S32, S64, S128                                                │
│  │   U8, U16, U32, U64, U128                                                │
│  │   F16, F32, F64, F128                                                    │
│  │   D32, D64, D128                                                         │
│  │   Bool, SAddr, UAddr                                                     │
│  └─ stdlib/Core/*.rf                                                        │
│      Blank, ComparisonSign, None                                            │
│                                                                              │
│  LOADED ON-DEMAND (Lazy):                                                    │
│  ├─ import Collections.List    → stdlib/Collections/List.rf                 │
│  ├─ import memory.DynamicSlice → stdlib/memory/DynamicSlice.rf              │
│  ├─ import errors.IndexOutOfBoundsError → stdlib/errors/...                 │
│  ├─ import Text.Text           → stdlib/Text/Text.rf                        │
│  └─ ... any module via namespace.type syntax                                │
│                                                                              │
│  IMPORT SYNTAX:                                                              │
│  ├─ import Namespace.Type      (loads single module)                        │
│  ├─ Transitive imports are loaded automatically                             │
│  └─ Circular imports prevented by module tracking                           │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```