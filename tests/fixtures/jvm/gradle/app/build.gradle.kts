plugins {
    application
    kotlin("jvm") version "2.4.20"
}
dependencies { implementation(project(":library")) }
application { mainClass.set("example.MainKt") }
