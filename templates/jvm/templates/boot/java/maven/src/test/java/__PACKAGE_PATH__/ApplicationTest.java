package {{PACKAGE}};
import static org.junit.jupiter.api.Assertions.assertEquals;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.test.web.client.TestRestTemplate;
@SpringBootTest(webEnvironment = SpringBootTest.WebEnvironment.RANDOM_PORT)
class ApplicationTest {
    @Autowired TestRestTemplate client;
    @Test void servesGreeting() { assertEquals("Hello from {{NAME}}!", client.getForObject("/", String.class)); }
}
