package {{PACKAGE}}
import org.junit.jupiter.api.Test
import org.springframework.beans.factory.annotation.Autowired
import org.springframework.boot.resttestclient.autoconfigure.AutoConfigureRestTestClient
import org.springframework.boot.test.context.SpringBootTest
import org.springframework.test.web.servlet.client.RestTestClient
@SpringBootTest(webEnvironment = SpringBootTest.WebEnvironment.RANDOM_PORT)
@AutoConfigureRestTestClient
class ApplicationTest {
    @Autowired private lateinit var client: RestTestClient
    @Test fun servesGreeting() { client.get().uri("/").exchange().expectStatus().isOk().expectBody(String::class.java).isEqualTo("Hello from {{NAME}}!") }
}
