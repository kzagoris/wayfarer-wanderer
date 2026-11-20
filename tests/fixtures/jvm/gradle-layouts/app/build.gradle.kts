plugins {
    application
}
java { toolchain { languageVersion = JavaLanguageVersion.of(22) } }
application { mainClass = "example.app.App" }

dependencies {
    implementation(project(":core"))
    testImplementation(testFixtures(project(":core")))
    testImplementation("org.junit.jupiter:junit-jupiter:5.13.4")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}
tasks.test { useJUnitPlatform() }
