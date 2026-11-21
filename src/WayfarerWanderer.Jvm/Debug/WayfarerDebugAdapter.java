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
    private static final int MAX_CHILDREN = 1000;
    private static final int MAX_PREVIEW = 20;
    private static final Set<String> BOXES =
            Set.of(
                    "java.lang.Integer",
                    "java.lang.Long",
                    "java.lang.Short",
                    "java.lang.Byte",
                    "java.lang.Double",
                    "java.lang.Float",
                    "java.lang.Boolean",
                    "java.lang.Character");

    private VirtualMachine vm;
    private ThreadReference stopped;
    private EventSet suspended;
    // Paused by the person (vm.suspend()), not by an event: resumed with vm.resume().
    private boolean paused;
    private final List<Path> roots = new ArrayList<>();
    private final Map<Path, List<Integer>> breakpoints = new LinkedHashMap<>();
    private final Map<Path, ClassPrepareRequest> onPrepare = new HashMap<>();
    // Frames and objects shown while the target is suspended; forgotten when it resumes.
    private final Map<Integer, Object> handles = new HashMap<>();
    private int nextHandle = 1;
    private final Wire wire = new Wire();
    private ListeningConnector listener;
    private Map<String, Connector.Argument> listening;

    private record FrameRef(ThreadReference thread, int index) {
        StackFrame frame() throws IncompatibleThreadStateException {
            return thread.frame(index);
        }
    }

    public static void main(String[] args) throws Exception {
        new WayfarerDebugAdapter().serve();
    }

    private void serve() throws Exception {
        Map<String, Object> message;
        while ((message = wire.read()) != null) {
            String command = (String) message.get("command");
            synchronized (this) {
                Map<String, Object> response =
                        map("type", "response", "request_seq", message.get("seq"), "command", command);
                try {
                    response.put("body", request(command, object(message.get("arguments"))));
                    response.put("success", true);
                } catch (Exception ex) {
                    response.put("success", false);
                    response.put("message", message(ex));
                }
                wire.send(response);
                if (command.equals("attach") && Boolean.TRUE.equals(response.get("success")))
                    event("initialized", Map.of());
                if (command.equals("disconnect")) return;
            }
        }
    }

    private static String message(Exception ex) {
        if (ex instanceof EvaluationException) return ex.getMessage();
        if (ex instanceof InvocationException invoked)
            return "Method threw '" + invoked.exception().referenceType().name() + "' exception.";
        return ex.getMessage() == null ? ex.toString() : ex.getMessage();
    }

    private static Map<String, Object> map(Object... pairs) {
        Map<String, Object> result = new LinkedHashMap<>();
        for (int i = 0; i < pairs.length; i += 2) result.put((String) pairs[i], pairs[i + 1]);
        return result;
    }

    private int handle(Object value) {
        int id = nextHandle++;
        handles.put(id, value);
        return id;
    }

    private Object request(String command, Map<String, Object> args) throws Exception {
        switch (command) {
            case "initialize":
                return map(
                        "supportsConfigurationDoneRequest", true,
                        "supportsEvaluateForHovers", true);
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
            case "threads":
                return Map.of("threads", threads());
            case "stackTrace":
                return Map.of("stackFrames", frames(thread(args)));
            case "scopes":
                Object frame = handles.get(((Number) args.get("frameId")).intValue());
                if (!(frame instanceof FrameRef)) throw new IllegalStateException("The frame is gone");
                return Map.of(
                        "scopes",
                        List.of(
                                map(
                                        "name", "Locals",
                                        "expensive", false,
                                        "variablesReference", args.get("frameId"))));
            case "variables":
                return Map.of(
                        "variables",
                        variables(handles.get(((Number) args.get("variablesReference")).intValue())));
            case "evaluate":
                return evaluate(args);
            case "pause":
                pause();
                break;
            case "stepIn":
            case "next":
            case "stepOut":
            case "continue":
                resume(command, args);
                break;
            case "disconnect":
                if (vm != null) vm.dispose();
                break;
            default:
                throw new UnsupportedOperationException(command);
        }
        return Map.of();
    }

    private List<Object> threads() {
        List<Object> result = new ArrayList<>();
        for (ThreadReference thread : vm.allThreads()) {
            String group = "";
            try {
                if (thread.threadGroup() != null) group = thread.threadGroup().name();
            } catch (ObjectCollectedException | VMDisconnectedException ignored) {
            }
            result.add(
                    map(
                            "id", thread.uniqueID(),
                            "name", thread.name(),
                            "group", group,
                            "status", status(thread),
                            "suspended", thread.isSuspended()));
        }
        return result;
    }

    private static String status(ThreadReference thread) {
        return switch (thread.status()) {
            case ThreadReference.THREAD_STATUS_RUNNING -> "RUNNING";
            case ThreadReference.THREAD_STATUS_SLEEPING -> "SLEEPING";
            case ThreadReference.THREAD_STATUS_WAIT -> "WAIT";
            case ThreadReference.THREAD_STATUS_MONITOR -> "MONITOR";
            case ThreadReference.THREAD_STATUS_ZOMBIE -> "ZOMBIE";
            case ThreadReference.THREAD_STATUS_NOT_STARTED -> "NOT STARTED";
            default -> "UNKNOWN";
        };
    }

    private ThreadReference thread(Map<String, Object> args) {
        Object id = args.get("threadId");
        if (id instanceof Number number) {
            for (ThreadReference thread : vm.allThreads())
                if (thread.uniqueID() == number.longValue()) return thread;
        }
        if (stopped != null) return stopped;
        throw new IllegalStateException("No such thread");
    }

    private List<Object> frames(ThreadReference thread) throws Exception {
        List<Object> frames = new ArrayList<>();
        if (!thread.isSuspended()) return frames;
        List<StackFrame> stack = thread.frames();
        for (int i = 0; i < stack.size(); i++) {
            Location loc = stack.get(i).location();
            Map<String, Object> item =
                    map(
                            "id", handle(new FrameRef(thread, i)),
                            "name", loc.declaringType().name() + "." + loc.method().name(),
                            "method", loc.method().name(),
                            "type", loc.declaringType().name(),
                            "line", Math.max(0, loc.lineNumber()),
                            "column", 1);
            Path path = source(loc);
            if (path != null)
                item.put("source", map("path", path.toString(), "name", path.getFileName().toString()));
            frames.add(item);
        }
        return frames;
    }

    private List<Object> variables(Object owner) throws Exception {
        List<Object> result = new ArrayList<>();
        if (owner instanceof FrameRef ref) {
            StackFrame frame = ref.frame();
            ObjectReference self = frame.thisObject();
            if (self != null) result.add(variable("this", self, self.referenceType().name()));
            try {
                for (LocalVariable local : frame.visibleVariables())
                    result.add(variable(local.name(), frame.getValue(local), local.typeName()));
            } catch (AbsentInformationException missing) {
                /* A class built without a local variable table. */
            }
        } else if (owner instanceof ArrayReference array) {
            int length = array.length();
            int shown = Math.min(length, MAX_CHILDREN);
            String element = ((ArrayType) array.referenceType()).componentTypeName();
            List<Value> values = shown == 0 ? List.of() : array.getValues(0, shown);
            for (int i = 0; i < shown; i++) result.add(variable("[" + i + "]", values.get(i), element));
            if (length > shown)
                result.add(map("name", "...", "value", (length - shown) + " more", "type", "", "variablesReference", 0));
        } else if (owner instanceof ObjectReference object) {
            for (Field field : object.referenceType().allFields())
                if (!field.isStatic())
                    result.add(variable(field.name(), object.getValue(field), field.typeName()));
        }
        return result;
    }

    private Map<String, Object> variable(String name, Value value, String declared) {
        return map(
                "name", name,
                "value", format(value),
                "type", value == null ? declared : value.type().name(),
                "variablesReference", hasChildren(value) ? handle(value) : 0);
    }

    private static boolean hasChildren(Value value) {
        if (value instanceof ArrayReference array) return array.length() > 0;
        if (value instanceof StringReference || !(value instanceof ObjectReference object)) return false;
        if (BOXES.contains(object.referenceType().name())) return false;
        return object.referenceType().allFields().stream().anyMatch(f -> !f.isStatic());
    }

    private static String simple(String type) {
        return type.substring(type.lastIndexOf('.') + 1);
    }

    static String format(Value value) {
        if (value == null) return "null";
        if (value instanceof StringReference text) return quote(text.value(), '"');
        if (value instanceof CharValue c) return quote(String.valueOf(c.value()), '\'');
        if (value instanceof PrimitiveValue) return value.toString();
        if (value instanceof VoidValue) return "void";
        ObjectReference object = (ObjectReference) value;
        if (object instanceof ArrayReference array) {
            String type = simple(array.referenceType().name());
            int bracket = type.indexOf('[');
            StringBuilder text =
                    new StringBuilder("{")
                            .append(type, 0, bracket + 1)
                            .append(array.length())
                            .append(type.substring(bracket + 1))
                            .append("@")
                            .append(object.uniqueID())
                            .append("} [");
            int shown = Math.min(array.length(), MAX_PREVIEW);
            List<Value> values = shown == 0 ? List.of() : array.getValues(0, shown);
            for (int i = 0; i < shown; i++) {
                if (i > 0) text.append(", ");
                Value element = values.get(i);
                text.append(
                        element instanceof ObjectReference o && !(element instanceof StringReference)
                                ? "{" + simple(o.referenceType().name()) + "@" + o.uniqueID() + "}"
                                : format(element));
            }
            if (array.length() > shown) text.append(", ...");
            return text.append("]").toString();
        }
        ReferenceType type = object.referenceType();
        String head = "{" + simple(type.name()) + "@" + object.uniqueID() + "}";
        try {
            if (BOXES.contains(type.name())) return head + " " + format(object.getValue(type.fieldByName("value")));
            if (type instanceof ClassType kind && kind.isEnum()) {
                Field name = kind.superclass().fieldByName("name");
                if (name != null) return head + " " + format(object.getValue(name));
            }
        } catch (RuntimeException ignored) {
        }
        return head;
    }

    private static String quote(String text, char mark) {
        StringBuilder out = new StringBuilder().append(mark);
        int limit = Math.min(text.length(), 1000);
        for (int i = 0; i < limit; i++) {
            char c = text.charAt(i);
            switch (c) {
                case '\n' -> out.append("\\n");
                case '\r' -> out.append("\\r");
                case '\t' -> out.append("\\t");
                case '\\' -> out.append("\\\\");
                default -> {
                    if (c == mark) out.append('\\');
                    out.append(c);
                }
            }
        }
        if (text.length() > limit) out.append("...");
        return out.append(mark).toString();
    }

    private void pause() {
        if (suspended != null || paused) return;
        vm.suspend();
        paused = true;
        ThreadReference chosen = null;
        for (ThreadReference thread : vm.allThreads()) {
            try {
                if (thread.frameCount() == 0) continue;
                String group = thread.threadGroup() == null ? "" : thread.threadGroup().name();
                if (thread.name().equals("main")) {
                    chosen = thread;
                    break;
                }
                if (chosen == null && !group.equals("system")) chosen = thread;
            } catch (IncompatibleThreadStateException | ObjectCollectedException ignored) {
            }
        }
        if (chosen == null && !vm.allThreads().isEmpty()) chosen = vm.allThreads().get(0);
        stopped = chosen;
        event(
                "stopped",
                map(
                        "reason", "pause",
                        "threadId", chosen == null ? 0L : chosen.uniqueID(),
                        "allThreadsStopped", true));
    }

    private void resume(String command, Map<String, Object> args) {
        if (suspended == null && !paused) throw new IllegalStateException("No suspended thread");
        ThreadReference thread = thread(args);
        var manager = vm.eventRequestManager();
        manager.deleteEventRequests(List.copyOf(manager.stepRequests()));
        if (!command.equals("continue")) {
            int depth =
                    command.equals("next")
                            ? StepRequest.STEP_OVER
                            : command.equals("stepOut") ? StepRequest.STEP_OUT : StepRequest.STEP_INTO;
            StepRequest step = manager.createStepRequest(thread, StepRequest.STEP_LINE, depth);
            for (String pattern : List.of("java.*", "javax.*", "jdk.*", "sun.*", "com.sun.*", "kotlin.jvm.internal.*"))
                step.addClassExclusionFilter(pattern);
            step.addCountFilter(1);
            step.enable();
        }
        handles.clear();
        if (paused) {
            paused = false;
            vm.resume();
        } else {
            suspended.resume();
        }
        suspended = null;
        stopped = null;
    }

    // Evaluate: names (locals, then this' fields, then the frame's class' statics), field
    // access, array indexing and .length, literals, and method calls on a thread stopped by an
    // event.
    private static final class EvaluationException extends Exception {
        EvaluationException(String message) {
            super(message);
        }
    }

    private Object evaluate(Map<String, Object> args) throws Exception {
        String expression = String.valueOf(args.get("expression")).trim();
        if (expression.isEmpty()) throw new EvaluationException("Nothing to evaluate");
        Object frameId = args.get("frameId");
        FrameRef ref = frameId instanceof Number n && handles.get(n.intValue()) instanceof FrameRef f ? f : null;
        if (ref == null) {
            if (stopped == null) throw new EvaluationException("The program is running");
            ref = new FrameRef(stopped, 0);
        }
        Evaluator evaluator = new Evaluator(expression, ref);
        Value value = evaluator.whole();
        return map(
                "result", format(value),
                "type", value == null ? "null" : value.type().name(),
                "variablesReference", hasChildren(value) ? handle(value) : 0);
    }

    private final class Evaluator {
        private final String text;
        private final FrameRef ref;
        private int at;

        Evaluator(String text, FrameRef ref) {
            this.text = text;
            this.ref = ref;
        }

        Value whole() throws Exception {
            Value value = expression();
            space();
            if (at < text.length()) throw new EvaluationException("Unexpected '" + text.substring(at) + "'");
            return value;
        }

        private void space() {
            while (at < text.length() && Character.isWhitespace(text.charAt(at))) at++;
        }

        private boolean take(char c) {
            space();
            if (at < text.length() && text.charAt(at) == c) {
                at++;
                return true;
            }
            return false;
        }

        private String name() throws EvaluationException {
            space();
            int begin = at;
            while (at < text.length() && Character.isJavaIdentifierPart(text.charAt(at))) at++;
            if (begin == at) throw new EvaluationException("Expected a name at '" + text.substring(begin) + "'");
            return text.substring(begin, at);
        }

        private Value expression() throws Exception {
            Value value = primary();
            while (true) {
                if (take('.')) {
                    String member = name();
                    if (take('(')) value = call(value, null, member, arguments());
                    else value = member(value, member);
                } else if (take('[')) {
                    Value index = expression();
                    if (!take(']')) throw new EvaluationException("Expected ']'");
                    if (!(value instanceof ArrayReference array))
                        throw new EvaluationException("Array type expected");
                    if (!(index instanceof PrimitiveValue number) || index instanceof BooleanValue)
                        throw new EvaluationException("Index must be a number");
                    int i = number.intValue();
                    if (i < 0 || i >= array.length())
                        throw new EvaluationException("Index " + i + " out of bounds for length " + array.length());
                    value = array.getValue(i);
                } else return value;
            }
        }

        private List<Value> arguments() throws Exception {
            List<Value> values = new ArrayList<>();
            if (take(')')) return values;
            do values.add(expression());
            while (take(','));
            if (!take(')')) throw new EvaluationException("Expected ')'");
            return values;
        }

        private Value primary() throws Exception {
            space();
            if (at >= text.length()) throw new EvaluationException("Incomplete expression");
            char c = text.charAt(at);
            if (take('(')) {
                Value inner = expression();
                if (!take(')')) throw new EvaluationException("Expected ')'");
                return inner;
            }
            if (c == '"') {
                int end = text.indexOf('"', at + 1);
                if (end < 0) throw new EvaluationException("Unclosed string");
                String literal = text.substring(at + 1, end);
                at = end + 1;
                return vm.mirrorOf(literal);
            }
            if (Character.isDigit(c) || c == '-') {
                int begin = at++;
                while (at < text.length() && (Character.isDigit(text.charAt(at)) || text.charAt(at) == '.')) at++;
                String number = text.substring(begin, at);
                if (number.contains(".")) return vm.mirrorOf(Double.parseDouble(number));
                long whole = Long.parseLong(number);
                if (at < text.length() && (text.charAt(at) == 'L' || text.charAt(at) == 'l')) {
                    at++;
                    return vm.mirrorOf(whole);
                }
                return whole == (int) whole ? vm.mirrorOf((int) whole) : vm.mirrorOf(whole);
            }
            String name = name();
            switch (name) {
                case "null":
                    return null;
                case "true":
                    return vm.mirrorOf(true);
                case "false":
                    return vm.mirrorOf(false);
                case "this":
                    ObjectReference self = ref.frame().thisObject();
                    if (self == null) throw new EvaluationException("'this' is not available in a static context");
                    return self;
                default:
                    break;
            }
            StackFrame frame = ref.frame();
            if (take('(')) {
                List<Value> args = arguments();
                ObjectReference self = ref.frame().thisObject();
                return self != null
                        ? call(self, null, name, args)
                        : call(null, ref.frame().location().declaringType(), name, args);
            }
            try {
                LocalVariable local = frame.visibleVariableByName(name);
                if (local != null) return frame.getValue(local);
            } catch (AbsentInformationException ignored) {
            }
            ObjectReference self = frame.thisObject();
            if (self != null) {
                Field field = self.referenceType().fieldByName(name);
                if (field != null) return self.getValue(field);
            }
            ReferenceType declaring = frame.location().declaringType();
            Field field = declaring.fieldByName(name);
            if (field != null && field.isStatic()) return declaring.getValue(field);
            // A class: java.lang's by its simple name, any loaded one by its full name.
            ReferenceType type = type(name);
            while (type == null && take('.')) {
                name = name + "." + name();
                type = type(name);
            }
            if (type == null) throw new EvaluationException("Cannot find local variable '" + name + "'");
            if (!take('.')) throw new EvaluationException("'" + name + "' is a class");
            String member = name();
            if (take('(')) return call(null, type, member, arguments());
            Field statics = type.fieldByName(member);
            if (statics == null || !statics.isStatic())
                throw new EvaluationException("Cannot find static field '" + member + "' in " + type.name());
            return type.getValue(statics);
        }

        private ReferenceType type(String name) {
            for (String candidate : List.of(name, "java.lang." + name)) {
                List<ReferenceType> found = vm.classesByName(candidate);
                if (!found.isEmpty()) return found.get(0);
            }
            return null;
        }

        private Value member(Value owner, String name) throws EvaluationException {
            if (owner == null) throw new EvaluationException("NullPointerException: '" + name + "' of null");
            if (owner instanceof ArrayReference array && name.equals("length")) return vm.mirrorOf(array.length());
            if (!(owner instanceof ObjectReference object))
                throw new EvaluationException("'" + name + "' of a primitive value");
            Field field = object.referenceType().fieldByName(name);
            if (field == null)
                throw new EvaluationException("Cannot find field '" + name + "' in " + object.referenceType().name());
            return object.getValue(field);
        }

        private Value call(Value owner, ReferenceType statics, String name, List<Value> args) throws Exception {
            if (suspended == null)
                throw new EvaluationException("Methods can be called only when stopped at a breakpoint or step");
            if (owner == null && statics == null)
                throw new EvaluationException("NullPointerException: " + name + "() on null");
            if (owner != null && !(owner instanceof ObjectReference))
                throw new EvaluationException(name + "() on a primitive value");
            ReferenceType type = owner != null ? ((ObjectReference) owner).referenceType() : statics;
            Method method =
                    type.methodsByName(name).stream()
                            .filter(m -> m.argumentTypeNames().size() == args.size())
                            .filter(m -> owner != null || m.isStatic())
                            .findFirst()
                            .orElseThrow(
                                    () ->
                                            new EvaluationException(
                                                    "Cannot find method '" + name + "' with " + args.size()
                                                            + " arguments in " + type.name()));
            // A breakpoint hit inside the call would wait on the event thread, which waits on us.
            var manager = vm.eventRequestManager();
            List<BreakpointRequest> enabled =
                    manager.breakpointRequests().stream().filter(EventRequest::isEnabled).toList();
            enabled.forEach(EventRequest::disable);
            try {
                ThreadReference thread = ref.thread();
                if (owner != null)
                    return ((ObjectReference) owner)
                            .invokeMethod(thread, method, args, ObjectReference.INVOKE_SINGLE_THREADED);
                if (type instanceof ClassType kind)
                    return kind.invokeMethod(thread, method, args, ObjectReference.INVOKE_SINGLE_THREADED);
                if (type instanceof InterfaceType kind)
                    return kind.invokeMethod(thread, method, args, ObjectReference.INVOKE_SINGLE_THREADED);
                throw new EvaluationException("Cannot call " + name + " on " + type.name());
            } finally {
                enabled.forEach(EventRequest::enable);
            }
        }
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
                        else if (e instanceof VMDeathEvent) event("exited", Map.of());
                        else if (e instanceof VMDisconnectEvent) return;
                    }
                    if (stop == null) batch.resume();
                    else {
                        // A breakpoint may interrupt an outstanding step.
                        vm.eventRequestManager()
                                .deleteEventRequests(
                                        List.copyOf(vm.eventRequestManager().stepRequests()));
                        if (paused) {
                            // Paused by the person while the event arrived: keep one suspension.
                            paused = false;
                            vm.resume();
                        }
                        handles.clear();
                        suspended = batch;
                        stopped = stop.thread();
                        event(
                                "stopped",
                                map(
                                        "reason", stop instanceof StepEvent ? "step" : "breakpoint",
                                        "threadId", stopped.uniqueID(),
                                        "allThreadsStopped", true));
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
