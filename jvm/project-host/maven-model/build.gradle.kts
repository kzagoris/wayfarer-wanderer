plugins { java }

// Loaded in the host's JVM beside the wrapper-selected Maven's own lib folder, in a class loader of its own.
tasks.named<JavaCompile>("compileJava") { options.release = 21 }

dependencies {
    // Compiled against Maven 3.9's API; at runtime the workspace's own Maven supplies these classes.
    compileOnly("org.apache.maven:maven-core:3.9.11")
    compileOnly("org.apache.maven:maven-embedder:3.9.11")
}
