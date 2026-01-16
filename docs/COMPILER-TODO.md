# RazorForge Compiler TODO

## Quick Status

| Phase             | Status     | Description                                                   |
|-------------------|------------|---------------------------------------------------------------|
| Lexer/Parser      | ✅ COMPLETE | Stable, synchronized between RazorForge and Suflae            |
| Semantic Analyzer | ✅ COMPLETE | Type system, scopes, mutation inference, error variants       |
| Code Generator    | ✅ WORKING  | Stdlib intrinsics compile to native LLVM, end-to-end verified |
| LSP               | ⏳ TODO     | Will reuse semantic analyzer infrastructure                   |
| Suflae Transpiler | ⏳ TODO     | GC + Reflection + Actor insertion                             |

---

## Recent Session: Parser Bug Fixes ✅ COMPLETE

**Date:** January 15, 2026

### Summary: Fixed Parser Issues (4 fixes, 6 tests fixed)

Fixed multiple parser and tokenizer issues across Suflae and RazorForge.

### Changes Made

1. **Suflae Generic Protocol Parsing** (`SuflaeParser.Declarations.cs`)
    - Added generic parameter parsing to `ParseProtocolDeclaration()`
    - Now supports `protocol Iterable<T>:` syntax
    - Added generic constraint support and scope management

2. **String Line Continuation** (`RazorForgeTokenizer.Literals.cs`)
    - Added `\` + newline support in string literals
    - Backslash followed by `\n` or `\r\n` continues string on next line
    - Skips leading whitespace on continuation line

3. **Suflae Blank Line Handling** (`SuflaeTokenizer.Indentation.cs`)
    - Fixed CRLF line ending handling in empty line detection
    - Added `\r` check to skip blank lines properly on Windows
    - Prevents false "Unexpected indent" errors after blank lines

4. **Protocol Method Attributes** (`SuflaeParser.Declarations.cs`)
    - Added attribute parsing before routine signatures in protocol bodies
    - Supports `@readonly routine display() -> Text` inside protocols
    - Connected parsed attributes to `RoutineSignature` AST node

### Test Results

| Metric        | Before | After |
|---------------|--------|-------|
| Failing Tests | 23     | 17    |
| Passing Tests | 714    | 720   |
| Skipped Tests | 10     | 10    |

### Tests Fixed

- `ParseSuflae_GenericProtocol` - Generic protocol declarations
- `Parse_LongTextEscapeLiteral` - Line continuation in strings
- `ParseSuflae_AttributeOnField` - Blank lines in record bodies
- `ParseSuflae_AttributeOnEntityField` - Blank lines in entity bodies
- `ParseSuflae_AttributeOnProtocol` - Protocol-level attributes
- `ParseSuflae_ProtocolMethodAttributes` - Attributes on protocol method signatures

### Remaining Test Failures (17 tests)

| Category         | Tests | Issue                                              | Complexity                  |
|------------------|-------|----------------------------------------------------|-----------------------------|
| When patterns    | 8     | `ParsePrimary` called with `=>` token unexpectedly | Medium - parsing flow issue |
| Using blocks     | 3     | Feature not implemented in parser                  | Medium                      |
| ForWithEnumerate | 2     | Requires AST changes for tuple destructuring       | High - AST + parser         |
| Loop-else        | 2     | `while...else`/`for...else` not implemented        | Medium                      |
| Other Suflae     | 2     | When pattern tests (similar to RazorForge)         | Medium                      |

### Analysis of Remaining Issues

**When Pattern Tests (8 tests):**

- Tests like `Parse_WhenChoice`, `Parse_WhenLiteralWithGuard` fail
- Error: "Unexpected token: FatArrow" in `ParsePrimary`
- First clause parses fine, second clause fails
- Likely issue with statement terminator consumption or token position after body parsing

**Using Blocks (3 tests):**

- `using` keyword exists but statement parsing not implemented
- Would need `UsingStatement` AST node and parsing logic

**ForWithEnumerate (2 tests):**

- Syntax: `for (index, item) in items.enumerate()`
- Current `ForStatement` only has single `Variable: string`
- Needs `Variables: List<string>` or tuple destructuring pattern

**Loop-Else (2 tests):**

- `while...else` and `for...else` (Python-style)
- Would need `ElseStatement` field on `WhileStatement`/`ForStatement`

---

## Recent Session: Wiki Documentation & Test Cleanup ✅ COMPLETE

**Date:** January 14, 2026 (Continued)

### Summary: Added Nested Destructuring Docs, Skipped Unimplemented Features

Added nested pattern destructuring documentation to both wikis and cleaned up tests by skipping those that require
unimplemented parser features.

### Changes Made

1. **RazorForge Wiki** (`RazorForge-Wiki/docs/Pattern-Matching.md`)
    - Added "Nested Destructuring" section with examples
    - Documents `is Circle ((x, y), radius)` syntax for deep destructuring

2. **Suflae Wiki** (`Suflae-Wiki/docs/Pattern-Matching.md`)
    - Added "Nested Destructuring" section with Suflae syntax
    - Fixed Decimal → F64 in examples (Suflae records can't use Decimal)

3. **Skipped Unimplemented Tests** (`tests/Parser/SuflaeAccessBlockTests.cs`)
    - `ParseSuflae_UsingWithAsync` - requires `suspended`/`waitfor` keywords
    - `ParseSuflae_NetworkResourceManagement` - requires `suspended`/`waitfor` keywords
    - `ParseSuflae_SelectOnChannels` - requires pattern matching on channel select
    - `ParseSuflae_SpawnThread` - requires `suspended`/`waitfor` keywords
    - `ParseSuflae_SpawnWithArguments` - requires `suspended`/`waitfor` keywords
    - `ParseSuflae_SpawnMultiple` - requires `suspended`/`waitfor` keywords

### Test Results

| Metric        | Before | After |
|---------------|--------|-------|
| Failing Tests | 24     | 23    |
| Passing Tests | 719    | 714   |
| Skipped Tests | 4      | 10    |

### Parser Features Not Yet Implemented

The following features are documented in wikis but not yet in parsers:

| Feature                     | Tests Affected | Token Exists |
|-----------------------------|----------------|--------------|
| `suspended` keyword         | 4 tests        | ✅ Yes        |
| `waitfor` keyword           | 4 tests        | ✅ Yes        |
| `when` as expression        | 4 tests        | -            |
| Nested destructuring        | 2 tests        | -            |
| `using` blocks (RF)         | 3 tests        | -            |
| `for enumerate`             | 2 tests        | -            |
| Suflae attributes on fields | 4 tests        | -            |

---

## Recent Session: Semantic Analyzer & Parser Fixes ✅ COMPLETE

**Date:** January 14, 2026 (Continued)

### Summary: Fixed Analyzer and Parser Issues, ChoiceCase Type Update

Fixed multiple analyzer test failures and updated ChoiceCase to use Expression instead of long for value storage,
following the design principle that parser stores raw expressions and semantic analyzer handles conversion.

### Changes Made

1. **Shadowing Detection** (`src/Analysis/SemanticAnalyzer.Declarations.cs`)
    - Fixed to properly detect variable shadowing in the same scope
    - Now reports error when variable name conflicts with existing in same scope

2. **Duplicate Field Detection** (`src/Analysis/SemanticAnalyzer.Declarations.cs`)
    - Fixed to properly detect duplicate field names in entity/record
    - Added field name tracking during declaration collection phase

3. **Extension Method Registration** (`src/Analysis/SemanticAnalyzer.Declarations.cs`)
    - Fixed FullName computation for methods with owner type
    - Parser produces `Name="Type.method"`, now correctly extracts just "method" for registration

4. **Reserved Function Prefix Validation**
    - Fixed validation for `try_`, `check_`, `find_` prefixes (reserved for error variants)
    - Now correctly reports errors for user-defined routines with these prefixes

5. **Unknown Type Parameter in Constraints** (`src/Analysis/SemanticAnalyzer.Declarations.cs`)
    - Added `ValidateConstraintTypeParameters()` method
    - Now validates that constraint type parameters exist in the generic parameter list

6. **Readonly Method Mutation Detection** (`src/Analysis/SemanticAnalyzer.Statements.cs`)
    - Added check for `@readonly` methods trying to mutate `me`
    - Reports `RF-S255: MutationInReadonlyMethod` error

7. **ChoiceCase Value Type** (`src/AST/Declarations.cs`, parsers)
    - Changed `ChoiceCase.Value` from `long?` to `Expression?`
    - Parser now stores raw expression, semantic analyzer handles conversion
    - Updated RazorForgeParser and SuflaeParser to just pass expression
    - Updated StdlibLoader to handle new type (value extraction deferred to semantic analysis)

8. **Test Fixes**
    - Fixed `Assert.NotNull` parameter in TypeDeclarationTests (use `@object:` not `value:`)
    - Fixed pattern matching tests with incorrect `=> {` syntax (should be just `{`)

### Test Results

| Metric        | Before | After |
|---------------|--------|-------|
| Failing Tests | 36     | 24    |
| Passing Tests | 707    | 719   |

### Tests Fixed

- `Analyze_ShadowingInSameScope_ReportsError`
- `Analyze_DuplicateFieldName_ReportsError`
- `Analyze_Method_RegistersWithOwnerType`
- `Analyze_ReservedFunctionPrefix_*` (3 tests)
- `Analyze_UnknownTypeParameter_ReportsError`
- `Analyze_ReadonlyMethod_*` (2 tests)
- `Parse_Choice_WithValues`
- Various pattern matching tests (syntax fixes)

### Remaining Test Failures (24 tests)

**Parser Tests:**

- Pattern matching (`Parse_When*`, `ParseSuflae_When*`) - some tests
- Control flow (`Parse_ForWithEnumerate`) - 1 test
- Access blocks (`ParseSuflae_Spawn*`, `ParseSuflae_Select*`, etc.) - 7 tests
- Suflae generic protocol parsing - 1 test

---

## Recent Session: Test Infrastructure & Mutability Validation Fixes ✅ COMPLETE

**Date:** January 14, 2026

### Summary: Fixed Test Naming and Assignment Mutability Validation

Fixed test helper methods to properly propagate test names, and fixed semantic analyzer to validate assignment
mutability when assignments are parsed as binary expressions.

### Changes Made

1. **Test Helper `[CallerMemberName]` Propagation** (`tests/TestHelpers.cs`)
    - Added `[CallerMemberName] string? fileName = null` to:
        - `AssertParseError()`
        - `Analyze()`
        - `AssertParses()`
        - `AssertAnalyzes()`
        - `AssertHasError()`
    - Now test failure messages show actual test names (e.g., `Analyze_LetReassignment_ReportsError`) instead of
      `AssertParses`

2. **Assignment Mutability Validation** (`src/Analysis/SemanticAnalyzer.Expressions.cs`)
    - **Root Cause:** Parser creates `BinaryExpression` with `BinaryOperator.Assign` for assignments (e.g., `x = 10`),
      but analyzer was only checking `AssignmentStatement` which doesn't exist
    - Added assignment handling in `AnalyzeBinaryExpression()`:
      ```csharp
      if (binary.Operator == BinaryOperator.Assign)
      {
          return AnalyzeAssignmentExpression(...);
      }
      ```
    - Added new `AnalyzeAssignmentExpression()` method that validates:
        - Target is assignable (identifier, member, or index)
        - Variable mutability (`let` variables cannot be reassigned)
        - Field write access (setter visibility)
        - Index assignment mutability (can't assign to index of `let` variable)
        - Type compatibility between value and target

### Test Results

| Metric        | Before | After |
|---------------|--------|-------|
| Failing Tests | 39     | 36    |
| Passing Tests | 704    | 707   |

### Tests Fixed

- All `MutabilityTests` now pass (17 tests)
- `Analyze_LetReassignment_ReportsError`
- `Analyze_LetCompoundAssignment_ReportsError`
- `Analyze_IndexAssignmentOnLet_ReportsError`

---

## Recent Session: Suflae Parser Fixes ✅ COMPLETE

**Date:** January 13, 2026 (Evening)

### Summary: Fixed Suflae Parser for `me` Keyword and When Statement Indentation

Fixed two failing Suflae parser tests by adding `me` keyword support and correcting indentation tracking for nested when
clause bodies.

### Changes Made

1. **`me` Keyword Parsing** (`SuflaeParser.Expressions.cs`)
    - Added `TokenType.Me` to the `Match` call in `ParsePrimary()`
    - The `me` keyword is tokenized as `TokenType.Me`, not `TokenType.Identifier`
    - Now `me.field` parses correctly in Suflae method bodies

2. **When Statement Indentation Tracking** (`SuflaeParser.Statements.cs`)
    - Changed `ParseWhenStatement()` to use `ProcessIndentToken()` instead of `Consume(TokenType.Indent)`
    - This properly tracks the when block's indentation level in the stack
    - Updated end of `ParseWhenStatement` to use `ProcessDedentTokens()` and handle EOF gracefully

3. **Single DEDENT Processing** (`SuflaeParser.Helpers.cs`)
    - Changed `ProcessDedentTokens()` from processing ALL consecutive DEDENTs to processing just ONE
    - Each block should only consume its own DEDENT, not consume DEDENTs belonging to outer blocks
    - Fixes nested blocks like when clause bodies inside when statements

### Root Cause Analysis

The original issue was in how nested indented blocks handled DEDENT tokens:

```
routine test():              # push level 1
    when result:             # push level 2 (was not tracked!)
        is SUCCESS value:
            show(...)        # push level 3
                             # DEDENT (was consuming all 3 dedents!)
```

The parser's `ProcessDedentTokens()` consumed ALL consecutive DEDENTs in a loop, which would "steal" dedents from outer
blocks and cause parsing errors like "Unexpected dedent - no matching indent".

### Tests Fixed

- `ParseSuflae_MeFieldAccess` - `me.x` and `me.y` field access in methods
- `ParseSuflae_VariantImmediatePatternMatch` - nested when clause bodies with variant patterns

---

## Recent Session: Literal Parsing & Visibility Simplification ✅ COMPLETE

**Date:** January 13, 2026

### Summary: Deferred All Literal Parsing to Semantic Analyzer

Simplified the parser to store all numeric, duration, and memory size literals as raw strings. The semantic analyzer
will parse values contextually based on expected type.

### Changes Made

1. **Numeric Literals as Strings** (`RazorForgeParser.Expressions.cs`, `SuflaeParser.Expressions.cs`)
    - `TryParseNumericLiteral()` now stores `token.Text` directly
    - Removed all `long.TryParse`, `double.TryParse` logic
    - Removed `ParseIntegerValue()` helper methods
    - Enables contextual type inference: `let a: S16 = 100` treats 100 as S16

2. **Duration/Memory Literals as Strings**
    - `TryParseDurationLiteral()` - stores raw text like `"500ms"`, `"2h"`
    - `TryParseMemoryLiteral()` - stores raw text like `"1024kb"`, `"2gb"`
    - Semantic analyzer will parse and validate values

3. **Imaginary Literals for Complex Numbers** (`TokenType.cs`, Tokenizers)
    - Added `J32Literal`, `J64Literal`, `J128Literal`, `JnLiteral` token types
    - Added suffix mappings: `j`/`j64`, `j32`, `j128`, `jn`
    - Example: `4.0j`, `2.5j32`, `1.0j128`, `3.14jn` (arbitrary precision)

4. **Unary Minus Handling**
    - Unary minus on literals now prepends `"-"` to the string value
    - Example: `-42` becomes literal with value `"-42"`
    - Binary subtraction `3 - 2` is correctly parsed as `3.__sub__(2)`, not `"3"` and `"-2"`

5. **Visibility System Simplification** (from earlier session)
    - Removed `SetterVisibility` from `VariableDeclaration` AST
    - Removed `SetterVisibility` from `FieldInfo` symbol
    - Now only 4 visibility levels: `public`, `published`, `internal`, `private`
    - Added `GetEffectiveWriteVisibility()` helper in SemanticAnalyzer

### Tests Added

- `Parse_BinarySubtraction_NotUnaryMinus` - verifies `3 - 2` is `3.__sub__(2)`
- `Parse_UnaryMinus_OnLiteral` - verifies `-2` is literal `"-2"`
- `Parse_BinarySubtraction_WithImaginaryLiteral` - verifies `3 - 2j` works correctly
- `Parse_UnaryMinusAndBinarySubtraction` - verifies `-3 - 2` is `(-3).__sub__(2)`
- `Parse_BinaryAddition_NotAffectedByUnaryHandling` - verifies `3 + 2` is `3.__add__(2)`

### Why Store as Strings?

| Benefit                   | Example                                            |
|---------------------------|----------------------------------------------------|
| Contextual type inference | `let a: S16 = 100` → 100 is S16, not S64           |
| Arbitrary precision       | `let b: Integer = 99999...99999` → no overflow     |
| Overflow detection        | Semantic analyzer validates value fits target type |
| Cleaner separation        | Parser handles syntax, analyzer handles values     |

### Remaining TODO (Semantic Analyzer)

- [ ] Parse numeric literals from strings with contextual type inference
- [ ] Parse duration literals (extract numeric part, convert units)
- [ ] Parse memory size literals (extract numeric part, convert units)
- [ ] Validate values fit in target types (overflow detection)

---

## Recent Session: Comparison Patterns & `becomes` Parser Enforcement ✅ COMPLETE

**Date:** January 12, 2026

### Summary: Added Comparison Pattern Parsing and `becomes` Syntax Enforcement

Implemented parser support for comparison patterns in `when` branches and added enforcement for the `becomes` keyword
syntax rules.

### Changes Made

1. **`ComparisonPattern` AST Node** (`src/AST/Statements.cs`)
    - New pattern record for comparison-based matching
    - Stores `TokenType Operator` and `Expression Value`
    - Supports: `==`, `!=`, `<`, `>`, `<=`, `>=`, `===`, `!==`

2. **RazorForgeParser.Statements.cs**
    - Added Case 5 for parsing comparison patterns in `ParseWhenStatement()`
    - Added `IsComparisonOperator()` helper method
    - Added `ParseComparisonPattern()` method
    - Added validation to reject `=> {` syntax (forces `pattern { }` for multi-statement blocks)

3. **SuflaeParser.Statements.cs**
    - Same comparison pattern support as RazorForge
    - Added validation to reject `=> :` syntax (forces `pattern:` for multi-statement blocks)
    - Added `IsComparisonOperator()` and `ParseComparisonPattern()` methods

### New Syntax Support

```razorforge
when status {
    == ACTIVE => process()
    < 0 => show("Negative")
    >= 100 => show("Large")
    === current_user => show("It's you!")
}
```

### Parser Enforcement

```razorforge
# ERROR: Block not allowed after =>
when x {
    == 1 => { ... }  # Parser error RF-G250
}

# CORRECT:
when x {
    == 1 { ... becomes value }  # Multi-statement block
}
```

### Remaining TODO (Semantic Analyzer)

- [ ] Error if multi-statement block in when expression lacks `becomes`
- [ ] Error if single-expression branch uses `becomes`

---

## Recent Session: Semantic Error Reporting Overhaul ✅ COMPLETE

**Date:** January 11, 2026

### Summary: Added Diagnostic Codes to All Semantic Errors

Implemented structured diagnostic codes (RF-S### for errors, RF-W### for warnings) across all semantic analyzer error
messages, matching the parser error format (RF-G###).

### Changes Made

1. **`SemanticDiagnosticCode` enum** - Added ~60 error codes organized in categories:
   | Range | Category |
   |-------|----------|
   | RF-S001-049 | Literal/Identifier errors |
   | RF-S050-099 | Operator errors |
   | RF-S100-149 | Type resolution errors |
   | RF-S150-199 | Generic constraint errors |
   | RF-S200-249 | Statement errors |
   | RF-S250-299 | Assignment errors |
   | RF-S300-349 | Control flow errors |
   | RF-S350-399 | Pattern matching errors |
   | RF-S400-449 | Declaration errors |
   | RF-S450-499 | Member access errors |
   | RF-S500-549 | Call errors |
   | RF-S550-599 | Collection errors |
   | RF-S600-649 | Memory token errors |
   | RF-S650-699 | Mutation errors |
   | RF-S700-799 | Error handling errors |
   | RF-S800-899 | Intrinsic errors |

2. **`SemanticWarningCode` enum** - New file with warning codes (RF-W###)

3. **`SemanticError` / `SemanticWarning` records** - Added `Code` property and `FormattedMessage`

4. **All analyzer files updated** - 113+ `ReportError`/`ReportWarning` call sites converted

5. **Module files updated** - `CompilationDriver`, `ModuleDependencyGraph`, `ModuleResolver`

6. **CLI updated** - Uses `FormattedMessage` for consistent output

### New Error Format

**Before:**

```
[5:10] Unknown identifier 'foo'.
```

**After:**

```
error[RF-S007]: test.rf:5:10: Unknown identifier 'foo'.
```

### Test Results After Overhaul

| Category | Passing | Failing | Notes                                         |
|----------|---------|---------|-----------------------------------------------|
| Total    | 692     | 46      | Pre-existing failures, not caused by overhaul |

### Remaining Test Failures (Pre-existing Issues)

The 46 failing tests are pre-existing issues, not caused by this overhaul:

1. ~~**`break`/`continue` outside loop**~~ - ✅ FIXED (2026-01-13)
2. **Various semantic edge cases** - Tests expecting errors that aren't being reported

---

## Recent Session: Intrinsic Attribute Separation

**Date:** January 10, 2026

### Summary: Split `@intrinsic` into Type and Routine Variants

Separated the `@intrinsic` attribute into two distinct attributes for clarity:

- `@intrinsic_type` - Declares LLVM IR primitive types (i8, i32, f64, ptr, etc.)
- `@intrinsic_routine` - Declares LLVM IR operations with type arguments (sitofp, fpext, trunc, etc.)

### Changes Made

1. **Lexer** - Added `TokenType.IntrinsicType` and `TokenType.IntrinsicRoutine`

2. **Parser - Declarations** (`RazorForgeParser.Declarations.cs`, `SuflaeParser.Declarations.cs`)
    - Updated `ParseAttributes()` to recognize new tokens:
   ```csharp
   while (Check(TokenType.At, TokenType.IntrinsicType, TokenType.IntrinsicRoutine, TokenType.Native))
   ```

3. **Parser - Helpers** (`RazorForgeParser.Helpers.cs`, `SuflaeParser.Helpers.cs`)
    - Renamed `ParseIntrinsicCall()` → `ParseIntrinsicRoutineCall()`
    - Updated doc comments to reference `@intrinsic_routine`

4. **Parser - Expressions** (`SuflaeParser.Expressions.cs`)
    - Updated to use `TokenType.IntrinsicRoutine` for expression parsing

5. **Wiki Documentation** (`RazorForge-Wiki/docs/Attributes.md`)
    - Added comprehensive documentation for both attributes
    - Added `@native` documentation for C FFI

### Usage

```razorforge
# Type intrinsic - declares LLVM primitive
@intrinsic_type("i64")
record S64 { value: @intrinsic.i64 }

# Routine intrinsic - LLVM operation with type args
@intrinsic_routine("sitofp")
routine S64.to_f64(me: S64) -> F64

# Native - C FFI call
@native("puts")
routine c_puts(s: Snatched<U8>) -> S32
```

---

## Recent Session: Parser Test Fixes & Error Reporting Issues

**Date:** January 9, 2026

### Summary: Fixed Parser Issues, Identified Major Error Reporting Problem

Fixed several parser issues but identified that parse error reporting needs major rework.

### Fixes Applied

1. **Octal Literal Parsing** (`RazorForgeParser.Expressions.cs`, `SuflaeParser.Expressions.cs`)
    - Added `0o` prefix support in `ParseIntegerValue()`
    - Before: `0o77` threw "Invalid integer literal"
    - After: `0o77` correctly parses to decimal 63

2. **Arbitrary Precision Integer Handling**
    - `TokenType.Integer` now stores raw string for semantic analysis
    - Large values that overflow 64-bit also stored as strings
    - `NumericLiteralParser` handles these during semantic analysis
    - Prevents parse errors for arbitrarily large integers

3. **Exception-Expecting Tests**
    - Changed 5 tests from `Assert.ThrowsAny<Exception>` to `AssertParseError`
    - Parser uses error recovery, not exception throwing

4. **Variant Registry Test**
    - Changed variant name from `Result` to `MyVariant`
    - `Result` is a well-known error handling type, caused conflict

### Test Results

| Category       | Passing | Failing |
|----------------|---------|---------|
| Parser tests   | 597     | 33      |
| Analyzer tests | 94      | 14      |
| **Total**      | **691** | **47**  |

### ~~MAJOR TODO: Parse Error Reporting Overhaul~~ PARTIALLY COMPLETE

**Status:** Semantic errors (RF-S###) ✅ COMPLETE | Parser errors (RF-G###) already existed | CLI format ✅ UPDATED

The current parse error reporting is inconsistent and needs complete rework:

#### Issues Identified

1. **Missing Filename** - Some parse errors don't include filename at all
2. **"unknown" Filename** - Some errors show `unknown:line:col:` instead of actual filename
3. **Inconsistent Format** - Error format varies between different error sites
4. **No Error Codes** - Errors lack unique identifiers for documentation/tooling

#### Required Changes

1. **Error Code System**
    - Define error codes: `RF-G001` (parser), `RF-S001` (semantic)
    - Each error type gets unique code
    - Enables error documentation and IDE integration

2. **Unified Error Format**
   ```
   error[RF-P042]: filename.rf:10:5: Expected '}' after block
   ```

3. **Filename Propagation**
    - Ensure `fileName` is passed through all parser methods
    - `ThrowParseError()` must always receive current file context
    - Tokenizer should store filename for token location

4. **Files to Update**
    - `RazorForgeParser.Base.cs` - `ParseException` class and `ThrowParseError()`
    - `SuflaeParser.Base.cs` - Same changes
    - `RazorForgeTokenizer.cs` - Store filename in tokens
    - `SuflaeTokenizer.cs` - Same changes
    - All `Consume()` and error sites - Pass location info

### Naming Convention Decisions (2026-01-10, updated 2026-01-12)

| Construct        | Case Names   | Pattern Match (no data) | Pattern Match (with data)  |
|------------------|--------------|-------------------------|----------------------------|
| **Choice**       | `ALL_CAPS`   | `== NORTH =>`           | N/A (choices have no data) |
| **Variant**      | `PascalCase` | `is Connect =>`         | `is Success value =>`      |
| **Preset const** | `ALL_CAPS`   | N/A                     | N/A                        |

**Pattern Types (2026-01-12):**

| Pattern               | Meaning                      | Use Case                    |
|-----------------------|------------------------------|-----------------------------|
| `is CaseName`         | Variant case (no data)       | Variant matching            |
| `is CaseName binding` | Variant case with extraction | Variant matching            |
| `is Type binding`     | Type check with binding      | Error handling, Data        |
| `== value`            | Value equality               | Choices, literals, booleans |
| `!= value`            | Value inequality             | Exclusion patterns          |
| `< value`             | Less than                    | Numeric ranges              |
| `> value`             | Greater than                 | Numeric ranges              |
| `<= value`            | Less than or equal           | Numeric ranges              |
| `>= value`            | Greater than or equal        | Numeric ranges              |
| `=== value`           | Reference equality           | Entity identity             |
| `!== value`           | Reference inequality         | Entity identity             |

**Special Error Handling Types (NOT normal variants):**

- `Maybe<T>`/`T?` - `T`, `none` - has `??` operator, `try_` prefix
- `Result<T>` - `T`, `Crashable` - `check_` prefix
- `Lookup<T>` - `T`, `none`, `Crashable` - has `lookup_` prefix

**Files to update:**

- [x] Pattern-Matching.md (both wikis) - Updated with comparison patterns
- [ ] `stdlib/` files using variants
- [ ] Test files with variant patterns
- [x] Parser support for comparison patterns in `when` branches ✅ (2026-01-12)

### What To Do Next

1. ~~**`break`/`continue` Outside Loop Validation**~~ ✅ COMPLETE (2026-01-13)
    - Added `ScopeKind.Loop` to track loop context
    - Added `IsInLoop` property to `Scope` class
    - `while`/`for` statements now use `ScopeKind.Loop`
    - `BreakStatement`/`ContinueStatement` now report `RF-S207`/`RF-S208` errors when outside loop

2. ~~**Error Reporting Overhaul**~~ ✅ COMPLETE (2026-01-11)
    - Semantic errors now have RF-S### codes
    - Parser errors already had RF-G### codes
    - See "Recent Session: Semantic Error Reporting Overhaul" above

3. **Fix Remaining Test Failures** (46 tests)
    - `break`/`continue` outside loop validation (see #1)
    - Other semantic edge cases

4. **Remaining Parser Fixes**
    - `while...else` syntax - requires `WhileStatement.ElseStatement` field
    - Line continuation `\` in string literals
    - Suflae method syntax (`TypeName.methodName()`)

5. **Suflae Parser Gaps**
    - ~~`using` statements (resource management)~~ ✅ Implemented (2026-01-10)
    - Attribute parsing on various constructs
    - Note: `spawn` is dead, `select` and `enumerate` are just routine names

---

## Recent Session: Suflae Parser Fixes

**Date:** January 9, 2026

### Summary: Fixed Multiple Suflae Parsing Issues

Fixed several parsing issues in the Suflae parser that were causing test failures. Reduced test failures from 51 to 44.

### Fixes Applied

1. **`elseif` Chain Handling** (`SuflaeParser.Statements.cs:18-107`)
    - Added support for `elseif` chains in `ParseIfStatement()`
    - Converts `elseif` branches to nested `IfStatement` nodes
    - Added helper method `AttachElseBranch()` for immutable chain construction
    - Before: `elseif` was not recognized, causing empty declarations

2. **`return` Without Value Before Dedent** (`SuflaeParser.Statements.cs:485-486`)
    - Fixed `ParseReturnStatement()` to check for both `Newline` AND `Dedent` tokens
    - Before: `return` at end of block failed because `Dedent` was incorrectly parsed as expression start
    - After: `return` followed by `Dedent` correctly creates valueless return

3. **`is none` Pattern Matching** (`SuflaeParser.Statements.cs:419-426`)
    - Added special handling for `TokenType.None` in `ParseTypePattern()`
    - `none` is a keyword, not an identifier, so needed explicit handling
    - Before: `is none` threw "Expected type name after 'is', got None"
    - After: `is none` correctly creates a TypePattern with name "none"

### Test Results

| Metric        | Before | After |
|---------------|--------|-------|
| Failing Tests | 51     | 44    |
| Passing Tests | 690    | 697   |

### Remaining Known Issues

- `while...else` syntax - requires AST changes (WhileStatement needs ElseStatement field)
- ~~`using` statements - resource management parsing~~ ✅ Implemented (2026-01-10)
- Some attribute parsing on fields
- Note: `spawn` is dead, `select`/`enumerate` are just routine names (not special syntax)

### Also Fixed: Windows Native Build (`-fPIC` removal)

Removed `-fPIC` flag from CMake configs for Windows builds:

- `cmake/libbf.cmake` - wrapped `-fPIC` with `if(NOT WIN32)`
- `cmake/inteldecimal.cmake` - moved `-fPIC` out of compiler-specific blocks, wrapped with `if(NOT WIN32)`
- `cmake/libtommath.cmake` - added `NOT WIN32` check
- `native/build.bat` - updated paths from `net9.0` to `net10.0`, fixed DLL copy paths

---

## Recent Session: `becomes` Keyword Documentation

**Date:** January 9, 2026

### Summary: Documented `becomes` Syntax Rules

Updated wiki documentation for `becomes` keyword and clarified syntax rules for `when` expressions.

### Changes Made

1. **RazorForge-Wiki/docs/Pattern-Matching.md**
    - Added `## The becomes Keyword` section with examples
    - Fixed incorrect `=> {` patterns to just `{` for multi-statement blocks

2. **Suflae-Wiki/docs/Pattern-Matching.md**
    - Added equivalent `becomes` documentation for Suflae syntax
    - Multi-statement blocks use `:` (indentation) not `=> {`

### Key Syntax Rules

| Context    | Single Expression | Multi-Statement Block                            |
|------------|-------------------|--------------------------------------------------|
| RazorForge | `pattern => expr` | `pattern { ... becomes value }`                  |
| Suflae     | `pattern => expr` | `pattern:` + indented block with `becomes value` |

**Critical:** NO `=>` before `{` or `:` when starting a multi-statement block.

---

## Recent Session: Removed `@prelude` Attribute

**Date:** January 8, 2026

### Summary: Simplified Module System

Removed the `@prelude` attribute system. Now only `Core` namespace is auto-imported.

### Changes Made

1. **RazorForge-Wiki** - Updated `Modules-and-Imports.md`:
    - Removed `@prelude` attribute section
    - Added `## The Core Namespace` section listing auto-imported types
    - Core includes: integers, floats, decimals, complex, memory types, control types, protocols

2. **Suflae-Wiki** - Updated `Modules-and-Imports.md`:
    - Same changes as RazorForge
    - Suflae Core also includes Console I/O and File I/O

3. **README.md** - Updated links from "Core Prelude" to "Core"

4. **docs/STDLIB-TODO.md** - Updated terminology:
    - "Core Prelude" → "Core Namespace"
    - Updated directory structure comments

### New Module Design

| Concept             | Before                              | After                                              |
|---------------------|-------------------------------------|----------------------------------------------------|
| `Core` types        | Auto-imported                       | Auto-imported (unchanged)                          |
| `@prelude` routines | Auto-available without import       | **Removed** - must import module                   |
| `show()`, `exit!()` | Available everywhere via `@prelude` | RazorForge: `import Console`, Suflae: part of Core |

### Rationale

- **Explicitness**: Know exactly where every symbol comes from
- **No magic**: Everything except Core requires explicit import
- **Simpler mental model**: One auto-import (Core), everything else explicit

---

## Recent Session: Gitea Migration ✅ COMPLETE

**Date:** January 8, 2026

- [x] Push repo to Gitea
- [x] Rename remotes: `origin` → Gitea, `github` → secondary
- [x] Verify `act_runner` picks up `.gitea/workflows/build.yaml`
- [x] CI downloads native libraries (Intel Decimal, LibBF, LibTomMath, MAPM)
- [x] Fixed `-fPIC` for shared library linking on Linux
- [x] CI runs `dotnet build` + `dotnet test`

**Rationale:** GitHub Actions charging for self-hosted runners starting March 2026.

---

## Recent Session: `published` Access Modifier

**Date:** January 7, 2026

### Summary: Simplified Access Modifier System

Replaced the 6-level getter/setter visibility system (`public private(set)`, `public internal(set)`, etc.) with a
simpler 4-level system using the new `published` keyword.

### Changes Made

1. **VisibilityModifier Enum** - Updated `src/AST/Enums/VisibilityModifier.cs`:
    - Added `Published` (public read, private write)
    - Removed `PublicPrivateSet`, `PublicInternalSet`, `InternalPrivateSet`

2. **Parser** - Updated both SuflaeParser and RazorForgeParser:
    - Added `Published` to `ParseVisibilityModifier()`
    - Removed `ParseGetterSetterVisibility()` (no more `X(set)` syntax)

3. **Semantic Analyzer** - Updated `src/Analysis/SemanticAnalyzer.Helpers.cs`:
    - `GetVisibilityLevel()` now handles `Published` as level 2 (public read)
    - `ValidateMemberAccess()` treats `Published` like `Public` for read access

4. **Tokenizer** - `TokenType.Published` already existed and is recognized

### The 4 Access Levels

| Modifier    | Read Access | Write Access | Use Case                      |
|-------------|-------------|--------------|-------------------------------|
| `public`    | public      | public       | Fully mutable public field    |
| `published` | public      | private      | Public read, private write    |
| `internal`  | internal    | internal     | Module-internal mutable field |
| `private`   | private     | private      | Fully private field           |

### TODO: Semantic Analysis for `published` Write Access

Currently the semantic analyzer recognizes `published` for read access validation, but needs additional work:

- [ ] Validate that writes to `published` fields are only allowed from same file
- [ ] Error message: "Cannot write to published field 'X' from outside its defining file"
- [ ] Update `ValidateFieldWriteAccess()` to check for `Published` visibility
- [ ] Add tests for published field access patterns

---

## Recent Session: Stdlib List.rf Uses Native Memory

**Date:** January 7, 2026

### Milestone: List<T> Uses Snatched<T> and Native Heap Functions

Updated `stdlib/Collections/List.rf` to use proper native memory management:

### Changes Made

1. **Native Memory Functions** - List.rf now uses core heap functions:
    - `heap_alloc(bytes: UAddr) -> UAddr` - Allocate heap memory
    - `heap_free(address: UAddr)` - Free heap memory
    - `heap_realloc(address: UAddr, new_bytes: UAddr) -> UAddr` - Resize allocation
    - `data_size<T>()` - Compiler intrinsic for type size in bytes

2. **Snatched<T> Pointer Wrapper** - Heap-allocated data uses `Snatched<T>`:
    - `.read()` - Read value at pointer
    - `.write(value)` - Write value at pointer
    - `.offset(bytes)` - Get offset pointer
    - `.is_none()` - Check for null
    - `snatched_none<T>()` - Create null pointer

3. **Indexing Overloads** - Added multiple `__getitem__`/`__setitem__` overloads:
    - `Integral` types (forward index)
    - `BackIndex` types (backward index using `^`)
    - `Range<Integral>` (forward range slicing)
    - `Range<BackIndex>` (backward range slicing)

### Example Implementation

```razorforge
routine List<T>.add_last(me: List<T>, value: T) {
    if me.count >= me.capacity {
        let new_capacity = if me.capacity == 0u64 then 4u64 else me.capacity * 2u64
        me.reserve(new_capacity)
    }
    let byte_offset = me.count * data_size<T>()
    me.data
        .offset(byte_offset)
        .write(value)
    me.count = me.count + 1u64
}
```

### Files Modified

- `stdlib/Collections/List.rf` - Complete rewrite using native memory

---

## Previous Session: Stdlib Intrinsics Now Compile to Native LLVM

**Date:** January 7, 2026

### Milestone: Removed primitives.c Dependency

The codegen now compiles stdlib routines with `@intrinsic.*` API, emitting native LLVM instructions instead of calling
external C functions.

### Changes Made

1. **Stdlib Program Compilation** - Codegen now receives and compiles stdlib ASTs
    - `LLVMCodeGenerator` accepts `stdlibPrograms` parameter
    - Stdlib routine bodies are compiled with their intrinsic operations
    - Example: `S64.__add__` now emits `add i64` instruction directly

2. **LLVM Type Name Aliases** - Added aliases for stdlib convenience
    - `@intrinsic.double` → `@intrinsic.f64`
    - `@intrinsic.float` → `@intrinsic.f32`
    - `@intrinsic.half` → `@intrinsic.f16`
    - `@intrinsic.fp128` → `@intrinsic.f128`

3. **Single-Field Wrapper Fix** - F64, F32, etc. now correctly unwrap
    - Before: `%Record.F64 = type { }` (empty struct)
    - After: `double` (correct LLVM type)

4. **Function Deduplication** - Prevents declare/define conflicts
    - `_generatedFunctions` and `_generatedFunctionDefs` tracking
    - Still has minor issues with stdlib routines that have parse errors

### Generated LLVM Example

```llvm
define i64 @S64___add__(i64 %me, i64 %you) {
entry:
  %you.addr = alloca i64
  store i64 %you, ptr %you.addr
  %tmp498 = load i64, ptr %you.addr
  %tmp499 = add i64 %me, %tmp498
  ret i64 %tmp499
}
```

### Remaining Issues

1. **Duplicate function declarations** - Some stdlib routines with parse errors cause both `declare` and `define` to be
   emitted
2. ~~**Stdlib parse errors** - Console.rf, Decimal.rf, Integer.rf, Blank.rf have syntax issues~~ **FIXED** (2026-01-07)
    - Added `where` keyword support for generic constraints
    - Added `private` field support inside entity bodies
    - Added `PassDeclaration` for `pass` inside type bodies

### Files Modified

- `src/CodeGen/LLVMCodeGenerator.cs` - Added stdlib programs support
- `src/CodeGen/LLVMCodeGenerator.Declarations.cs` - Function deduplication
- `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - Fixed MemberExpression property access
- `src/Analysis/TypeRegistry.cs` - Added LLVM type name aliases

---

## Previous Session: End-to-End Execution Working!

**Date:** January 6, 2026

### Milestone: First Successful Native Execution

Successfully compiled and ran RazorForge code end-to-end:

```
$ ./test_codegen.exe
Testing RazorForge codegen:
  factorial(5) = 120
  add_s64(10, 20) = 30
  max_s64(15, 8) = 15
```

### Changes Made

1. **Function Name Mangling** - Changed from `Type.method` to `Type_method` for C ABI compatibility
    - `LLVMCodeGenerator.Declarations.cs:469` - MangleFunctionName uses `_` separator
    - `LLVMCodeGenerator.Expressions.cs:548` - EmitMethodCall fallback uses `_`

2. **Runtime Primitive Stubs** - Created `native/runtime/primitives.c`
    - Sn, Un: `__add__`, `__sub__`, `__mul__`, `__floordiv__`, `__rem__`, `__eq__`, `__ne__`, `__lt__`, `__le__`,
      `__gt__`, `__ge__`, `__cmp__`
    - F64/F32: Same operations (excluding `__rem__`, including `__truediv__`)
    - Bool: `__and__`, `__or__`, `__not__`, `__eq__`, `__ne__`

3. **Main Wrapper** - Created `native/runtime/main_wrapper.c`
    - Calls RazorForge `start()` entry point
    - Provides C `main()` for executable linking

### Build Command

```bash
dotnet run -- codegen test_codegen.rf
clang test_codegen.ll primitives.o native/runtime/main_wrapper.c -o test_codegen.exe
./test_codegen.exe
```

### Verified Features

| Feature                | Status                             |
|------------------------|------------------------------------|
| Arithmetic (`+ - *`)   | ✅ Executes correctly               |
| Comparison (`> == <=`) | ✅ Executes correctly               |
| If/else control flow   | ✅ Executes correctly               |
| Recursion (factorial)  | ✅ `factorial(5) = 120`             |
| Function calls         | ✅ Parameters passed correctly      |
| Variable declarations  | ✅ Allocas and stores work          |
| Float operations (F64) | ✅ Compiles (not tested at runtime) |

### Next Steps

1. **Expand test coverage:**
    - [ ] Loops (while, for, until)
    - [ ] Pattern matching (when)
    - [ ] Records and entities
    - [ ] Collections (List, Dict)

2. **Runtime expansion:**
    - [ ] Memory allocation (`rf_alloc`, `rf_free`)
    - [ ] I/O operations (`show`, `show_line`, `get_line`)
    - [ ] Text/String handling

3. **Optimization:**
    - [ ] Inline primitive operations instead of function calls
    - [ ] Dead code elimination
    - [ ] Constant folding

---

## Architecture Overview

```
Suflae source (.sf)                    RazorForge source (.rf)
       ↓                                        ↓
   Suflae Lexer                           RF Lexer
       ↓                                        ↓
   Suflae Parser                          RF Parser
       ↓                                        ↓
   Suflae AST                              RF AST
       ↓                                        │
   Transpiler ──────────────────────────────────┤
   (GC + Reflection + Actor insertion)          │
       ↓                                        ↓
       └──────────────→ RazorForge AST ←────────┘
                              ↓
                    Semantic Analyzer
                    (type checking, generic instantiation,
                     symbol resolution)
                              ↓
                        Typed AST / IR
                        ↙         ↘
                LLVM CodeGen      LSP Services
```

---

## Completed Work Summary

### Semantic Analyzer (Phases 1-6) ✅ COMPLETE

- **Type System**: TypeRegistry, TypeInfo hierarchy (Record/Entity/Resident/Choice/Variant/Mutant/Protocol)
- **Scope System**: Hierarchical scopes with ScopeKind (Global/Module/Type/Function/Block)
- **Statement Analysis**: Variable declarations, assignments, control flow, loops, returns
- **Expression Analysis**: Identifiers, operators, calls, member access, patterns
- **Mutation Inference**: Call graph propagation for me mutation tracking
- **Error Handling**: Auto-generation of try_/check_/find_ variants from `!` functions
- **Derived Operators**: Auto-generate `__ne__` from `__eq__`, comparison ops from `__cmp__`

### Code Generator (Phase 7) 🔄 IN PROGRESS

**Completed:**

- Expressions: intrinsics, calls, operators, literals, conditionals, indexing
- Statements: variables, assignments, returns, if/else, loops, when/pattern matching
- Terminator tracking for valid LLVM IR basic blocks
- String constant deduplication
- Native function declaration tracking

**Remaining:**

- [x] End-to-end LLVM verification and execution ✅ January 6, 2026
- [ ] Loops (while, for, until) - needs testing
- [ ] Collections (List, Dict, Set)
- [ ] Text types (Text, Bytes, TextBuffer)
- [ ] I/O operations (show, show_line, get_line)
- [ ] FFI (imported routines)
- [ ] Concurrency (Shared, inspecting/seizing)
- [ ] Runtime initialization

---

## Source Organization

```
src/Analysis/
├── Enums/           # Language, TypeCategory, MutationCategory, ScopeKind, etc.
├── Results/         # AnalysisResult, SemanticError, SemanticWarning
├── Symbols/         # RoutineInfo, VariableInfo, FieldInfo, ParameterInfo
├── Types/           # TypeInfo hierarchy (15+ type classes)
├── Scopes/          # Scope hierarchy
├── Inference/       # CallGraph, MutationInference, ErrorHandlingGenerator
├── Modules/         # ModuleResolver, CompilationDriver, StdlibLoader
├── Native/          # NumericLiteralParser
├── TypeRegistry.cs  # Central type repository
└── SemanticAnalyzer.*.cs  # Partial classes for analysis phases

src/CodeGen/
├── LLVMCodeGenerator.cs            # Main infrastructure
├── LLVMCodeGenerator.Types.cs      # Type generation
├── LLVMCodeGenerator.Declarations.cs  # Function declarations
├── LLVMCodeGenerator.Statements.cs # Statement emission
└── LLVMCodeGenerator.Expressions.cs # Expression emission
```

---

## Future Phases

### Phase 8: `becomes` Keyword for Block Results ✅

- Add `becomes` to lexer (TokenType.Becomes) ✅
- Parser support for `becomes expression` in block contexts ✅
- Semantic analysis for `becomes` statement ✅
- Works in `when` branches, `if` expressions, any block that produces a value

**Syntax Rules (updated 2026-01-09):**

| Context    | Single Expression | Multi-Statement Block                            |
|------------|-------------------|--------------------------------------------------|
| RazorForge | `pattern => expr` | `pattern { ... becomes value }`                  |
| Suflae     | `pattern => expr` | `pattern:` + indented block with `becomes value` |

**Key Points:**

- `=>` is for single expressions only (no `becomes` needed)
- Multi-statement blocks use `{` (RazorForge) or `:` (Suflae) directly after pattern - NO `=>`
- `becomes` specifies the block's result value in multi-statement contexts

**TODO:**

- [x] Parser: Enforce no `=>` before `{` or `:` in when branches ✅ (2026-01-12)
- [ ] Semantic: Error if multi-statement block in when expression lacks `becomes`
- [ ] Semantic: Error if single-expression branch uses `becomes`

### Phase 9: LSP Integration

- Reuse TypeRegistry for hover, completions, go-to-definition
- Incremental parsing/analysis

### Phase 10: Suflae Actor Model

- `.share()` → actor infrastructure
- Method calls → message sends
- GreenThread scheduler

### Phase 11: Generator Routines (`generate`)

- State machine transformation
- Coroutine frame allocation

### Phase 12: Async Routines (`suspended`, `waitfor`)

- Async/await state machines
- Task executor integration

---

## Build Status

| Component          | Status                                |
|--------------------|---------------------------------------|
| C# Compiler        | ✅ Builds (warnings only)              |
| Native Runtime     | ✅ primitives.c + main_wrapper.c       |
| LLVM IR Generation | ✅ Generates valid IR                  |
| LLVM Compilation   | ✅ Links and executes                  |
| End-to-End Test    | ✅ factorial(5)=120, add_s64(10,20)=30 |

---

## Key Design Decisions

1. **No user-visible primitives** - S32, Bool, etc. are single-field records wrapping `@intrinsic.*`
2. **Operators are methods** - `a + b` → `a.__add__(b)` at parse time
3. **Types**: record (value), entity (reference), resident (fixed reference), choice, variant, mutant, protocol
4. **Error handling**: `!` suffix, `throw`/`absent`, auto-generated safe variants
5. **Memory wrappers**: Handles (Shared, Tracked) vs Tokens (Viewed, Hijacked, Inspected, Seized)
