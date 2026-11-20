subprojects {
    apply(plugin = "java")
    dependencyLocking { lockAllConfigurations() }
    tasks.withType<JavaCompile>().configureEach {
        options.encoding = "UTF-8"
        options.compilerArgs.addAll(listOf("-Xlint:all,-options,-processing", "-Werror"))
    }
    tasks.withType<AbstractArchiveTask>().configureEach {
        isPreserveFileTimestamps = false
        isReproducibleFileOrder = true
    }
}

// Stages the host, its runtime dependencies and the two evaluator jars into one folder:
// ./gradlew stage -PstageDir=<folder>
tasks.register<Sync>("stage") {
    val target = providers.gradleProperty("stageDir").orElse(layout.buildDirectory.dir("stage").map { it.asFile.path })
    into(target)
    from(project(":host").tasks.named("jar")) { rename { "host.jar" } }
    from(project(":host").configurations.named("runtimeClasspath")) { into("lib") }
    from(project(":maven-model").tasks.named("jar")) { rename { "maven-model.jar" } }
}
