plugins { java }

// Runs inside the build under evaluation, on whatever JVM its daemon uses (Gradle 8.0 supports Java 8 and later).
tasks.named<JavaCompile>("compileJava") { options.release = 8 }

dependencies {
    // Gradle's own API, from the distribution that builds the host; only API present since Gradle 8.0 is used.
    compileOnly(gradleApi())
    compileOnly("org.gradle:gradle-tooling-api:9.7.1")
}
