package example.core;

import org.apache.commons.lang3.StringUtils;

public final class Greeting {
    public static String of(String name) {
        return "Hello, " + StringUtils.capitalize(name) + " " + Extra.MARK + Version.VALUE;
    }
}
