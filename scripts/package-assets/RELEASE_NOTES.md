## What's new in v0.0.4-alpha

This release makes **value types mutate in place**. Methods on a struct record now
receive their `me` receiver **by reference**, so a method can change the caller's
storage instead of a throwaway copy. It's a small surface change with a large
consequence: it's the foundation the upcoming concurrency tier (lock-free atomics)
and C FFI out-parameters are built on.

### Language

- **Struct-record methods now take `me` by reference.** A method on a value-type
  record can mutate its fields and the change is visible to the caller. Previously
  the receiver arrived as a hidden copy, so `me.field = …` (and `me.field.hijack()`)
  operated on a temporary that was discarded the moment the method returned — the
  mutation silently vanished. Now `me.field.hijack()` / `me.field.get_address()`
  inside a method yield the caller's real storage, so in-place mutation works.
  - **Behavioral note:** any code that relied on the old "mutation is a no-op"
    behavior will now see the mutation take effect. In practice that pattern was a
    latent bug (a value-type method that appeared to mutate but didn't).
- **`@llvm`-primitive records are unchanged.** The numeric types (`S8`…`U128`,
  `F16`…`F128`, the decimals and complexes), `Hijacked`, `Retained`, and friends
  keep value-passing semantics — their operators pass the raw value to intrinsics,
  so they were never affected by the copy problem and are untouched here.

### Compiler fixes

- **Address-of an rvalue receiver now works.** Taking the address of (or calling a
  by-reference method on) a temporary — a call result, a constructor expression, a
  literal — now spills the value to a temporary and uses its address, instead of
  failing with "cannot take address of expression".
- **Address-of through a crashable's fields** is now supported (it previously
  rejected crashable parents), so returning or copying a `Text`/record field of a
  crashable from `crash_message`/`$diagnose` works.

### Groundwork

- This is the prerequisite for two v0.1.0 features: **value-representation atomics**
  (`AtomicS64` is one byte like `S64`, differing only in its atomic-only API) and
  **C FFI out-parameters** (passing a struct's address to a C function). Both
  require a method to reach its receiver's real storage, which is exactly what
  by-reference `me` provides.

The change is locked by the full end-to-end fixture suite and the analyzer test
suite (1382 tests), all green, with every generated module verified by LLVM.
