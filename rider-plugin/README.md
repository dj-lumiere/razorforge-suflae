# RazorForge / Suflae — Rider (JetBrains) plugin

Brings `.rf` / `.sf` language support into Rider by driving the compiler's built-in language
server (`dotnet RazorForge.dll --lsp`) through the IntelliJ Platform **LSP client API**. This is
the Rider counterpart to `../vscode-extension` — the LSP server is the same process; only the
client that launches it differs.

> The LSP client API (`com.intellij.platform.lsp`) is a **paid-IDE feature**. Rider qualifies;
> the free Community IDEs do not.

## What it does

- Registers the `RazorForge` (`.rf`, `.razorforge`) and `Suflae` (`.sf`) file types.
- On opening such a file, launches `dotnet <RazorForge.dll> --lsp` and connects as an LSP client,
  surfacing the server's diagnostics, hover, go-to-definition, references and completion.

## Prerequisites

1. Build the compiler so the DLL + its sibling `Standard/` stdlib exist:
   ```
   dotnet build       # from the repo root — produces bin/Debug/net10.0/RazorForge.dll
   ```
2. `dotnet` on PATH.
3. A local Gradle (or generate the wrapper — see below).

## Build & run

The wrapper jar is gitignored; generate it once, then use `./gradlew`:

```
cd rider-plugin
gradle wrapper --gradle-version 8.10.2   # one-time; creates gradle/wrapper/gradle-wrapper.jar
./gradlew runIde                         # launches a sandbox Rider with the plugin loaded
```

`runIde` downloads the Rider build named by `platformVersion` in `gradle.properties` — set that
to match your installed Rider (e.g. `2025.1`) and bump `sinceBuild` in `build.gradle.kts` to the
matching branch (`2025.1` -> `251`) if you tighten compatibility.

To package an installable ZIP:

```
./gradlew buildPlugin        # -> build/distributions/razorforge-rider-lsp-0.1.0.zip
```

Install it in your real Rider via **Settings → Plugins → ⚙ → Install Plugin from Disk…**.

## Locating RazorForge.dll

`RazorForgeLspServerDescriptor.resolveServerDll()` looks, in order:

1. `RAZORFORGE_LSP_DLL` — an absolute path to the DLL (set this for a Release build, or when a
   game project references RazorForge from another location).
2. `<project>/bin/Debug/net10.0/RazorForge.dll` — the repo dev build (default).

The server is launched with its working directory set to the DLL's folder so the sibling
`Standard/` stdlib is found; `FORGE_STDLIB` still overrides the stdlib path if you need it.

## Layout

```
rider-plugin/
  build.gradle.kts                     IntelliJ Platform Gradle plugin 2.x, targets Rider
  gradle.properties                    platformVersion (which Rider to build against)
  settings.gradle.kts
  src/main/resources/META-INF/plugin.xml   file types + lsp serverSupportProvider registration
  src/main/kotlin/com/razorforge/lsp/
    Languages.kt                       RazorForge/Suflae Language + LanguageFileType
    RazorForgeLspServerSupportProvider.kt  launches `dotnet RazorForge.dll --lsp`
```
