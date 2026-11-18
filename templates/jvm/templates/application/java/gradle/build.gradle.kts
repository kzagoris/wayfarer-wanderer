plugins { java; application }
group = "{{GROUP}}"
version = "1.0.0"
repositories { mavenCentral() }
java { toolchain { languageVersion = JavaLanguageVersion.of({{JDK_VERSION}}) } }
dependencies {
    testImplementation("org.junit.jupiter:junit-jupiter:5.12.2")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}
application { mainClass = "{{PACKAGE}}.App" }
tasks.test { useJUnitPlatform() }
