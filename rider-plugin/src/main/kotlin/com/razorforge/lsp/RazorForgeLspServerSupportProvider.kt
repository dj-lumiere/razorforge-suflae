package com.razorforge.lsp

import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspServerSupportProvider
import com.intellij.platform.lsp.api.ProjectWideLspServerDescriptor
import java.io.File
import java.nio.charset.StandardCharsets

private val LOG = logger<RazorForgeLspServerSupportProvider>()

private val SUPPORTED_EXTENSIONS = setOf("rf", "razorforge", "sf")

private fun VirtualFile.isRazorForgeSource(): Boolean =
    extension?.lowercase() in SUPPORTED_EXTENSIONS

/**
 * Entry point Rider calls whenever a file is opened. When it is a RazorForge/Suflae source, we
 * ensure the shared per-project language server is running. This mirrors what the VS Code
 * extension's LanguageClient does — it just launches `dotnet RazorForge.dll --lsp` and speaks
 * LSP over the process's stdio.
 */
class RazorForgeLspServerSupportProvider : LspServerSupportProvider {
    override fun fileOpened(
        project: Project,
        file: VirtualFile,
        serverStarter: LspServerSupportProvider.LspServerStarter
    ) {
        if (!file.isRazorForgeSource()) return
        serverStarter.ensureServerStarted(RazorForgeLspServerDescriptor(project))
    }
}

/**
 * Describes the one language server shared across the whole project (RF/SF analysis is
 * whole-program). Locates the compiler DLL, then runs it under `dotnet` with the `--lsp` verb.
 */
class RazorForgeLspServerDescriptor(project: Project) :
    ProjectWideLspServerDescriptor(project, "RazorForge") {

    override fun isSupportedFile(file: VirtualFile): Boolean = file.isRazorForgeSource()

    override fun createCommandLine(): GeneralCommandLine {
        val dll = resolveServerDll()
        LOG.info("Starting RazorForge language server: dotnet \"$dll\" --lsp")

        return GeneralCommandLine("dotnet", dll.absolutePath, "--lsp").apply {
            // Run from the DLL's own folder so its sibling `Standard/` stdlib is found; the server
            // also honors the FORGE_STDLIB env override if you point it elsewhere.
            withWorkDirectory(dll.parentFile)
            // The server frames JSON-RPC as UTF-8 and forwards RF program output as raw UTF-8.
            charset = StandardCharsets.UTF_8
        }
    }

    /**
     * Finds `RazorForge.dll`, in order:
     *   1. the `RAZORFORGE_LSP_DLL` env var (absolute path to the DLL), then
     *   2. the repo dev build at `<project>/bin/Debug/net10.0/RazorForge.dll`.
     * Adjust #2 or set the env var if your layout differs (e.g. a Release build, or a game
     * project that references RazorForge from elsewhere).
     */
    private fun resolveServerDll(): File {
        System.getenv("RAZORFORGE_LSP_DLL")
            ?.takeIf { it.isNotBlank() }
            ?.let { return File(it) }

        val base = project.basePath
            ?: error("Cannot locate RazorForge.dll: the project has no base path. " +
                "Set the RAZORFORGE_LSP_DLL environment variable to the DLL path.")

        val devBuild = File(base, "bin/Debug/net10.0/RazorForge.dll")
        if (!devBuild.exists()) {
            LOG.warn("RazorForge.dll not found at ${devBuild.absolutePath} — " +
                "build the compiler (dotnet build) or set RAZORFORGE_LSP_DLL.")
        }
        return devBuild
    }
}
