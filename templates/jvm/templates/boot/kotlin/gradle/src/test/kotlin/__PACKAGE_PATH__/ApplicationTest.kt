package {{PACKAGE}}
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test
import org.springframework.boot.test.context.SpringBootTest
import org.springframework.boot.test.web.client.TestRestTemplate
import org.springframework.boot.test.web.server.LocalServerPort
@SpringBootTest(webEnvironment = SpringBootTest.WebEnvironment.RANDOM_PORT)
class ApplicationTest {
 @LocalServerPort private var port: Int = 0
 @Test fun servesGreeting() { assertEquals("Hello from {{NAME}}!", TestRestTemplate().getForObject("http://localhost:$port/", String::class.java)) }
}
