## What's new in v0.0.4-alpha

This release makes **value types mutate in place**. A method on a storage-backed
record now receives its `me` receiver **by reference**, so it can change the
caller's storage instead of a throwaway copy. It's a small surface change with a
large consequence: it's the foundation the upcoming concurrency tier (lock-free
atomics) and C FFI out-parameters are built on.

### Language

- **Storage-backed record methods now take `me` by reference.** A method on a
  value-type record can mutate its fields and the change is visible to the caller.
  Previously the receiver arrived as a hidden copy, so `me.field = …` (and
  `me.field.hijack()`) operated on a temporary that was discarded the moment the
  method returned — the mutation silently vanished. Now those operations reach the
  caller's real storage, so in-place mutation works. This applies to every
  storage-backed record:
  - **struct records** (records with no `@llvm` backend type), and
  - **inline aggregate records** — `Array[T, N]` and `BitArray[N]`, whose backend
    is an inline `[N x T]` buffer.

  The rule is purely type-level — there is no per-method or per-name special case.
  `Array.$setitem!` is by-reference because `Array` is storage-backed, exactly like
  every other `Array` method.

  - **Behavioral note:** any code that relied on the old "mutation is a no-op"
    behavior will now see the mutation take effect. In practice that pattern was a
    latent bug (a value-type method that appeared to mutate but didn't).

- **Nested and indexed in-place mutation now works.** Assigning through a field or
  element chain mutates the target directly:
  - `me.inner.field = …` and `me.inner.field += 1` (nested struct fields)
  - `arr[i] = x` and `a.b[i] = x` (index assignment, including through a field)

  `Array` / `BitArray` index assignment writes the single element in place — it no
  longer rebuilds the whole buffer on every write.

- **`@llvm`-primitive (scalar) records are unchanged.** The numeric types
  (`S8`…`U128`, `F16`…`F128`, the decimals and complexes), `Bool`, `Hijacked`,
  `Retained`, and friends keep value-passing semantics — their value *is* the
  machine register their operators feed to intrinsics, so they were never affected
  by the copy problem and are untouched here. (Such types are pure values and never
  mutate `me` in place, so "needs by-value" and "mutates in place" never overlap.)
  Use `with` when you want a fresh, independent copy of a value record.

### Compiler fixes

- **Address-of an rvalue receiver now works.** Taking the address of (or calling a
  by-reference method on) a temporary — a call result, a constructor expression, a
  literal — now spills the value to a temporary and uses its address, instead of
  failing with "cannot take address of expression".
- **Address-of through a crashable's fields** is now supported (it previously
  rejected crashable parents), so returning or copying a `Text`/record field of a
  crashable from `crash_message`/`$diagnose` works.
- **Address-of on inline aggregate records** (`Array`/`BitArray`) now generates
  valid IR for the universal `get_address`/`hijack` machinery, where it previously
  emitted an illegal `[N x T] → ptr` cast.

### Internal

- The dead `set_element_at` intrinsic — superseded by the new in-place element
  store path — was removed.
- Storing an owned value through the in-place element store is now correctly
  treated as an ownership move, so the stored value is no longer torn down twice.

### Groundwork

- This is the prerequisite for two v0.1.0 features: **value-representation atomics**
  (`AtomicS64` is one machine word like `S64`, differing only in its atomic-only
  API) and **C FFI out-parameters** (passing a struct's address to a C function).
  Both require a method to reach its receiver's real storage, which is exactly what
  by-reference `me` provides.

The change is locked by the full end-to-end fixture suite and the analyzer test
suite (1382 tests), all green, with every generated module verified by LLVM.
