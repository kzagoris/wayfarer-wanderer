import com.sun.jdi.*;
import com.sun.jdi.connect.*;
import com.sun.jdi.event.*;
import com.sun.jdi.request.*;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.regex.*;

// The IDE owns the target process. The adapter listens on loopback and the target connects out
// to it (server=n), so the target never exposes a JDWP port. The adapter accepts one connection.
class WayfarerDebugAdapter {
    private VirtualMachine vm;
    private ThreadReference stopped;
    private EventSet suspended;
    private final List<Path> roots = new ArrayList<>();
    private final Map<Path, List<Integer>> breakpoints = new LinkedHashMap<>();
    private final Map<Path, ClassPrepareRequest> onPrepare = new HashMap<>();
    private final Wire wire = new Wire();
    private ListeningConnector listener;
    private Map<String, Connector.Argument> listening;

    public static void main(String[] args) throws Exception {
        new WayfarerDebugAdapter().serve();
    }

    private void serve() throws Exception {
        Map<String, Object> message;
        while ((message = wire.read()) != null) {
            String command = (String) message.get("command");
            synchronized (this) {
                Map<String, Object> response =
                        new LinkedHashMap<>(
                                Map.of(
                                        "type",
                                        "response",
                                        "request_seq",
                                        message.get("seq"),
                                        "command",
                                        command));
                try {
                    response.put("body", request(command, object(message.get("arguments"))));
                    response.put("success", true);
                } catch (Exception ex) {
                    response.put("success", false);
                    response.put("message", ex.toString());
                }
                wire.send(response);
                if (command.equals("attach") && Boolean.TRUE.equals(response.get("success")))
                    event("initialized", Map.of());
                if (command.equals("disconnect")) return;
            }
        }
    }

    private Object request(String command, Map<String, Object> args) throws Exception {
        switch (command) {
            case "initialize":
                return Map.of("supportsConfigurationDoneRequest", true);
            case "listen":
                listener =
                        Bootstrap.virtualMachineManager().listeningConnectors().stream()
                                .filter(c -> c.name().equals("com.sun.jdi.SocketListen"))
                                .findFirst()
                                .orElseThrow();
                listening = listener.defaultArguments();
                listening.get("localAddress").setValue("127.0.0.1");
                listening.get("port").setValue("0");
                listening.get("timeout").setValue("30000");
                String address = listener.startListening(listening);
                return Map.of(
                        "port", Integer.parseInt(address.substring(address.lastIndexOf(':') + 1)));
            case "attach":
                if (listener == null) throw new IllegalStateException("Send listen before attach");
                for (Object root : (List<?>) args.get("sourcePaths"))
                    roots.add(Path.of((String) root));
                try {
                    vm = listener.accept(listening);
                } finally {
                    // One connection only: whoever comes second is refused.
                    listener.stopListening(listening);
                    listener = null;
                }
                break;
            case "setBreakpoints":
                return setBreakpoints(args);
            case "configurationDone":
                Thread events = new Thread(this::events, "wayfarer-jdi-events");
                events.setDaemon(true);
                events.start();
                break;
            case "stackTrace":
                List<Object> frames = new ArrayList<>();
                for (StackFrame frame : stopped.frames()) {
                    Location loc = frame.location();
                    Map<String, Object> item =
                            new LinkedHashMap<>(
                                    Map.of(
                                            "id",
                                            frames.size() + 1,
                                            "name",
                                            loc.declaringType().name() + "." + loc.method().name(),
                                            "line",
                                            Math.max(0, loc.lineNumber()),
                                            "column",
                                            1));
                    Path path = source(loc);
                    if (path != null)
                        item.put(
                                "source",
                                Map.of(
                                        "path",
                                        path.toString(),
                                        "name",
                                        path.getFileName().toString()));
                    frames.add(item);
                }
                return Map.of("stackFrames", frames);
            case "scopes":
                return Map.of(
                        "scopes",
                        List.of(
                                Map.of(
                                        "name",
                                        "Locals",
                                        "expensive",
                                        false,
                                        "variablesReference",
                                        args.get("frameId"))));
            case "variables":
                StackFrame frame =
                        stopped.frame(((Number) args.get("variablesReference")).intValue() - 1);
                List<Object> variables = new ArrayList<>();
                try {
                    for (var entry : frame.getValues(frame.visibleVariables()).entrySet())
                        variables.add(
                                Map.of(
                                        "name",
                                        entry.getKey().name(),
                                        "value",
                                        String.valueOf(entry.getValue()),
                                        "type",
                                        entry.getKey().typeName(),
                                        "variablesReference",
                                        0));
                } catch (AbsentInformationException missing) {
                    /* A class built without a local variable table. */
                }
                return Map.of("variables", variables);
            case "stepIn":
            case "next":
            case "stepOut":
            case "continue":
                if (suspended == null) throw new IllegalStateException("No suspended thread");
                if (!command.equals("continue")) {
                    int depth =
                            command.equals("next")
                                    ? StepRequest.STEP_OVER
                                    : command.equals("stepOut")
                                            ? StepRequest.STEP_OUT
                                            : StepRequest.STEP_INTO;
                    StepRequest step =
                            vm.eventRequestManager()
                                    .createStepRequest(stopped, StepRequest.STEP_LINE, depth);
                    for (String pattern :
                            List.of("java.*", "jdk.*", "sun.*", "kotlin.jvm.internal.*"))
                        step.addClassExclusionFilter(pattern);
                    step.addCountFilter(1);
                    step.enable();
                }
                suspended.resume();
                suspended = null;
                stopped = null;
                break;
            case "disconnect":
                if (vm != null) vm.dispose();
                break;
            default:
                throw new UnsupportedOperationException(command);
        }
        return Map.of();
    }

    private Object setBreakpoints(Map<String, Object> args) throws Exception {
        Path path = Path.of((String) object(args.get("source")).get("path")).normalize();
        var manager = vm.eventRequestManager();
        manager.deleteEventRequests(
                manager.breakpointRequests().stream()
                        .filter(r -> path.equals(r.getProperty("path")))
                        .toList());
        if (onPrepare.containsKey(path)) manager.deleteEventRequest(onPrepare.remove(path));
        List<Integer> lines = new ArrayList<>();
        for (Object point : (List<?>) args.get("breakpoints"))
            lines.add(((Number) object(point).get("line")).intValue());
        breakpoints.remove(path);
        if (!lines.isEmpty()) {
            breakpoints.put(path, lines);
            ClassPrepareRequest prepare = manager.createClassPrepareRequest();
            if (vm.canUseSourceNameFilters())
                prepare.addSourceNameFilter(path.getFileName().toString());
            prepare.setSuspendPolicy(EventRequest.SUSPEND_ALL);
            prepare.enable();
            onPrepare.put(path, prepare);
            for (ReferenceType type : vm.allClasses())
                if (type.isPrepared()) bind(type, path, lines);
        }
        List<Object> result = new ArrayList<>();
        for (int line : lines)
            result.add(
                    Map.of(
                            "verified",
                            manager.breakpointRequests().stream()
                                    .anyMatch(
                                            r ->
                                                    path.equals(r.getProperty("path"))
                                                            && r.location().lineNumber() == line),
                            "line",
                            line));
        return Map.of("breakpoints", result);
    }

    private void bind(ReferenceType type, Path path, List<Integer> lines) throws Exception {
        String name = path.getFileName().toString();
        try {
            if (!type.sourceNames(null).contains(name)) return;
            for (int line : lines) {
                for (Location loc : type.locationsOfLine(null, name, line)) {
                    if (!path.equals(source(loc))) continue;
                    var manager = vm.eventRequestManager();
                    if (manager.breakpointRequests().stream()
                            .anyMatch(r -> r.location().equals(loc))) continue;
                    BreakpointRequest point = manager.createBreakpointRequest(loc);
                    point.putProperty("path", path);
                    point.enable();
                }
            }
        } catch (AbsentInformationException missing) {
            /* No source information. */
        }
    }

    private Path source(Location location) {
        try {
            String relative = location.sourcePath().replace('\\', '/');
            for (Path root : roots) {
                Path path = root.resolve(relative).normalize();
                if (Files.isRegularFile(path)) return path;
            }
            // Kotlin permits directory layouts independent of the package declaration.
            String name = location.sourceName();
            String type = location.declaringType().name();
            String pkg = type.substring(0, Math.max(0, type.lastIndexOf('.')));
            for (Path path : breakpoints.keySet()) {
                if (!path.getFileName().toString().equals(name)) continue;
                Matcher declaration =
                        Pattern.compile("(?m)^\\s*package\\s+([\\w.]+)")
                                .matcher(Files.readString(path));
                if ((declaration.find() ? declaration.group(1) : "").equals(pkg)) return path;
            }
        } catch (AbsentInformationException | IOException ignored) {
        }
        return null;
    }

    private void events() {
        try {
            while (true) {
                EventSet batch = vm.eventQueue().remove();
                synchronized (this) {
                    LocatableEvent stop = null;
                    for (Event e : batch) {
                        if (e instanceof ClassPrepareEvent prepare) {
                            for (var entry : breakpoints.entrySet())
                                bind(prepare.referenceType(), entry.getKey(), entry.getValue());
                        } else if (e instanceof StepEvent || e instanceof BreakpointEvent)
                            stop = (LocatableEvent) e;
                        else if (e instanceof VMDisconnectEvent) return;
                    }
                    if (stop == null) batch.resume();
                    else {
                        // A breakpoint may interrupt an outstanding step.
                        vm.eventRequestManager()
                                .deleteEventRequests(
                                        List.copyOf(vm.eventRequestManager().stepRequests()));
                        suspended = batch;
                        stopped = stop.thread();
                        event(
                                "stopped",
                                Map.of(
                                        "reason",
                                        stop instanceof StepEvent ? "step" : "breakpoint",
                                        "threadId",
                                        stopped.uniqueID(),
                                        "allThreadsStopped",
                                        true));
                    }
                }
            }
        } catch (VMDisconnectedException ignored) {
        } catch (Exception ex) {
            ex.printStackTrace(System.err);
            // Closing the adapter's pipes signals the host to end a target whose event set could
            // otherwise remain suspended after the event loop failed.
            System.exit(1);
        } finally {
            event("terminated", Map.of());
        }
    }

    private void event(String name, Object body) {
        wire.send(new LinkedHashMap<>(Map.of("type", "event", "event", name, "body", body)));
    }

    @SuppressWarnings("unchecked")
    private static Map<String, Object> object(Object value) {
        return value == null ? Map.of() : (Map<String, Object>) value;
    }

    // DAP framing uses byte counts; source paths, program output and variable values may be
    // Unicode.
    private static class Wire {
        private final BufferedInputStream in = new BufferedInputStream(System.in);
        private int seq;

        Map<String, Object> read() throws IOException {
            int length = -1;
            while (true) {
                StringBuilder line = new StringBuilder();
                int c;
                while ((c = in.read()) >= 0 && c != '\n') {
                    if (line.length() > 4096) throw new IOException("Invalid DAP header");
                    if (c != '\r') line.append((char) c);
                }
                if (c < 0) return null;
                if (line.isEmpty()) break;
                if (line.toString().startsWith("Content-Length:"))
                    length = Integer.parseInt(line.substring(15).trim());
            }
            if (length < 0 || length > 8 * 1024 * 1024) throw new IOException("Invalid DAP length");
            return object(
                    new Json(new String(in.readNBytes(length), StandardCharsets.UTF_8)).value());
        }

        synchronized void send(Map<String, Object> msg) {
            msg.put("seq", ++seq);
            byte[] bytes = Json.encode(msg).getBytes(StandardCharsets.UTF_8);
            byte[] header = ("Content-Length: " + bytes.length + "\r\n\r\n").getBytes(StandardCharsets.US_ASCII);
            System.out.write(header, 0, header.length);
            System.out.write(bytes, 0, bytes.length);
            System.out.flush();
        }
    }

    private static class Json {
        private final String s;
        private int i;

        Json(String s) {
            this.s = s;
        }

        private char peek() {
            while (Character.isWhitespace(s.charAt(i))) i++;
            return s.charAt(i);
        }

        private void expect(char c) {
            if (peek() != c) throw new IllegalArgumentException("Expected " + c);
            i++;
        }

        private String string() {
            expect('"');
            StringBuilder text = new StringBuilder();
            while (s.charAt(i) != '"') {
                char c = s.charAt(i++);
                if (c == '\\') {
                    c = s.charAt(i++);
                    c =
                            switch (c) {
                                case 'b' -> '\b';
                                case 'f' -> '\f';
                                case 'n' -> '\n';
                                case 'r' -> '\r';
                                case 't' -> '\t';
                                case 'u' -> {
                                    int value = Integer.parseInt(s.substring(i, i + 4), 16);
                                    i += 4;
                                    yield (char) value;
                                }
                                default -> c;
                            };
                }
                text.append(c);
            }
            i++;
            return text.toString();
        }

        Object value() {
            char c = peek();
            if (c == '"') return string();
            if (c == '{') {
                i++;
                Map<String, Object> result = new LinkedHashMap<>();
                if (peek() != '}')
                    do {
                        String key = string();
                        expect(':');
                        result.put(key, value());
                        if (peek() != ',') break;
                        i++;
                    } while (true);
                expect('}');
                return result;
            }
            if (c == '[') {
                i++;
                List<Object> result = new ArrayList<>();
                if (peek() != ']')
                    do {
                        result.add(value());
                        if (peek() != ',') break;
                        i++;
                    } while (true);
                expect(']');
                return result;
            }
            int begin = i;
            while (i < s.length() && ",]} \t\r\n".indexOf(s.charAt(i)) < 0) i++;
            return switch (s.substring(begin, i)) {
                case "true" -> true;
                case "false" -> false;
                case "null" -> null;
                default -> Long.parseLong(s.substring(begin, i));
            };
        }

        static String encode(Object value) {
            if (value instanceof Map<?, ?> m) {
                StringJoiner j = new StringJoiner(",", "{", "}");
                m.forEach((k, v) -> j.add(encode(k) + ":" + encode(v)));
                return j.toString();
            }
            if (value instanceof Iterable<?> list) {
                StringJoiner j = new StringJoiner(",", "[", "]");
                list.forEach(v -> j.add(encode(v)));
                return j.toString();
            }
            if (!(value instanceof String text)) return String.valueOf(value);
            StringBuilder out = new StringBuilder("\"");
            for (char c : text.toCharArray()) {
                if (c == '"' || c == '\\') out.append('\\').append(c);
                else if (c < 32) out.append(String.format("\\u%04x", (int) c));
                else out.append(c);
            }
            return out.append('"').toString();
        }
    }
}
