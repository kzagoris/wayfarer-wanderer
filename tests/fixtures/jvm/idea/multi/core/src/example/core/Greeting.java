package example.core;

public final class Greeting {
    private Greeting() {
    }

    public static String to(String name) {
        return "Hello, " + name;
    }
}
