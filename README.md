# RazorForge & Suflae

**Two languages. One family. From sweet to sharp, seamlessly.**

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![Status](https://img.shields.io/badge/status-in%20development-orange.svg)

---

## What Are RazorForge and Suflae?

RazorForge and Suflae are modern programming languages designed to provide a smooth gradient from high-productivity
application development to high-performance systems programming.

### RazorForge: "Make Programming Sharp Again"

A precision systems language for absolute control and deterministic performance.

```razorforge
entity ConnectionPool {
    var connections: List<Connection>
    var active_count: S64
    var max_connections: S64
}

routine acquire_connection(pool: Shared<ConnectionPool>) -> Connection? {
    # Scoped access for multiple operations
    hijacking pool as p {
        # Check capacity with pattern matching
        when p.active_count < p.max_connections {
            == true => {
                let conn = Connection()
                p.connections.add_last(steal conn)  # Explicit ownership transfer
                p.active_count += 1
                return conn
            },
            else => return None  # Pool exhausted
        }
    }
}

routine monitor_pool(weak: Tracked<ConnectionPool>) {
    # Try to recover weak reference
    when weak.try_recover() {
        is None => show("Pool was deallocated"),
        else strong => {
            # Inline access for single read
            show(f"Pool stats: {strong.view().active_count}/{strong.view().max_connections}")
        }
    }
}
```

**Use RazorForge for:**

- Systems programming
- Game engines and real-time applications
- Embedded systems and firmware
- Performance-critical hot paths
- Manual memory control

### Suflae: "Make Programming Sweet Again"

A productivity-first language for building modern applications quickly and safely.

```suflae
entity UserCache:
    private cache: Dict<Integer, User>
    private hit_count: Integer
    private miss_count: Integer

public routine UserCache.__create__():
    me.cache = Dict()
    me.hit_count = 0
    me.miss_count = 0

public suspended routine UserCache.fetch_user!(id: Integer) -> User:
    # Check cache first
    when me.cache.try_getitem(id):
        is None:
            me.miss_count += 1
            let user = waitfor http.get(f"/api/users/{id}")
            unless user:
                throw NetworkError()
            me.cache.insert(id, user)
            return user
        else cached:
            me.hit_count += 1
            return cached

routine start():
    # Create an actor (spawns a green thread)
    let cache = UserCache().act()  # Type: Actor<UserCache>

    # Multiple tasks can safely call cache methods concurrently
    # Method calls become messages - no locks needed!
    let user = waitfor cache.fetch_user(12345)
    show(f"Got user: {user.name}")
```

**Use Suflae for:**

- Web applications and APIs
- CLI tools and scripts
- Data processing and analytics
- Business logic
- Rapid prototyping

---

## Philosophy

We believe programming should be **enjoyable, not a battle with the language.**

### Core Principles

- **Total development cost** over raw runtime performance
- **Clear, descriptive words** over obscure historical terms
- **Explicit** over implicit
- **Consistency** over clever inconsistency
- **Honesty** over marketing hype

### The Smooth Gradient Vision

Our mission is to provide a single, continuous path from your first "Hello World" to building world-class systems software. Each step builds naturally on the last, with no walls to climb.

Read more: [RazorForge Philosophy](https://razorforge.lumi-dev.xyz/Philosophy) | [Suflae Philosophy](https://suflae.lumi-dev.xyz/Philosophy)

---

## Quick Start

### Installation

```bash
# Clone the repository
git clone https://git.lumi-dev.xyz/Lumi/razorforge-suflae.git
cd razorforge-suflae

# Build the compiler
dotnet build

# Build native runtime
cd native && mkdir build && cd build
cmake .. && make
```

### Using the Build Tools

```bash
# Create a new RazorForge project
forge new my-project
cd my-project

# Build and run
forge build
forge run

# For Suflae projects
bake new my-app
cd my-app
bake run
```

### Hello World in RazorForge

```razorforge
# hello.rf
import IO/Console

routine start() {
    show("Hello from RazorForge!")
}
```

### Hello World in Suflae

```suflae
# hello.sf
show("Hello from Suflae!")
```

See: [RazorForge Hello World](https://razorforge.lumi-dev.xyz/Hello-World) | [Suflae Hello World](https://suflae.lumi-dev.xyz/Hello-World)

---

## Key Features

### RazorForge

- **Theatrical Memory Model**: Inline tokens (`.view()`, `.hijack()`) + scoped blocks (`viewing`, `hijacking`) + explicit
  ownership (`steal`)
- **No Lifetime Annotations**: Tokens cannot be returned from routines = safety without complexity
- **Pay Only for What You Use**: No hidden costs, explicit tradeoffs
- **Danger Blocks**: Opt-in unsafe operations (`danger!`) for zero-overhead code
- **Five Data Types**: `record`, `resident`, `entity`, `choice`, `variant`
- **Explicit Concurrency**: `Shared<T, Policy>` with `seizing`/`inspecting` for thread-safe access
- **Freestanding Mode**: Bare metal programming without runtime
- **C Subsystem**: Full FFI with C libraries

### Suflae

- **Automatic Memory Management**: RC + GC hybrid (deterministic cleanup for most objects)
- **Arbitrary Precision by Default**: `Integer` and `Decimal` types (no overflow)
- **Actor Model Concurrency**: `.act()` transforms entities into actors with message passing
- **Green Threads**: Lightweight coroutines (millions of actors, no problem)
- **Four Data Types**: `record`, `entity`, `choice`, `variant`
- **Native Performance**: AOT compilation to machine code
- **RazorForge Interop**: Import RazorForge libraries when you need systems control

### Shared Features

- **Pattern Matching**: Powerful `when` expressions with structural matching
- **Rich Collections**: `List`, `Dict`, `Set`, `Deque`, `SortedDict`, `PriorityQueue`, and more
- **Error Handling**: `Maybe<T>`, `Result<T>`, `Lookup<T>` (no exceptions by default)
- **Generics**: Full generic programming with type parameters and protocols
- **Extension Methods**: Add methods to any type from anywhere
- **Modern Syntax**: Clear keywords (`routine`, `entity`, `when`)
- **Unified Standard Library**: Same collections and APIs across both languages

---

## Documentation

### Getting Started

- [RazorForge Hello World](https://razorforge.lumi-dev.xyz/Hello-World) | [Suflae Hello World](https://suflae.lumi-dev.xyz/Hello-World)
- [Choosing Between Languages](https://razorforge.lumi-dev.xyz/Choosing-Language)
- [IDE Support](https://razorforge.lumi-dev.xyz/IDE-Support)

### RazorForge Documentation

- [Memory Model](https://razorforge.lumi-dev.xyz/Memory-Model) — Theatrical memory management (view, hijack, share, steal)
- [Concurrency Model](https://razorforge.lumi-dev.xyz/Concurrency-Model) — `Shared<T, Policy>`, threading, message passing
- [Data Types](https://razorforge.lumi-dev.xyz/Data-Types) — Records, entities, residents, choices, variants, mutants
- [Residents](https://razorforge.lumi-dev.xyz/Residents) — Fixed-size reference types for embedded systems
- [Danger Blocks](https://razorforge.lumi-dev.xyz/Danger-Blocks) — Unsafe operations and raw memory access
- [C Subsystem](https://razorforge.lumi-dev.xyz/C-Subsystem) — FFI and C interop
- [Freestanding Mode](https://razorforge.lumi-dev.xyz/Freestanding-Mode) — Bare metal programming
- [Reality Bender](https://razorforge.lumi-dev.xyz/Reality-Bender) — Advanced low-level manipulation
- [Lock Policies](https://razorforge.lumi-dev.xyz/Lock-Policies) — Mutex, MultiReadLock, RejectEdit
- [Code Style](https://razorforge.lumi-dev.xyz/Code-Style) — Coding conventions
- [Core](https://razorforge.lumi-dev.xyz/Core) — Auto-imported types and primitives

### Suflae Documentation

- [Data Types](https://suflae.lumi-dev.xyz/Data-Types) — Records, entities, choices, variants
- [Concurrency Model](https://suflae.lumi-dev.xyz/Concurrency-Model) — Actor model, `.act()`, green threads, async/await
- [Error Handling](https://suflae.lumi-dev.xyz/Error-Handling) — `Maybe`, `Result`, error propagation
- [Text](https://suflae.lumi-dev.xyz/Text) — String handling and text processing
- [Code Style](https://suflae.lumi-dev.xyz/Code-Style) — Coding conventions
- [Core](https://suflae.lumi-dev.xyz/Core) — Auto-imported types and abstractions

### Language Reference

- [Keyword Comparison](https://razorforge.lumi-dev.xyz/Keyword-Comparison) — RazorForge vs Suflae syntax
- [Numeric Types](https://razorforge.lumi-dev.xyz/Numeric-Types) — `S32`, `F64`, `D128`, `Integer`, `Decimal`
- [Operators](https://razorforge.lumi-dev.xyz/Operators) — All operators reference
- [Comparison Operators](https://razorforge.lumi-dev.xyz/Comparison-Operators) — Equality and ordering
- [Collections](https://razorforge.lumi-dev.xyz/Collections) — `List`, `Dict`, `Set`, sorted collections, fixed-size
- [Pattern Matching](https://razorforge.lumi-dev.xyz/Pattern-Matching) — `when` expressions
- [Generics](https://razorforge.lumi-dev.xyz/Generics) — Generic programming
- [Protocols](https://razorforge.lumi-dev.xyz/Protocols) — Interface definitions
- [Attributes](https://razorforge.lumi-dev.xyz/Attributes) — Metadata and annotations
- [Modules and Imports](https://razorforge.lumi-dev.xyz/Modules-and-Imports) — Module system

### System Reference

- [Build System](https://razorforge.lumi-dev.xyz/Build-System) — `forge` commands, TOML manifests
- [Runtime](https://razorforge.lumi-dev.xyz/Runtime) — Runtime information
- [Console I/O](https://razorforge.lumi-dev.xyz/Console-IO) — Terminal input/output
- [File I/O](https://razorforge.lumi-dev.xyz/File-IO) — File system operations
- [DateTime & Duration](https://razorforge.lumi-dev.xyz/DateTime-Duration) — Time handling
- [Memory Size Literals](https://razorforge.lumi-dev.xyz/Memory-Size-Literals) — Size units (KiB, MiB)

---

## Learning Paths

### Path 1: Application Developer (Suflae First)

1. [Hello World](https://suflae.lumi-dev.xyz/Hello-World) — Your first program
2. [Data Types](https://suflae.lumi-dev.xyz/Data-Types) — Understanding types
3. [Pattern Matching](https://suflae.lumi-dev.xyz/Pattern-Matching) — Control flow
4. [Collections](https://suflae.lumi-dev.xyz/Collections) — Data structures
5. [Concurrency Model](https://suflae.lumi-dev.xyz/Concurrency-Model) — Actor model and green threads

### Path 2: Systems Programmer (RazorForge First)

1. [Hello World](https://razorforge.lumi-dev.xyz/Hello-World) — Your first program
2. [Data Types](https://razorforge.lumi-dev.xyz/Data-Types) — Type system
3. [Memory Model](https://razorforge.lumi-dev.xyz/Memory-Model) — Memory management
4. [Collections](https://razorforge.lumi-dev.xyz/Collections) — Including fixed collections
5. [Concurrency Model](https://razorforge.lumi-dev.xyz/Concurrency-Model) — Threading and explicit locking
6. [Danger Blocks](https://razorforge.lumi-dev.xyz/Danger-Blocks) — Unsafe operations

### Path 3: The Smooth Gradient (Suflae -> RazorForge)

Start with Suflae for productivity, gradually learn RazorForge for control:

1. Master Suflae basics (Path 1)
2. [Choosing Between Languages](https://razorforge.lumi-dev.xyz/Choosing-Language) — When to use each
3. [RazorForge Memory Model](https://razorforge.lumi-dev.xyz/Memory-Model) — Understanding explicit control
4. [Residents](https://razorforge.lumi-dev.xyz/Residents) — Fixed-size reference types
5. [Freestanding Mode](https://razorforge.lumi-dev.xyz/Freestanding-Mode) — Bare metal programming
6. [Reality Bender](https://razorforge.lumi-dev.xyz/Reality-Bender) — Advanced techniques

---

## Examples

### RazorForge: Theatrical Memory Management

RazorForge makes memory operations **visible and explicit** through inline tokens and scoped blocks:

```razorforge
entity Node {
    var value: S32,
    var next: Shared<Node>?,
    var prev: Tracked<Node>?  # Weak to prevent cycles
}

routine process_node(node: Node) {
    # Inline read-only access
    show(node.view().value)

    # Inline mutable access
    node.hijack().value += 1

    # Multiple operations - use scoped syntax
    hijacking node as h {
        h.value += 10
        h.value *= 2

        # Downgrade to read-only within hijack
        viewing h as v {
            show(v.value)
        }
    }

    # Ownership transfer
    let list: List<Node> = List()
    list.add_last(steal node)  # node becomes deadref
}
```

### RazorForge: Thread-Safe Concurrency

RazorForge provides **explicit control** over concurrent access with configurable lock policies:

```razorforge
entity Counter {
    var value: S64
}

routine start() {
    let counter = Counter(value: 0)

    # Create thread-safe reference with MultiReadLock policy
    let shared = counter.share<MultiReadLock>()

    # Spawn reader threads - can run concurrently!
    for i in 0 to 5 {
        let reader = shared  # Clone Arc (atomic increment)
        inspecting reader as r {
            show(f"Thread {i} reads: {r.value}")
        }  # Read lock released automatically
    }

    # Writer thread - exclusive access
    let writer = shared
    seizing writer as w {
        w.value += 1
    }  # Write lock released automatically
}
```

### RazorForge: Lock-Free Atomic Operations

For high-performance scenarios, use **lock-free atomics**:

```razorforge
entity MetricsCollector {
    request_count: Atomic<S64>
    error_count: Atomic<S64>
}

routine track_request(metrics: MetricsCollector, success: Bool) {
    # Lock-free increment (~5-10 CPU cycles)
    metrics.request_count.fetch_add(1)

    unless success {
        metrics.error_count.fetch_add(1)
    }
}
```

### RazorForge: Message Passing with Channels

For clean thread communication, **transfer ownership** via channels:

```razorforge
entity Task {
    id: S32
    data: Text
}

routine start() {
    let (tx, rx) = Channel<Task>()

    # Spawn worker threads
    for worker_id in 0 to 4 {
        let receiver = rx
        loop {
            when receiver.try_receive() {
                is None => break,  # Channel closed
                else task => {
                    show(f"Worker {worker_id} processing task {task.id}")
                    process_task(task)
                }
            }
        }
    }

    # Send tasks (ownership transferred to channel)
    for i in 0 to 100 {
        let task = Task(id: i, data: "payload")
        tx.send(steal task)  # Explicit: task becomes deadref
    }
}
```

### Suflae: Actor Model Concurrency

Suflae uses the **actor model** for safe, simple concurrency:

```suflae
entity Counter:
    var value: S32

routine Counter.increment():
    me.value += 1

routine Counter.get() -> S32:
    return me.value

let counter = Counter(value: 0).act()  # Type: Actor<Counter>

suspended routine increment_worker():
    for j in 0 to 100:
        counter.increment()  # Sends message to actor

suspended routine start():
    # Start 10 workers (fire-and-forget)
    for i in 0 to 10:
        increment_worker()

    # counter.get() will return 1000 (always correct, no races!)
    show(counter.get())  # Output: 1000
```

**Why this works:**

- Each `increment()` is a message sent to the actor
- The actor processes messages one at a time
- No two threads access `value` simultaneously
- Result is always correct, no locks needed!

### Suflae: Async/Await with Tasks

```suflae
suspended routine fetch(url: Text) -> Text:
    return waitfor http.get(url)

suspended routine start():
    # Sequential execution
    let a = waitfor fetch("url1")
    let b = waitfor fetch("url2")

    # Parallel execution
    let t1 = fetch("url1")  # Returns Task<Text>
    let t2 = fetch("url2")  # Returns Task<Text>
    let (a, b) = waitfor (t1, t2)  # Wait for both
```

---

## Concurrency Comparison

| Aspect           | Suflae (Actor Model)      | RazorForge (Primitives)                          |
|------------------|---------------------------|--------------------------------------------------|
| **Philosophy**   | "Just use actors"         | "Choose your primitive"                          |
| **Primitives**   | Actors only               | Atomics, Mutex, MultiReadLock, Channels          |
| **Syntax**       | `counter.increment()`     | `seizing counter as c { c.increment() }`         |
| **Field Access** | Forbidden on actors       | Allowed in lock scope                            |
| **Channels**     | Not exposed (internal)    | Exposed for direct use                           |
| **Atomics**      | Not exposed               | Exposed for lock-free ops                        |
| **Control**      | Automatic/implicit        | Explicit/manual                                  |
| **Use Case**     | Applications, scripts     | Systems programming                              |

---

## Project Structure

```
RazorForge/
├── src/              # Compiler source code
│   ├── Lexer/        # Tokenization
│   ├── Parser/       # Parsing (RazorForge & Suflae)
│   ├── AST/          # Abstract syntax tree
│   ├── Analysis/     # Semantic analysis
│   └── CodeGen/      # LLVM code generation
├── native/           # Native runtime components
├── extensions/       # IDE extensions
│   └── razorforge/   # VSCode extension
├── tests/            # Test files
├── RazorForge-Wiki/  # RazorForge documentation
├── Suflae-Wiki/      # Suflae documentation
└── docs/             # Technical documentation
```

---

## Building from Source

### Prerequisites

- .NET SDK 10.0 or later
- LLVM 15+ (for code generation)
- CMake 3.20+ (for native components)
- C compiler (GCC, Clang, or MSVC)

### Build Steps

```bash
# Clone the repository
git clone https://git.lumi-dev.xyz/Lumi/razorforge-suflae.git
cd razorforge-suflae

# Build the compiler
dotnet build

# Run tests
dotnet test

# Build native runtime (required for execution)
cd native
mkdir build && cd build
cmake ..
make  # or 'cmake --build .' on Windows
```

See [Build System](https://razorforge.lumi-dev.xyz/Build-System) for detailed build configuration.

---

## Roadmap

### Version 0.1 (Current)

- Lexer and parser for both languages
- Memory model implementation (inline tokens, scoped access, RC)
- LLVM code generation
- Pattern matching (`when` expressions)
- Collections (List, Dict, Set, etc.)
- Error handling (`Maybe`, `Result`, `Lookup`)
- Standard library expansion (in progress)
- Module system refinement (in progress)

### Version 0.2 (Next)

- Full generics implementation
- Complete C FFI support (C Subsystem)
- Freestanding mode for bare metal
- Full async/await for Suflae (`suspended`/`waitfor`)
- Package manager (`forge` CLI tool)
- Language server protocol (LSP)
- VSCode extensions
- Reflection system

### Future

- Self-hosting compiler (compiler written in RazorForge)
- Native debugging support (DWARF/PDB generation)
- WASM backend
- Formal verification tools
- Advanced SIMD support
- Reality Bender toolkit (ObjectHacker, Mutant, Metamorph)

---

## Contributing

We welcome contributions! Whether it's:

- Bug reports
- Feature suggestions
- Documentation improvements
- Code contributions

---

## License

RazorForge and Suflae are dual-licensed under:

- MIT License
- Apache License 2.0

Choose the license that works best for your project.

---

## Community

- **Gitea**: [git.lumi-dev.xyz/Lumi/razorforge-suflae](https://git.lumi-dev.xyz/Lumi/razorforge-suflae)
- **Issues**: [Report bugs or request features](https://git.lumi-dev.xyz/Lumi/razorforge-suflae/issues)
- **GitHub Mirror**: [github.com/dj-lumiere/razorforge-lang](https://github.com/dj-lumiere/razorforge-lang)
- **RazorForge Docs**: [razorforge.lumi-dev.xyz](https://razorforge.lumi-dev.xyz/)
- **Suflae Docs**: [suflae.lumi-dev.xyz](https://suflae.lumi-dev.xyz/)

---

## Acknowledgments

RazorForge and Suflae are inspired by:

- **Rust** — Memory safety without garbage collection
- **Python** — Readable syntax and developer happiness
- **Zig** — Explicit control and simplicity
- **C#** — Modern language design and tooling
- **Erlang/Elixir** — Actor model concurrency (Suflae)
- **Swift** — Actor model implementation

But we believe we can do better by:

- Being honest about tradeoffs (no "zero-cost" marketing lies)
- Focusing on total development cost (not just runtime performance)
- Providing a smooth gradient (not a wall) between high-level and low-level programming
- Making memory management theatrical and explicit (not hidden or complex)

---

**Happy coding!**

*"From sweet to sharp, seamlessly."*
