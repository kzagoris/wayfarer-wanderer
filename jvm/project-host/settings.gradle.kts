// Wayfarer Wanderer's JVM project host: asks Gradle and Maven for a workspace's model in a process of its own.
// host           - the process: protocol on stdio, the evaluators' drivers, IntelliJ modules
// gradle-model   - loaded into the build under evaluation by an init script; Java 8 bytecode
// maven-model    - loaded beside the wrapper-selected Maven's own jars, in a class loader of its own
pluginManagement {
    repositories { gradlePluginPortal() }
}
dependencyResolutionManagement {
    repositoriesMode = RepositoriesMode.FAIL_ON_PROJECT_REPOS
    repositories {
        mavenCentral()
        maven("https://repo.gradle.org/gradle/libs-releases") {
            content { includeGroup("org.gradle") }
        }
    }
}
rootProject.name = "wayfarer-project-host"
include("host", "gradle-model", "maven-model")
