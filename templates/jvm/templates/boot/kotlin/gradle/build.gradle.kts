plugins { kotlin("jvm") version "2.4.20"; kotlin("plugin.spring") version "2.4.20"; id("org.springframework.boot") version "3.5.7"; id("io.spring.dependency-management") version "1.1.7" }
group = "{{GROUP}}"
version = "1.0.0"
repositories { mavenCentral() }
kotlin { jvmToolchain({{JDK_VERSION}}) }
dependencies {
    implementation("org.springframework.boot:spring-boot-starter-web")
    implementation("com.fasterxml.jackson.module:jackson-module-kotlin")
    implementation("org.jetbrains.kotlin:kotlin-reflect")
    testImplementation("org.springframework.boot:spring-boot-starter-test")
    testImplementation("org.jetbrains.kotlin:kotlin-test-junit5")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}
tasks.test { useJUnitPlatform() }
