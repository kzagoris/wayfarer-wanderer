plugins { kotlin("jvm") version "{{KOTLIN_VERSION}}"; kotlin("plugin.spring") version "{{KOTLIN_VERSION}}"; id("org.springframework.boot") version "{{SPRING_BOOT_VERSION}}"; id("io.spring.dependency-management") version "1.1.7" }
group = "{{GROUP}}"
version = "1.0.0"
repositories { mavenCentral() }
kotlin { jvmToolchain({{JDK_VERSION}}) }
dependencies {
    implementation("org.springframework.boot:spring-boot-starter-webmvc")
    implementation("tools.jackson.module:jackson-module-kotlin")
    implementation("org.jetbrains.kotlin:kotlin-reflect")
    testImplementation("org.springframework.boot:spring-boot-starter-webmvc-test")
    testImplementation("org.jetbrains.kotlin:kotlin-test-junit5")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}
tasks.test { useJUnitPlatform() }
