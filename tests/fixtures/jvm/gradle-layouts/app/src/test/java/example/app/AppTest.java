package example.app;

import static org.junit.jupiter.api.Assertions.assertEquals;

import example.core.Greeting;
import example.core.Names;
import org.junit.jupiter.api.Test;

class AppTest {
    @Test
    void greets() {
        assertEquals("Hello, Wayfarer v1", Greeting.of(Names.SAMPLE));
    }
}
