plugins { java }

// The launcher checks the JVM it was started on before loading anything that needs Java 21,
// so even a JVM that is too old answers with a readable first line.
val launcher: SourceSet = sourceSets.create("launcher")


tasks.named<JavaCompile>("compileLauncherJava") { options.release = 8 }
tasks.named<JavaCompile>("compileJava") { options.release = 21 }

dependencies {
    implementation(project(":gradle-model"))
    implementation("com.google.code.gson:gson:2.13.1")
    implementation("org.gradle:gradle-tooling-api:9.7.1")
    // Resolves IntelliJ repository libraries (a maven-id whose jars are not on disk) without a project's own Maven.
    implementation("org.apache.maven.resolver:maven-resolver-supplier:1.9.27")
    // The Tooling API logs through SLF4J; the simple binding writes to standard error, never to the protocol.
    runtimeOnly("org.slf4j:slf4j-simple:2.0.17")
    testImplementation("org.junit.jupiter:junit-jupiter:5.13.4")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}

tasks.test {
    useJUnitPlatform()
    // The protocol's golden examples, shared with the C# tests.
    systemProperty("wayfarer.protocolExamples", rootProject.file("protocol").absolutePath)
}

tasks.jar {
    from(launcher.output)
    manifest {
        attributes(
            "Main-Class" to "wayfarer.host.Launcher",
            "Class-Path" to provider { configurations.runtimeClasspath.get().files.joinToString(" ") { "lib/" + it.name } },
        )
    }
}
