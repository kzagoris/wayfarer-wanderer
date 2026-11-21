package example.app;

import example.core.Greeting;

public final class Main {
    private Main() {
    }

    public static void main(String[] args) {
        System.out.println(Greeting.to("multi"));
    }
}
