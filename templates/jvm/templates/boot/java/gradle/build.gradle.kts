plugins { java; id("org.springframework.boot") version "{{SPRING_BOOT_VERSION}}"; id("io.spring.dependency-management") version "1.1.7" }
group = "{{GROUP}}"
version = "1.0.0"
repositories { mavenCentral() }
java { toolchain { languageVersion = JavaLanguageVersion.of({{JDK_VERSION}}) } }
dependencies {
    implementation("org.springframework.boot:spring-boot-starter-webmvc")
    testImplementation("org.springframework.boot:spring-boot-starter-webmvc-test")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}
tasks.test { useJUnitPlatform() }
