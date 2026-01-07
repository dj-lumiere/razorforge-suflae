# RazorForge Compiler Internals

This document describes internal compiler constructs that are NOT part of the user-facing language specification. These are implementation details used by the stdlib and compiler.

## @intrinsic.* - LLVM Primitives

`@intrinsic.*` provides direct access to LLVM types and operations. These are the lowest-level building blocks used internally by native data types.

### Usage Context
- Only valid inside `danger!` blocks
- Used for implementing primitive types (s8, u8, s16, u16, etc.)
- Maps directly to LLVM IR

### Categories

#### Arithmetic Operations
```razorforge
@intrinsic.add<T>(a, b)      # Addition
@intrinsic.sub<T>(a, b)      # Subtraction
@intrinsic.mul<T>(a, b)      # Multiplication
@intrinsic.div<T>(a, b)      # Division
@intrinsic.rem<T>(a, b)      # Remainder/modulo
@intrinsic.neg<T>(a)         # Negation
```

#### Bitwise Operations
```razorforge
@intrinsic.and<T>(a, b)      # Bitwise AND
@intrinsic.or<T>(a, b)       # Bitwise OR
@intrinsic.xor<T>(a, b)      # Bitwise XOR
@intrinsic.not<T>(a)         # Bitwise NOT
@intrinsic.shl<T>(a, b)      # Shift left
@intrinsic.shr<T>(a, b)      # Shift right (logical)
@intrinsic.sar<T>(a, b)      # Shift right (arithmetic)
```

#### Comparison Operations
```razorforge
@intrinsic.eq<T>(a, b)       # Equal
@intrinsic.ne<T>(a, b)       # Not equal
@intrinsic.lt<T>(a, b)       # Less than
@intrinsic.le<T>(a, b)       # Less than or equal
@intrinsic.gt<T>(a, b)       # Greater than
@intrinsic.ge<T>(a, b)       # Greater than or equal
```

#### Type Conversions
```razorforge
@intrinsic.bitcast<From, To>(value)   # Reinterpret bits
@intrinsic.trunc<From, To>(value)     # Truncate to smaller type
@intrinsic.zext<From, To>(value)      # Zero-extend to larger type
@intrinsic.sext<From, To>(value)      # Sign-extend to larger type
@intrinsic.fptoui<From, To>(value)    # Float to unsigned int
@intrinsic.fptosi<From, To>(value)    # Float to signed int
@intrinsic.uitofp<From, To>(value)    # Unsigned int to float
@intrinsic.sitofp<From, To>(value)    # Signed int to float
```

#### Memory Operations
```razorforge
@intrinsic.load<T>(ptr)               # Load from memory
@intrinsic.store<T>(ptr, value)       # Store to memory
@intrinsic.store.volatile<T>(ptr, value)  # Volatile store
```

#### Overflow-Checking Arithmetic
```razorforge
@intrinsic.add.overflow<T>(a, b)      # Returns (result, overflow_flag)
@intrinsic.sub.overflow<T>(a, b)
@intrinsic.mul.overflow<T>(a, b)
```

#### Saturating Arithmetic
```razorforge
@intrinsic.add.sat<T>(a, b)           # Saturates at min/max
@intrinsic.sub.sat<T>(a, b)
@intrinsic.mul.sat<T>(a, b)           # Saturates at min/max
```

#### Wrapping Arithmetic
```razorforge
@intrinsic.add.wrap<T>(a, b)          # Wraps around on overflow
@intrinsic.sub.wrap<T>(a, b)
@intrinsic.mul.wrap<T>(a, b)
```

---

## @native.* - C Runtime Library Calls

`@native.*` provides access to the C runtime library functions defined in `native/runtime/`. These are used for operations that require platform-specific implementations or cannot be expressed in pure RazorForge.

### Usage Context
- Only valid inside `danger!` blocks
- Calls C functions from the native runtime library
- Used for I/O, memory management, string operations, etc.

### Syntax
```razorforge
danger! {
    @native.function_name(arg1, arg2, ...)
}
```

### Categories

#### Memory Management
```razorforge
@native.claim_dynamic_uaddr(bytes)         # Allocate heap memory
@native.release_dynamic_uaddr(address)     # Free heap memory
@native.resize_dynamic(address, new_bytes) # Reallocate memory
@native.memory_copy_uaddr(src, dest, bytes)# Copy memory
@native.memory_fill(address, pattern, bytes)# Fill memory
@native.memory_zero_uaddr(address, bytes)  # Zero memory
@native.sizeof<T>()                        # Get size of type
```

#### Pointer Operations
```razorforge
@native.address_of<T>(value)               # Get address of value
@native.read_as<T>(address)                # Read typed value from address
@native.write_as<T>(address, value)        # Write typed value to address
@native.volatile_read_as<T>(address)       # Volatile read
@native.volatile_write_as<T>(address, value)# Volatile write
@native.invalidate<T>(snatched)            # Free memory at pointer
```

#### Console I/O
```razorforge
@native.rf_console_print_cstr(cstr)        # Print to stdout
@native.rf_console_print_line_cstr(cstr)   # Print line to stdout
@native.rf_console_print_line_empty()      # Print newline to stdout
@native.rf_console_alert_cstr(cstr)        # Print to stderr
@native.rf_console_alert_line_cstr(cstr)   # Print line to stderr
@native.rf_console_alert_line_empty()      # Print newline to stderr
@native.rf_console_get_letters(count)      # Read n characters
@native.rf_console_get_word()              # Read word
@native.rf_console_get_line()              # Read line
@native.rf_console_get_all()               # Read until EOF
@native.rf_console_flush()                 # Flush stdout
@native.rf_console_clear()                 # Clear console
```

#### C String Operations
```razorforge
@native.rf_strlen(cstr)                    # Get string length
@native.rf_strcpy(dest, src)               # Copy string
@native.rf_strcmp(a, b)                    # Compare strings
@native.rf_cstr_len(cstr)                  # Get cstr length
```

#### Wide String Operations
```razorforge
@native.rf_wcslen(cwstr)                   # Get wide string length
@native.rf_wcscpy(dest, src)               # Copy wide string
@native.rf_wcscmp(a, b)                    # Compare wide strings
```

#### Decimal Floating Point (d32)
```razorforge
@native.d32_add(a, b)                      # Add
@native.d32_sub(a, b)                      # Subtract
@native.d32_mul(a, b)                      # Multiply
@native.d32_div(a, b)                      # Divide
@native.d32_neg(a)                         # Negate
@native.d32_abs(a)                         # Absolute value
@native.d32_eq(a, b)                       # Equal
@native.d32_ne(a, b)                       # Not equal
@native.d32_lt(a, b)                       # Less than
@native.d32_le(a, b)                       # Less or equal
@native.d32_gt(a, b)                       # Greater than
@native.d32_ge(a, b)                       # Greater or equal
@native.d32_from_s64(value)                # From signed 64-bit
@native.d32_from_u64(value)                # From unsigned 64-bit
@native.d32_from_f64(value)                # From float64
@native.d32_to_s64(value)                  # To signed 64-bit
@native.d32_to_u64(value)                  # To unsigned 64-bit
@native.d32_to_f64(value)                  # To float64
@native.d32_sqrt(value)                    # Square root
@native.d32_floor(value)                   # Floor
@native.d32_ceil(value)                    # Ceiling
@native.d32_round(value)                   # Round
@native.d32_trunc(value)                   # Truncate
@native.d32_is_nan(value)                  # Check NaN
@native.d32_is_inf(value)                  # Check infinity
```

#### Decimal Floating Point (d64, d128)
Same operations as d32 with `d64_*` and `d128_*` prefixes.

#### Parsing
```razorforge
@native.parse_u8(cstr)                     # Parse u8 from string
# Similar for other numeric types
```

---

## Implementation Notes

### danger! Blocks
Both `@intrinsic.*` and `@native.*` require `danger!` blocks:
```razorforge
danger! {
    # Unsafe code here
    let raw_ptr = @native.address_of<T>(value)
    let result = @intrinsic.add<u64>(a, b)
}
```

### Type Parameters
Generic intrinsics and native calls use `<T>` syntax:
```razorforge
@intrinsic.add<u64>(a, b)
@native.sizeof<MyStruct>()
```

### Return Values
Most operations return values that must be captured:
```razorforge
let sum = @intrinsic.add<s32>(x, y)
let ptr = @native.claim_dynamic_uaddr(1024uaddr)
```

---

## Native Runtime Library Location

C implementations are in `native/runtime/`:
- Memory functions
- Console I/O
- String operations
- Decimal floating point (using Intel DFP library)
- Platform-specific implementations
