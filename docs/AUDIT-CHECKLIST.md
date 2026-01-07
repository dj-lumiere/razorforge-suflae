# RazorForge Compiler Audit Checklist

This document helps you understand the compiler codebase systematically.

---

## How to Read the Code

### Recommended Order

1. **Start with data structures** (what does the compiler work with?)
    - `src/AST/` - What the parser produces
    - `src/Analysis/Types/` - How types are represented
    - `src/Analysis/Symbols/` - How functions/variables are tracked

2. **Then read the flow** (how does data move?)
    - `src/CLI/Program.cs` - Entry point
    - `src/Analysis/SemanticAnalyzer.cs` - Main orchestrator
    - `src/Analysis/TypeRegistry.cs` - Central storage

3. **Finally, read the details** (how are specific things handled?)
    - `SemanticAnalyzer.Declarations.cs` - Type/function registration
    - `SemanticAnalyzer.Expressions.cs` - Expression type inference
    - `SemanticAnalyzer.Statements.cs` - Statement validation

### Key Questions When Reading

For each file, ask:

1. **What data does it define?** (records, classes, enums)
2. **What data does it consume?** (parameters, dependencies)
3. **What data does it produce?** (return values, side effects)
4. **What invariants must hold?** (preconditions, postconditions)

---

## Phase 1: AST Layer Audit

### AST Node Types (`src/AST/`)

| File              | Contains                                   | Audit Status |
|-------------------|--------------------------------------------|--------------|
| `ASTNode.cs`      | `IAstNode`, `SourceLocation`, base classes | [ ]          |
| `Declarations.cs` | Type/function declarations                 | [x]          |
| `Expressions.cs`  | All expression node types                  | [x]          |
| `Statements.cs`   | All statement node types                   | [ ]          |
| `TypeInfo.cs`     | `TypeExpression` for type annotations      | [ ]          |

**Questions to answer:**

- [ ] What AST nodes exist for each language construct?
- [ ] How is source location tracked through the AST?
- [ ] What visitor pattern is used (`IAstVisitor<T>`)?
- [ ] Are there any AST nodes missing for planned features?

---

## Phase 2: Type System Audit

### Type Representations (`src/Analysis/Types/`)

| File                          | Purpose                            | Audit Status |
|-------------------------------|------------------------------------|--------------|
| `TypeInfo.cs`                 | Base class, `TypeCategory` enum    | [ ]          |
| `IntrinsicTypeInfo.cs`        | LLVM native types (`@intrinsic.*`) | [ ]          |
| `RecordTypeInfo.cs`           | Value types (records)              | [ ]          |
| `EntityTypeInfo.cs`           | Reference types (entities)         | [ ]          |
| `ResidentTypeInfo.cs`         | Static reference types             | [ ]          |
| `ChoiceTypeInfo.cs`           | Enumerations                       | [ ]          |
| `VariantTypeInfo.cs`          | Tagged unions                      | [ ]          |
| `ProtocolTypeInfo.cs`         | Interfaces/traits                  | [ ]          |
| `ErrorHandlingTypeInfo.cs`    | Maybe/Result/Lookup                | [ ]          |
| `ErrorTypeInfo.cs`            | Placeholder for errors             | [ ]          |
| `GenericParameterTypeInfo.cs` | Type parameters (`T`)              | [ ]          |

**Questions to answer:**

- [ ] How are generic types represented vs instantiated?
- [ ] How does `TypeCategory` enum map to language constructs?
- [ ] What is `IsSingleFieldWrapper` and why does it matter?
- [ ] How are implemented protocols tracked?

### Symbol Representations (`src/Analysis/Symbols/`)

| File               | Purpose                          | Audit Status |
|--------------------|----------------------------------|--------------|
| `RoutineInfo.cs`   | Functions, methods, constructors | [ ]          |
| `VariableInfo.cs`  | Local variables, parameters      | [ ]          |
| `FieldInfo.cs`     | Type fields                      | [ ]          |
| `ParameterInfo.cs` | Function parameters              | [ ]          |

**Questions to answer:**

- [ ] How is mutation category tracked (`MutationCategory`)?
- [ ] How are generic routines instantiated?
- [ ] What's the difference between `RoutineKind` values?

---

## Phase 3: Semantic Analyzer Audit

### Main Analyzer (`src/Analysis/SemanticAnalyzer*.cs`)

| File                                 | Purpose                               | Audit Status |
|--------------------------------------|---------------------------------------|--------------|
| `SemanticAnalyzer.cs`                | Entry point, fields, `Analyze()`      | [ ]          |
| `SemanticAnalyzer.Declarations.cs`   | Phase 1 & 2: collect types/routines   | [ ]          |
| `SemanticAnalyzer.Statements.cs`     | Analyze statements                    | [ ]          |
| `SemanticAnalyzer.Expressions.cs`    | Infer expression types, literal parsing | [ ]       |
| `SemanticAnalyzer.Patterns.cs`       | Pattern matching analysis             | [ ]          |
| `SemanticAnalyzer.TypeResolution.cs` | Resolve `TypeExpression` → `TypeInfo` | [ ]          |
| `SemanticAnalyzer.Helpers.cs`        | Utility methods                       | [ ]          |
| `SemanticAnalyzer.Mutation.cs`       | Mutation inference integration        | [ ]          |
| `SemanticAnalyzer.ErrorHandling.cs`  | Error variant generation              | [ ]          |

### Native Interop (`src/Analysis/Native/`)

| File                      | Purpose                                    | Audit Status |
|---------------------------|--------------------------------------------|--------------|
| `NumericLiteralParser.cs` | P/Invoke bindings for numeric parsing      | [ ]          |

### Results (`src/Analysis/Results/`)

| File               | Purpose                                        | Audit Status |
|--------------------|------------------------------------------------|--------------|
| `AnalysisResult.cs` | Analysis output with parsed literals          | [ ]          |
| `ParsedLiteral.cs`  | Parsed numeric literal representations        | [ ]          |
| `SemanticError.cs`  | Error representation                          | [ ]          |
| `SemanticWarning.cs`| Warning representation                        | [ ]          |

**Questions to answer:**

- [ ] What are the analysis phases and their order?
- [ ] How does scope management work (`EnterScope`/`ExitScope`)?
- [ ] How are errors reported (`ReportError`)?
- [ ] What state is tracked during analysis (`_currentRoutine`, `_currentType`)?

### Analysis Phases

```
Phase 1: Collect type declarations (names only)
    ↓
Phase 2: Resolve type bodies (fields, methods)
    ↓
Phase 3: Analyze routine bodies (statements, expressions)
    ↓
Phase 4: Mutation inference (propagate through call graph)
    ↓
Phase 5: Error handling variant generation
```

**For each phase, verify:**

- [ ] What data is collected?
- [ ] What validations are performed?
- [ ] What errors can be reported?

---

## Phase 4: Infrastructure Audit

### Type Registry (`src/Analysis/TypeRegistry.cs`)

**Questions to answer:**

- [ ] How are types registered and looked up?
- [ ] How are generic instantiations cached?
- [ ] How does scope management interact with type lookup?
- [ ] What language-specific validations exist?

### Scope Management (`src/Analysis/Scopes/Scope.cs`)

**Questions to answer:**

- [ ] What is the scope hierarchy?
- [ ] How are variables declared and looked up?
- [ ] How does shadowing work?

### Enums (`src/Analysis/Enums/`)

| File                   | Values                                                | Audit Status |
|------------------------|-------------------------------------------------------|--------------|
| `Language.cs`          | RazorForge, Suflae                                    | [ ]          |
| `TypeCategory.cs`      | Error, TypeParameter, Intrinsic, Record, Entity, etc. | [ ]          |
| `MutationCategory.cs`  | Readonly, Writable, Migratable                        | [ ]          |
| `ScopeKind.cs`         | Global, Module, Type, Function, Block                 | [ ]          |
| `RoutineKind.cs`       | Function, Method, Constructor, Extension              | [ ]          |
| `ErrorHandlingKind.cs` | Maybe, Result, Lookup                                 | [ ]          |
| `VariantKind.cs`       | Try, Check, Find                                      | [ ]          |

---

## Phase 5: Module System Audit

### Module Infrastructure (`src/Analysis/Modules/`)

| File                       | Purpose                           | Audit Status |
|----------------------------|-----------------------------------|--------------|
| `ModuleDependencyGraph.cs` | Track imports, detect cycles      | [ ]          |
| `ModuleResolver.cs`        | Resolve import paths to files     | [ ]          |
| `CompilationDriver.cs`     | Coordinate multi-file compilation | [ ]          |

**Questions to answer:**

- [ ] How are circular imports detected?
- [ ] How are import paths resolved (relative, stdlib, project)?
- [ ] How is initialization order determined?
- [ ] Is the CLI integrated with `CompilationDriver`? (Currently: NO)

---

## Phase 6: Inference Systems Audit

### Inference (`src/Analysis/Inference/`)

| File                        | Purpose                             | Audit Status |
|-----------------------------|-------------------------------------|--------------|
| `MutationInference.cs`      | Track `me` mutations                | [ ]          |
| `MigratableInference.cs`    | Track DynamicSlice invalidation     | [ ]          |
| `CallGraph.cs`              | Build routine call graph            | [ ]          |
| `CallGraphNode.cs`          | Node in call graph                  | [ ]          |
| `CallEdge.cs`               | Edge in call graph                  | [ ]          |
| `ErrorHandlingAnalysis.cs`  | Analyze throw/absent usage          | [ ]          |
| `ErrorHandlingGenerator.cs` | Generate try_/check_/find_ variants | [ ]          |

**Questions to answer:**

- [ ] How does fixpoint iteration work for mutation propagation?
- [ ] What triggers migratable inference?
- [ ] How are error handling variants generated and registered?

---

## Phase 7: Cross-Cutting Concerns

### Error Handling

- [ ] Where is `SemanticError` created?
- [ ] How are errors collected and reported?
- [ ] Are error messages clear and actionable?
- [ ] Do errors include source locations?

### Generic Instantiation

- [ ] Where does generic instantiation happen?
- [ ] How are type arguments substituted?
- [ ] Are constraints validated during instantiation?
- [ ] Is instantiation cached properly?

### Language Differences (RazorForge vs Suflae)

- [ ] Where is `Language` enum checked?
- [ ] What features are RazorForge-only?
- [ ] Are Suflae restrictions properly enforced?

---

## Audit Findings Template

For each issue found, document:

```markdown
### [ISSUE-001] Brief description

**Location:** `src/Analysis/File.cs:123`

**Severity:** Critical / High / Medium / Low

**Description:**
What's wrong or missing.

**Expected Behavior:**
What should happen according to wiki/spec.

**Current Behavior:**
What actually happens.

**Suggested Fix:**
How to address it.
```

---

## Quick Reference: Key Data Flows

### Type Resolution Flow

```
TypeExpression (AST)
    → ResolveType() in SemanticAnalyzer.TypeResolution.cs
    → TypeRegistry.LookupType()
    → TypeInfo (or GenericParameterTypeInfo)
    → If generic: TypeRegistry.GetOrCreateInstantiation()
```

### Expression Type Inference Flow

```
Expression (AST)
    → AnalyzeExpression() in SemanticAnalyzer.Expressions.cs
    → Returns TypeInfo
    → Stored in expression's inferred type (if tracked)
```

### Routine Analysis Flow

```
FunctionDeclaration (AST)
    → Phase 2: Register in TypeRegistry via RoutineInfo
    → Phase 3: AnalyzeRoutineBody()
        → EnterScope(Function)
        → Declare parameters
        → Analyze body statements
        → ExitScope()
    → Phase 4: Mutation inference
    → Phase 5: Error handling variant generation
```

---

## Checklist Summary

| Phase | Area              | Files     | Status |
|-------|-------------------|-----------|--------|
| 1     | AST Layer         | 5 files   | [ ]    |
| 2     | Type System       | 15+ files | [ ]    |
| 3     | Semantic Analyzer | 9 files   | [ ]    |
| 4     | Infrastructure    | 3+ files  | [ ]    |
| 5     | Module System     | 3 files   | [ ]    |
| 6     | Inference         | 7 files   | [ ]    |
| 7     | Cross-Cutting     | N/A       | [ ]    |

**Total estimated files to audit:** ~40 files

**Recommended time:** 2-4 hours for thorough audit
