plugins { application; kotlin("jvm") version "2.4.20" }
group = "{{GROUP}}"
version = "1.0.0"
repositories { mavenCentral() }
kotlin { jvmToolchain({{JDK_VERSION}}) }
dependencies {
    testImplementation("org.junit.jupiter:junit-jupiter:5.12.2")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}
application { mainClass = "{{PACKAGE}}.AppKt" }
tasks.test { useJUnitPlatform() }
