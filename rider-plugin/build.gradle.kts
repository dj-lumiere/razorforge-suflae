import org.jetbrains.intellij.platform.gradle.IntelliJPlatformType

plugins {
    id("java")
    id("org.jetbrains.kotlin.jvm") version "2.1.0"
    id("org.jetbrains.intellij.platform") version "2.2.1"
}

group = "com.razorforge"
version = "0.1.0"

repositories {
    mavenCentral()
    intellijPlatform {
        defaultRepositories()
    }
}

dependencies {
    intellijPlatform {
        // Target Rider. The LSP client API (com.intellij.platform.lsp.*) ships inside the
        // platform for paid IDEs, so no extra artifact is needed — just compile against Rider.
        create(IntelliJPlatformType.Rider, providers.gradleProperty("platformVersion"))
    }
}

intellijPlatform {
    // This is a pure-Kotlin plugin with no IntelliJ UI (.form) files, so form/NotNull instrumentation
    // is unnecessary — and its `java-compiler-ant-tasks` artifact for older builds (243) is no longer in
    // the JetBrains maven repos, which fails `:instrumentCode`. Turning it off both fixes that and speeds
    // the build.
    instrumentCode = false

    pluginConfiguration {
        // sinceBuild is the branch of platformVersion (2024.3 -> 243). untilBuild is left open so
        // a minor Rider bump doesn't disable the plugin; tighten if the LSP API changes under you.
        ideaVersion {
            sinceBuild = "243"
            untilBuild = provider { null }
        }
    }
}

kotlin {
    jvmToolchain(21)
}
