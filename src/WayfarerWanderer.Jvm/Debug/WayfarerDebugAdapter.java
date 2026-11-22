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
                        "supportsEvaluateForHovers", true,
                        "supportsSetVariable", true,
                        "supportsCompletionsRequest", true);
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
            case "setVariable":
                return setVariable(args);
            case "completions":
                return completions(args);
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
        unpin();
        if (paused) {
            paused = false;
            vm.resume();
        } else {
            suspended.resume();
        }
        suspended = null;
        stopped = null;
    }

    // Evaluate, as IntelliJ IDEA's Evaluate Expression: a Java interpreter over JDI. It takes the
    // whole expression language (every operator, casts, instanceof with a pattern, ternaries,
    // assignment and compound assignment, ++ and --, string concatenation, boxing and unboxing,
    // class literals, object and array creation with initializers, overloaded, static, super,
    // generic and varargs calls) and code fragments (local declarations, blocks, if, while, do,
    // for, for-each, labelled break and continue, return). Names resolve as javac resolves them:
    // the fragment's locals, the frame's locals, this' and the outer instances' fields, the class'
    // and its enclosing classes' statics, then static imports; types from nested classes, the
    // source file's imports, its package and java.lang, loading a class in the target when it has
    // not been loaded yet. Lambdas, method references, anonymous classes, switch and try are not
    // interpreted.
    private static final class EvaluationException extends Exception {
        EvaluationException(String message) {
            super(message);
        }
    }

    private interface Getter {
        Value get() throws Exception;
    }

    private interface Setter {
        void set(Value value) throws Exception;
    }

    /** A variable, field or array element: what assignment, ++ and -- write. */
    private record Place(String type, Getter getter, Setter setter) {
        Value get() throws Exception {
            return getter.get();
        }

        void set(Value value) throws Exception {
            setter.set(value);
        }
    }

    private interface Expr {
        Value eval() throws Exception;

        default Place place() throws Exception {
            throw new EvaluationException("Cannot assign a value to this expression");
        }
    }

    private interface Stmt {
        void run() throws Exception;
    }

    private static final class Jump extends RuntimeException {
        final boolean leave;
        final String label;

        Jump(boolean leave, String label) {
            super(null, null, false, false);
            this.leave = leave;
            this.label = label;
        }
    }

    private static final class Return extends RuntimeException {
        final Value value;

        Return(Value value) {
            super(null, null, false, false);
            this.value = value;
        }
    }

    // kind: 'i' identifier or keyword, 'n' number, 'c' char, 's' string, 'o' operator, 'e' end.
    private record Token(char kind, String text, Object literal, int at) {
        boolean is(String s) {
            return (kind == 'o' || kind == 'i') && text.equals(s);
        }
    }

    private record Import(String name, boolean statics, boolean onDemand) {}

    private static final Set<String> PRIMITIVES =
            Set.of("boolean", "byte", "short", "char", "int", "long", "float", "double");
    private static final Map<String, String> BOX_OF =
            Map.of(
                    "boolean", "java.lang.Boolean",
                    "byte", "java.lang.Byte",
                    "short", "java.lang.Short",
                    "char", "java.lang.Character",
                    "int", "java.lang.Integer",
                    "long", "java.lang.Long",
                    "float", "java.lang.Float",
                    "double", "java.lang.Double");
    private static final Map<String, String> UNBOX_OF = new HashMap<>();

    static {
        BOX_OF.forEach((primitive, box) -> UNBOX_OF.put(box, primitive));
    }

    private static final Map<String, Set<String>> WIDENS =
            Map.of(
                    "byte", Set.of("short", "int", "long", "float", "double"),
                    "short", Set.of("int", "long", "float", "double"),
                    "char", Set.of("int", "long", "float", "double"),
                    "int", Set.of("long", "float", "double"),
                    "long", Set.of("float", "double"),
                    "float", Set.of("double"));
    private static final Set<String> KEYWORDS =
            Set.of(
                    "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class",
                    "const", "continue", "default", "do", "double", "else", "enum", "extends", "final",
                    "finally", "float", "for", "goto", "if", "implements", "import", "instanceof", "int",
                    "interface", "long", "native", "new", "package", "private", "protected", "public",
                    "return", "short", "static", "strictfp", "super", "switch", "synchronized", "this",
                    "throw", "throws", "transient", "try", "void", "volatile", "while", "true", "false",
                    "null");
    private static final String[] OPERATORS = {
        ">>>=", "<<=", ">>=", ">>>", "...", "->", "::", "++", "--", "&&", "||", "==", "!=", "<=", ">=",
        "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<", ">>", "(", ")", "{", "}", "[", "]", ";",
        ",", ".", "@", "=", ">", "<", "!", "~", "?", ":", "+", "-", "*", "/", "&", "|", "^", "%"
    };
    private static final List<Set<String>> BINARY =
            List.of(
                    Set.of("||"),
                    Set.of("&&"),
                    Set.of("|"),
                    Set.of("^"),
                    Set.of("&"),
                    Set.of("==", "!="),
                    Set.of("<", ">", "<=", ">=", "instanceof"),
                    Set.of("<<", ">>", ">>>"),
                    Set.of("+", "-"),
                    Set.of("*", "/", "%"));
    private static final Set<String> ASSIGNMENTS =
            Set.of("=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", ">>>=");
    // The magnitudes of Integer.MIN_VALUE and Long.MIN_VALUE: literals only after a unary minus.
    private static final Object INT_MIN = new Object();
    private static final Object LONG_MIN = new Object();

    // Objects an evaluation created stay alive while the program is suspended, so results open.
    private final List<ObjectReference> pinned = new ArrayList<>();
    private final Map<Path, List<Import>> importsBySource = new HashMap<>();

    private Value pin(Value value) {
        if (value instanceof ObjectReference object) {
            try {
                object.disableCollection();
                pinned.add(object);
            } catch (ObjectCollectedException | UnsupportedOperationException ignored) {
            }
        }
        return value;
    }

    private void unpin() {
        for (ObjectReference object : pinned) {
            try {
                object.enableCollection();
            } catch (RuntimeException ignored) {
            }
        }
        pinned.clear();
    }

    private FrameRef frameRef(Object frameId) throws EvaluationException {
        FrameRef ref = frameId instanceof Number n && handles.get(n.intValue()) instanceof FrameRef f ? f : null;
        if (ref == null) {
            if (stopped == null) throw new EvaluationException("The program is running");
            ref = new FrameRef(stopped, 0);
        }
        return ref;
    }

    private Map<String, Object> described(String key, Value value) {
        return map(
                key, format(value),
                "type", value == null ? "null" : value.type().name(),
                "variablesReference", hasChildren(value) ? handle(value) : 0);
    }

    private Object evaluate(Map<String, Object> args) throws Exception {
        String expression = String.valueOf(args.get("expression"));
        if (expression.isBlank()) throw new EvaluationException("Nothing to evaluate");
        Evaluator evaluator = new Evaluator(frameRef(args.get("frameId")), "hover".equals(args.get("context")));
        return described("result", evaluator.run(expression));
    }

    // Set Value on a row of the variables tree: a frame's local, an object's field or an element.
    private Object setVariable(Map<String, Object> args) throws Exception {
        Object owner = handles.get(((Number) args.get("variablesReference")).intValue());
        if (owner == null) throw new EvaluationException("The variable is no longer available");
        Evaluator evaluator = new Evaluator(owner instanceof FrameRef frame ? frame : frameRef(null), false);
        Place place = evaluator.member(owner, String.valueOf(args.get("name")));
        Value value = evaluator.assign(place, evaluator.run(String.valueOf(args.get("value"))));
        return described("value", value);
    }

    // Completion in the evaluate box: the members of what precedes a dot, or the names in scope.
    private Object completions(Map<String, Object> args) throws Exception {
        String text = String.valueOf(args.get("text"));
        int column = args.get("column") instanceof Number n ? n.intValue() : text.length() + 1;
        String before = text.substring(0, Math.max(0, Math.min(text.length(), column - 1)));
        int begin = before.length();
        while (begin > 0 && Character.isJavaIdentifierPart(before.charAt(begin - 1))) begin--;
        String prefix = before.substring(begin);
        Map<String, Map<String, Object>> found = new LinkedHashMap<>();
        Evaluator evaluator = new Evaluator(frameRef(args.get("frameId")), true);
        int dot = begin - 1;
        while (dot >= 0 && Character.isWhitespace(before.charAt(dot))) dot--;
        try {
            if (dot >= 0 && before.charAt(dot) == '.') evaluator.members(receiver(before, dot), found);
            else evaluator.names(found);
        } catch (Exception ignored) {
            // Nothing to offer after a receiver that cannot be evaluated without side effects.
        }
        String lower = prefix.toLowerCase(Locale.ROOT);
        List<Map<String, Object>> matches = new ArrayList<>();
        for (Map<String, Object> item : found.values())
            if (((String) item.get("label")).toLowerCase(Locale.ROOT).startsWith(lower)) matches.add(item);
        matches.sort(
                Comparator.comparing((Map<String, Object> item) -> !((String) item.get("label")).startsWith(prefix))
                        .thenComparing(item -> (String) item.get("label"), String.CASE_INSENSITIVE_ORDER));
        List<Object> targets = new ArrayList<>();
        for (Map<String, Object> item : matches.subList(0, Math.min(200, matches.size()))) {
            item.put("start", begin);
            item.put("length", prefix.length());
            targets.add(item);
        }
        return Map.of("targets", targets);
    }

    private static String receiver(String text, int dot) {
        int i = dot;
        int depth = 0;
        boolean quoted = false;
        while (i > 0) {
            char c = text.charAt(i - 1);
            if (c == '"') quoted = !quoted;
            else if (!quoted) {
                if (c == ')' || c == ']') depth++;
                else if (c == '(' || c == '[') {
                    if (depth == 0) break;
                    depth--;
                } else if (depth == 0 && !(Character.isJavaIdentifierPart(c) || c == '.')) break;
            }
            i--;
        }
        return text.substring(i, dot);
    }

    private static List<Token> lex(String s) throws EvaluationException {
        List<Token> out = new ArrayList<>();
        int i = 0;
        int n = s.length();
        while (true) {
            while (i < n) {
                char c = s.charAt(i);
                if (Character.isWhitespace(c)) i++;
                else if (s.startsWith("//", i)) {
                    while (i < n && s.charAt(i) != '\n') i++;
                } else if (s.startsWith("/*", i)) {
                    int end = s.indexOf("*/", i + 2);
                    if (end < 0) throw new EvaluationException("Unclosed comment");
                    i = end + 2;
                } else break;
            }
            if (i >= n) {
                out.add(new Token('e', "", null, n));
                return out;
            }
            char c = s.charAt(i);
            int begin = i;
            if (Character.isJavaIdentifierStart(c)) {
                while (i < n && Character.isJavaIdentifierPart(s.charAt(i))) i++;
                out.add(new Token('i', s.substring(begin, i), null, begin));
            } else if (Character.isDigit(c) || (c == '.' && i + 1 < n && Character.isDigit(s.charAt(i + 1)))) {
                i = number(s, i, out);
            } else if (s.startsWith("\"\"\"", i)) {
                int open = s.indexOf('\n', i + 3);
                int close = open < 0 ? -1 : s.indexOf("\"\"\"", open);
                if (close < 0) throw new EvaluationException("Unclosed text block");
                String body = s.substring(open + 1, close);
                out.add(new Token('s', body, unescape(body.stripIndent()), begin));
                i = close + 3;
            } else if (c == '"' || c == '\'') {
                int j = i + 1;
                while (j < n && s.charAt(j) != c && s.charAt(j) != '\n') j += s.charAt(j) == '\\' ? 2 : 1;
                if (j >= n || s.charAt(j) != c)
                    throw new EvaluationException(c == '"' ? "Unclosed string literal" : "Unclosed character literal");
                String body = s.substring(i + 1, j);
                String value = unescape(body);
                if (c == '"') out.add(new Token('s', body, value, begin));
                else {
                    if (value.length() != 1) throw new EvaluationException("Illegal character literal '" + body + "'");
                    out.add(new Token('c', body, value.charAt(0), begin));
                }
                i = j + 1;
            } else {
                String op = null;
                for (String candidate : OPERATORS)
                    if (s.startsWith(candidate, i)) {
                        op = candidate;
                        break;
                    }
                if (op == null) throw new EvaluationException("Unexpected '" + c + "'");
                if (op.equals("->") || op.equals("::"))
                    throw new EvaluationException("Lambdas and method references are not supported in Evaluate");
                out.add(new Token('o', op, null, begin));
                i += op.length();
            }
        }
    }

    private static String unescape(String body) throws EvaluationException {
        StringBuilder unicode = new StringBuilder();
        for (int i = 0; i < body.length(); i++) {
            char c = body.charAt(i);
            if (c == '\\' && i + 1 < body.length() && body.charAt(i + 1) == 'u') {
                int j = i + 1;
                while (j < body.length() && body.charAt(j) == 'u') j++;
                if (j + 4 > body.length()) throw new EvaluationException("Illegal unicode escape");
                unicode.append((char) Integer.parseInt(body.substring(j, j + 4), 16));
                i = j + 3;
            } else unicode.append(c);
        }
        try {
            return unicode.toString().translateEscapes();
        } catch (IllegalArgumentException e) {
            throw new EvaluationException("Illegal escape character in '" + body + "'");
        }
    }

    private static int number(String s, int i, List<Token> out) throws EvaluationException {
        int n = s.length();
        int begin = i;
        int radix = 10;
        boolean floating = false;
        if (s.startsWith("0x", i) || s.startsWith("0X", i)) {
            radix = 16;
            i += 2;
            while (i < n && (Character.digit(s.charAt(i), 16) >= 0 || s.charAt(i) == '_')) i++;
        } else if (s.startsWith("0b", i) || s.startsWith("0B", i)) {
            radix = 2;
            i += 2;
            while (i < n && (s.charAt(i) == '0' || s.charAt(i) == '1' || s.charAt(i) == '_')) i++;
        } else {
            while (i < n && (Character.isDigit(s.charAt(i)) || s.charAt(i) == '_')) i++;
            if (i < n && s.charAt(i) == '.' && (i + 1 >= n || !Character.isJavaIdentifierStart(s.charAt(i + 1)))) {
                floating = true;
                i++;
                while (i < n && (Character.isDigit(s.charAt(i)) || s.charAt(i) == '_')) i++;
            }
            if (i < n && (s.charAt(i) == 'e' || s.charAt(i) == 'E')) {
                floating = true;
                i++;
                if (i < n && (s.charAt(i) == '+' || s.charAt(i) == '-')) i++;
                while (i < n && Character.isDigit(s.charAt(i))) i++;
            }
        }
        String digits = s.substring(radix == 10 ? begin : begin + 2, i).replace("_", "");
        char suffix = i < n ? Character.toLowerCase(s.charAt(i)) : ' ';
        Object literal;
        try {
            if (radix == 10 && (floating || suffix == 'f' || suffix == 'd')) {
                if (suffix == 'f' || suffix == 'd') i++;
                literal = suffix == 'f' ? (Object) Float.parseFloat(digits) : (Object) Double.parseDouble(digits);
            } else {
                if (radix == 10 && digits.length() > 1 && digits.startsWith("0")) radix = 8;
                java.math.BigInteger value = new java.math.BigInteger(radix == 8 ? digits.substring(1) : digits, radix);
                boolean decimal = radix == 10;
                if (suffix == 'l') {
                    i++;
                    if (decimal && value.equals(java.math.BigInteger.ONE.shiftLeft(63))) literal = LONG_MIN;
                    else if (value.bitLength() > (decimal ? 63 : 64))
                        throw new EvaluationException("Long number too large: " + s.substring(begin, i));
                    else literal = value.longValue();
                } else {
                    if (decimal && value.equals(java.math.BigInteger.ONE.shiftLeft(31))) literal = INT_MIN;
                    else if (value.bitLength() > (decimal ? 31 : 32))
                        throw new EvaluationException("Integer number too large: " + s.substring(begin, i));
                    else literal = value.intValue();
                }
            }
        } catch (NumberFormatException e) {
            throw new EvaluationException("Malformed number '" + s.substring(begin, i) + "'");
        }
        if (i < n && Character.isJavaIdentifierPart(s.charAt(i)))
            throw new EvaluationException("Malformed number '" + s.substring(begin, i + 1) + "'");
        out.add(new Token('n', s.substring(begin, i), literal, begin));
        return i;
    }

    private static boolean widens(String from, String to) {
        return from.equals(to) || WIDENS.getOrDefault(from, Set.of()).contains(to);
    }

    private static String promoted(String a, String b) {
        if (a.equals("double") || b.equals("double")) return "double";
        if (a.equals("float") || b.equals("float")) return "float";
        if (a.equals("long") || b.equals("long")) return "long";
        return "int";
    }

    private static String promoted(String a) {
        return a.equals("long") || a.equals("float") || a.equals("double") ? a : "int";
    }

    private static boolean boxAccepts(String primitive, String type) {
        String box = BOX_OF.get(primitive);
        return type.equals(box)
                || type.equals("java.lang.Object")
                || type.equals("java.io.Serializable")
                || type.equals("java.lang.Comparable")
                || type.equals("java.lang.constant.Constable")
                || (type.equals("java.lang.constant.ConstantDesc") && !primitive.equals("boolean") && !primitive.equals("char"))
                || (type.equals("java.lang.Number") && !primitive.equals("boolean") && !primitive.equals("char"));
    }

    /** Whether a value of type t can be assigned to the named type, by name: nothing is loaded. */
    private static boolean instanceOf(Type t, String name) {
        if (t.name().equals(name)) return true;
        if (t instanceof PrimitiveType) return false;
        if (name.equals("java.lang.Object")) return true;
        if (t instanceof ArrayType array) {
            if (name.equals("java.lang.Cloneable") || name.equals("java.io.Serializable")) return true;
            if (!name.endsWith("[]")) return false;
            String component = name.substring(0, name.length() - 2);
            try {
                Type of = array.componentType();
                return of instanceof PrimitiveType ? of.name().equals(component) : instanceOf(of, component);
            } catch (ClassNotLoadedException e) {
                return array.componentTypeName().equals(component);
            }
        }
        if (t instanceof ClassType kind) {
            for (ClassType up = kind.superclass(); up != null; up = up.superclass())
                if (up.name().equals(name)) return true;
            for (InterfaceType face : kind.allInterfaces()) if (face.name().equals(name)) return true;
            return false;
        }
        if (t instanceof InterfaceType face) {
            for (InterfaceType up : face.superinterfaces()) if (instanceOf(up, name)) return true;
        }
        return false;
    }

    private static String join(List<String> parts) {
        StringJoiner joined = new StringJoiner(", ");
        for (String part : parts) joined.add(simple(part));
        return joined.toString();
    }

    private final class Evaluator {
        private final FrameRef ref;
        private final boolean sideEffectFree;
        private final long deadline = System.nanoTime() + 60_000_000_000L;
        private final Deque<Map<String, Place>> scopes = new ArrayDeque<>();
        private final Map<String, Optional<ReferenceType>> types = new HashMap<>();
        private String source = "";
        private List<Token> tokens;
        private int pos;
        private Value last;
        private String label;

        Evaluator(FrameRef ref, boolean sideEffectFree) {
            this.ref = ref;
            this.sideEffectFree = sideEffectFree;
            scopes.push(new HashMap<>());
        }

        // ------------------------------------------------------------------ entry points

        Value run(String text) throws Exception {
            parse(text);
            List<Stmt> body = new ArrayList<>();
            while (peek().kind != 'e') body.add(statement());
            try {
                for (Stmt statement : body) statement.run();
            } catch (Return returned) {
                return returned.value;
            } catch (Jump jump) {
                throw new EvaluationException((jump.leave ? "break" : "continue") + " outside of a loop");
            }
            return last;
        }

        private void parse(String text) throws EvaluationException {
            source = text;
            tokens = lex(text);
            pos = 0;
        }

        /** A type, or the value of an expression evaluated without side effects. */
        Object valueOrType(String text) throws Exception {
            parse(text);
            Expr expr = expression();
            if (peek().kind != 'e') throw unexpected(peek());
            if (expr instanceof NameExpr name) {
                Object resolved = name.resolve();
                return resolved instanceof Place place ? place.get() : resolved;
            }
            return expr.eval();
        }

        Place member(Object owner, String name) throws Exception {
            if (owner instanceof FrameRef) {
                Place place = variable(name);
                if (place == null) throw new EvaluationException("Cannot find local variable '" + name + "'");
                return place;
            }
            if (owner instanceof ArrayReference array && name.startsWith("[") && name.endsWith("]"))
                return element(array, Integer.parseInt(name.substring(1, name.length() - 1)));
            if (owner instanceof ObjectReference object)
                for (Field field : object.referenceType().allFields())
                    if (field.name().equals(name)) return field(object, field);
            throw new EvaluationException("Cannot find '" + name + "'");
        }

        Value assign(Place place, Value value) throws Exception {
            sideEffect();
            Value converted = convert(value, place.type());
            place.set(converted);
            return converted;
        }

        void names(Map<String, Map<String, Object>> found) throws Exception {
            StackFrame frame = frame();
            try {
                for (LocalVariable local : frame.visibleVariables())
                    add(found, local.name(), local.name(), "variable", local.typeName());
            } catch (AbsentInformationException ignored) {
            }
            ObjectReference self = frame.thisObject();
            addMembers(self != null ? self.referenceType() : frame.location().declaringType(), self == null, found);
            for (Import i : imports())
                if (!i.statics() && !i.onDemand()) add(found, simple(i.name()), simple(i.name()), "class", i.name());
            for (String keyword :
                    List.of("true", "false", "null", "this", "super", "new", "instanceof", "var", "int", "long",
                            "double", "float", "boolean", "char", "byte", "short", "if", "for", "while", "return"))
                add(found, keyword, keyword, "keyword", null);
        }

        void members(String receiver, Map<String, Map<String, Object>> found) throws Exception {
            Object owner = valueOrType(receiver);
            if (owner instanceof ReferenceType type) {
                addMembers(type, true, found);
                for (ReferenceType nested : type.nestedTypes()) {
                    String name = nested.name().substring(nested.name().lastIndexOf('$') + 1);
                    if (!name.isEmpty() && !Character.isDigit(name.charAt(0))) add(found, name, name, "class", nested.name());
                }
                add(found, "class", "class", "keyword", null);
            } else if (owner instanceof ArrayReference) {
                add(found, "length", "length", "field", "int");
                ReferenceType object = load("java.lang.Object");
                if (object != null) addMembers(object, false, found);
            } else if (owner instanceof ObjectReference object) addMembers(object.referenceType(), false, found);
        }

        private void addMembers(ReferenceType type, boolean staticOnly, Map<String, Map<String, Object>> found) {
            for (Field field : type.allFields())
                if (!field.isSynthetic() && (!staticOnly || field.isStatic()))
                    add(found, field.name(), field.name(), "field", field.typeName());
            Set<String> seen = new HashSet<>();
            for (Method method : type.allMethods()) {
                if (method.isConstructor() || method.isStaticInitializer() || method.isSynthetic() || method.isBridge()) continue;
                if (staticOnly && !method.isStatic()) continue;
                if (!seen.add(method.name() + method.signature())) continue;
                List<String> params = method.argumentTypeNames();
                add(found, method.name() + "(" + join(params) + ")", method.name() + (params.isEmpty() ? "()" : "("),
                        "method", method.returnTypeName());
            }
        }

        private void add(Map<String, Map<String, Object>> found, String label, String text, String kind, String detail) {
            if (found.containsKey(label)) return;
            Map<String, Object> item = map("label", label, "text", text, "type", kind);
            if (detail != null) item.put("detail", simple(detail));
            found.put(label, item);
        }

        // ------------------------------------------------------------------ tokens

        private Token peek() {
            return tokens.get(pos);
        }

        private Token peekAt(int ahead) {
            return tokens.get(Math.min(pos + ahead, tokens.size() - 1));
        }

        private Token next() {
            Token token = tokens.get(pos);
            if (token.kind != 'e') pos++;
            return token;
        }

        private boolean at(String s) {
            return peek().is(s);
        }

        private boolean take(String s) {
            if (!at(s)) return false;
            pos++;
            return true;
        }

        private void expect(String s) throws EvaluationException {
            if (!take(s)) throw expected("'" + s + "'");
        }

        private String rest(Token token) {
            return source.substring(token.at).strip();
        }

        private EvaluationException expected(String what) {
            Token token = peek();
            return new EvaluationException(
                    token.kind == 'e' ? what + " expected" : what + " expected at '" + rest(token) + "'");
        }

        private EvaluationException unexpected(Token token) {
            return new EvaluationException(token.kind == 'e' ? "Incomplete expression" : "Unexpected '" + rest(token) + "'");
        }

        private String identifier() throws EvaluationException {
            Token token = peek();
            if (token.kind != 'i' || KEYWORDS.contains(token.text)) throw expected("Identifier");
            pos++;
            return token.text;
        }

        private boolean atName() {
            return peek().kind == 'i' && !KEYWORDS.contains(peek().text);
        }

        // ------------------------------------------------------------------ statements

        private Stmt statement() throws Exception {
            Token token = peek();
            if (token.is("{")) return block();
            if (token.is(";")) {
                pos++;
                return () -> {};
            }
            if (token.kind == 'i') {
                if (atName() && peekAt(1).is(":")) {
                    pos += 2;
                    String name = token.text;
                    label = name;
                    Stmt inner = statement();
                    label = null;
                    return () -> {
                        try {
                            inner.run();
                        } catch (Jump jump) {
                            if (!jump.leave || !name.equals(jump.label)) throw jump;
                        }
                    };
                }
                switch (token.text) {
                    case "if":
                        return ifStatement();
                    case "while":
                        return whileStatement();
                    case "do":
                        return doStatement();
                    case "for":
                        return forStatement();
                    case "break":
                    case "continue": {
                        pos++;
                        String target = atName() ? next().text : null;
                        end();
                        boolean leave = token.text.equals("break");
                        return () -> {
                            throw new Jump(leave, target);
                        };
                    }
                    case "return": {
                        pos++;
                        Expr value = at(";") || peek().kind == 'e' || at("}") ? null : expression();
                        end();
                        return () -> {
                            throw new Return(value == null ? vm.mirrorOfVoid() : value.eval());
                        };
                    }
                    case "throw": {
                        pos++;
                        Expr thrown = expression();
                        end();
                        return () -> {
                            Value value = thrown.eval();
                            throw new EvaluationException(
                                    "Exception thrown: " + (value instanceof ObjectReference o ? describe(o) : format(value)));
                        };
                    }
                    case "switch":
                    case "try":
                    case "synchronized":
                    case "class":
                    case "interface":
                    case "enum":
                    case "assert":
                        throw new EvaluationException("'" + token.text + "' is not supported in Evaluate");
                    default:
                        break;
                }
            }
            if (declarationAhead()) {
                Stmt declaration = declaration();
                end();
                return declaration;
            }
            Expr expr = expression();
            end();
            return () -> last = expr.eval();
        }

        private void end() throws EvaluationException {
            if (take(";") || peek().kind == 'e' || at("}")) return;
            throw unexpected(peek());
        }

        private Stmt block() throws Exception {
            expect("{");
            List<Stmt> body = new ArrayList<>();
            while (!at("}")) {
                if (peek().kind == 'e') throw expected("'}'");
                body.add(statement());
            }
            pos++;
            return () -> scoped(() -> {
                for (Stmt statement : body) statement.run();
            });
        }

        private void scoped(Stmt body) throws Exception {
            scopes.push(new HashMap<>());
            try {
                body.run();
            } finally {
                scopes.pop();
            }
        }

        private Expr condition() throws Exception {
            expect("(");
            Expr condition = expression();
            expect(")");
            return condition;
        }

        private Stmt ifStatement() throws Exception {
            pos++;
            Expr condition = condition();
            Stmt then = statement();
            Stmt otherwise = take("else") ? statement() : null;
            return () -> {
                if (truth(condition.eval())) scoped(then);
                else if (otherwise != null) scoped(otherwise);
            };
        }

        /** Runs a loop's body: true to go on, false when a break leaves this loop. */
        private boolean loop(Stmt body, String name) throws Exception {
            tick();
            try {
                scoped(body);
                return true;
            } catch (Jump jump) {
                if (jump.label != null && !jump.label.equals(name)) throw jump;
                return !jump.leave;
            }
        }

        private Stmt whileStatement() throws Exception {
            String name = label;
            label = null;
            pos++;
            Expr condition = condition();
            Stmt body = statement();
            return () -> {
                while (truth(condition.eval()) && loop(body, name)) {}
            };
        }

        private Stmt doStatement() throws Exception {
            String name = label;
            label = null;
            pos++;
            Stmt body = statement();
            if (!take("while")) throw expected("'while'");
            Expr condition = condition();
            end();
            return () -> {
                while (loop(body, name) && truth(condition.eval())) {}
            };
        }

        private Stmt forStatement() throws Exception {
            String name = label;
            label = null;
            pos++;
            expect("(");
            int save = pos;
            if (declarationAhead()) {
                while (take("final")) {}
                String written = type();
                String variable = identifier();
                if (take(":")) {
                    Expr iterable = expression();
                    expect(")");
                    Stmt body = statement();
                    return () -> forEach(written, variable, iterable.eval(), body, name);
                }
                pos = save;
            }
            List<Stmt> init = new ArrayList<>();
            if (declarationAhead()) init.add(declaration());
            else if (!at(";")) {
                do {
                    Expr expr = expression();
                    init.add(expr::eval);
                } while (take(","));
            }
            expect(";");
            Expr condition = at(";") ? null : expression();
            expect(";");
            List<Expr> update = new ArrayList<>();
            if (!at(")"))
                do update.add(expression());
                while (take(","));
            expect(")");
            Stmt body = statement();
            return () -> scoped(() -> {
                for (Stmt statement : init) statement.run();
                while ((condition == null || truth(condition.eval())) && loop(body, name))
                    for (Expr expr : update) expr.eval();
            });
        }

        private void forEach(String written, String variable, Value iterable, Stmt body, String name) throws Exception {
            String declared = written.equals("var") ? null : canonical(written);
            scoped(() -> {
                Place place = local(variable, declared == null ? "java.lang.Object" : declared, null);
                if (iterable instanceof ArrayReference array) {
                    for (int i = 0; i < array.length(); i++) {
                        place.set(declared == null ? array.getValue(i) : convert(array.getValue(i), declared));
                        if (!loop(body, name)) return;
                    }
                } else if (iterable instanceof ObjectReference object) {
                    Value iterator = call(object, "iterator", List.of());
                    if (!(iterator instanceof ObjectReference it)) throw new EvaluationException("iterator() returned null");
                    while (truth(call(it, "hasNext", List.of()))) {
                        Value item = call(it, "next", List.of());
                        place.set(declared == null ? item : convert(item, declared));
                        if (!loop(body, name)) return;
                    }
                } else if (iterable == null) throw new EvaluationException("java.lang.NullPointerException");
                else throw new EvaluationException("foreach not applicable to type '" + iterable.type().name() + "'");
            });
        }

        private boolean declarationAhead() {
            int save = pos;
            try {
                while (take("final")) {}
                Token first = peek();
                if (first.kind != 'i' || (KEYWORDS.contains(first.text) && !PRIMITIVES.contains(first.text))) return false;
                type();
                if (!atName()) return false;
                Token after = peekAt(1);
                return after.kind == 'e' || after.is("=") || after.is(";") || after.is(",") || after.is("[") || after.is(":");
            } catch (EvaluationException e) {
                return false;
            } finally {
                pos = save;
            }
        }

        private Stmt declaration() throws Exception {
            while (take("final")) {}
            String written = type();
            List<Stmt> declarators = new ArrayList<>();
            do {
                String name = identifier();
                StringBuilder declared = new StringBuilder(written);
                while (at("[") && peekAt(1).is("]")) {
                    pos += 2;
                    declared.append("[]");
                }
                String typeText = declared.toString();
                Expr init = null;
                List<Object> array = null;
                if (take("=")) {
                    if (at("{")) array = arrayInitializer();
                    else init = expression();
                }
                Expr initializer = init;
                List<Object> elements = array;
                declarators.add(() -> {
                    if (scopes.peek().containsKey(name) || localOfFrame(name))
                        throw new EvaluationException("Variable '" + name + "' is already defined in the scope");
                    String type = typeText.equals("var") ? null : canonical(typeText);
                    Value value;
                    if (elements != null) {
                        if (type == null) throw new EvaluationException("Cannot infer type: array initializer needs an explicit target-type");
                        value = array(type, elements);
                    } else if (initializer == null) {
                        if (type == null) throw new EvaluationException("Cannot infer type: 'var' on variable without initializer");
                        value = null;
                    } else {
                        value = initializer.eval();
                        if (type != null) value = convert(value, type);
                        else if (value instanceof VoidValue) throw new EvaluationException("Cannot infer type: variable initializer is 'void'");
                    }
                    local(name, type != null ? type : value == null ? "java.lang.Object" : value.type().name(), value);
                    last = value;
                });
            } while (take(","));
            return () -> {
                for (Stmt declarator : declarators) declarator.run();
            };
        }

        private boolean localOfFrame(String name) throws Exception {
            try {
                return frame().visibleVariableByName(name) != null;
            } catch (AbsentInformationException e) {
                return false;
            }
        }

        private Place local(String name, String type, Value value) {
            Value[] cell = {value};
            Place place = new Place(type, () -> cell[0], v -> cell[0] = v);
            scopes.peek().put(name, place);
            return place;
        }

        /** A type as written: a primitive, a name with type arguments dropped, and dimensions. */
        private String type() throws EvaluationException {
            Token first = next();
            if (first.kind != 'i' || (KEYWORDS.contains(first.text) && !PRIMITIVES.contains(first.text) && !first.text.equals("void")))
                throw new EvaluationException(first.kind == 'e' ? "Type expected" : "Type expected at '" + rest(first) + "'");
            StringBuilder name = new StringBuilder(first.text);
            if (!PRIMITIVES.contains(first.text)) {
                typeArguments();
                while (at(".") && peekAt(1).kind == 'i' && !KEYWORDS.contains(peekAt(1).text)) {
                    pos++;
                    name.append('.').append(next().text);
                    typeArguments();
                }
            }
            while (at("[") && peekAt(1).is("]")) {
                pos += 2;
                name.append("[]");
            }
            return name.toString();
        }

        private void typeArguments() throws EvaluationException {
            if (!at("<")) return;
            pos++;
            int depth = 1;
            while (depth > 0) {
                Token token = next();
                switch (token.text) {
                    case "<" -> depth++;
                    case ">" -> depth--;
                    case ">>" -> depth -= 2;
                    case ">>>" -> depth -= 3;
                    default -> {
                        boolean allowed =
                                token.kind == 'i' || token.is(",") || token.is(".") || token.is("?") || token.is("[")
                                        || token.is("]") || token.is("&");
                        if (!allowed) throw expected("'>'");
                    }
                }
            }
            if (depth < 0) throw expected("Type");
        }

        // ------------------------------------------------------------------ expressions

        private Expr expression() throws Exception {
            Expr left = ternary();
            Token token = peek();
            if (token.kind == 'o' && ASSIGNMENTS.contains(token.text)) {
                pos++;
                Expr right = expression();
                String op = token.text;
                return () -> {
                    sideEffect();
                    Place place = left.place();
                    if (op.equals("=")) {
                        Value value = convert(right.eval(), place.type());
                        place.set(value);
                        return value;
                    }
                    Value current = place.get();
                    Value value = narrowed(binary(op.substring(0, op.length() - 1), current, right.eval()), place.type());
                    place.set(value);
                    return value;
                };
            }
            return left;
        }

        private Expr ternary() throws Exception {
            Expr condition = binary(0);
            if (!take("?")) return condition;
            Expr then = expression();
            expect(":");
            Expr otherwise = ternary();
            return () -> truth(condition.eval()) ? then.eval() : otherwise.eval();
        }

        private Expr binary(int level) throws Exception {
            if (level == BINARY.size()) return unary();
            Expr left = binary(level + 1);
            while (true) {
                Token token = peek();
                if ((token.kind != 'o' && !token.is("instanceof")) || !BINARY.get(level).contains(token.text)) return left;
                pos++;
                String op = token.text;
                if (op.equals("instanceof")) {
                    left = instanceOfExpression(left);
                    continue;
                }
                Expr a = left;
                Expr b = binary(level + 1);
                left = switch (op) {
                    case "&&" -> () -> vm.mirrorOf(truth(a.eval()) && truth(b.eval()));
                    case "||" -> () -> vm.mirrorOf(truth(a.eval()) || truth(b.eval()));
                    default -> () -> binary(op, a.eval(), b.eval());
                };
            }
        }

        private Expr instanceOfExpression(Expr operand) throws Exception {
            while (take("final")) {}
            String written = type();
            String binding = atName() ? identifier() : null;
            return () -> {
                Value value = operand.eval();
                String type = canonical(written);
                if (value != null && !(value instanceof ObjectReference))
                    throw new EvaluationException("Unexpected type: required reference, found " + value.type().name());
                boolean is = value != null && instanceOf(value.type(), type);
                if (is && binding != null) local(binding, type, value);
                return vm.mirrorOf(is);
            };
        }

        private Expr unary() throws Exception {
            Token token = peek();
            if (token.kind == 'o') {
                switch (token.text) {
                    case "-":
                        if (peekAt(1).kind == 'n' && (peekAt(1).literal == INT_MIN || peekAt(1).literal == LONG_MIN)) {
                            boolean isLong = peekAt(1).literal == LONG_MIN;
                            pos += 2;
                            return postfix(() -> isLong ? vm.mirrorOf(Long.MIN_VALUE) : vm.mirrorOf(Integer.MIN_VALUE));
                        }
                    // fall through
                    case "+":
                    case "!":
                    case "~": {
                        pos++;
                        Expr operand = unary();
                        return () -> unary(token.text, operand.eval());
                    }
                    case "++":
                    case "--": {
                        pos++;
                        Expr operand = unary();
                        return increment(operand, token.text, true);
                    }
                    case "(": {
                        String cast = castAhead();
                        if (cast != null) {
                            Expr operand = unary();
                            return () -> cast(operand.eval(), canonical(cast));
                        }
                        break;
                    }
                    default:
                        break;
                }
            }
            return postfix(primary());
        }

        /** The type of a cast that starts here, consumed with its parentheses; otherwise null. */
        private String castAhead() {
            int save = pos;
            try {
                pos++;
                Token first = peek();
                if (first.kind != 'i' || (KEYWORDS.contains(first.text) && !PRIMITIVES.contains(first.text))) return null;
                String type = type();
                while (take("&")) type();
                if (!take(")")) return null;
                Token after = peek();
                boolean primitive = PRIMITIVES.contains(first.text) && !type.endsWith("]");
                boolean operand =
                        (after.kind == 'i' && !after.is("instanceof"))
                                || after.kind == 'n' || after.kind == 'c' || after.kind == 's'
                                || after.is("(") || after.is("!") || after.is("~")
                                || (primitive && (after.is("-") || after.is("+") || after.is("++") || after.is("--")));
                if (!operand) return null;
                save = pos;
                return type;
            } catch (EvaluationException e) {
                return null;
            } finally {
                pos = save;
            }
        }

        private Expr increment(Expr operand, String op, boolean prefix) {
            return () -> {
                sideEffect();
                Place place = operand.place();
                Value before = place.get();
                Value after = narrowed(binary(op.equals("++") ? "+" : "-", before, vm.mirrorOf(1)), place.type());
                place.set(after);
                return prefix ? after : unboxed(before);
            };
        }

        private Expr postfix(Expr expr) throws Exception {
            while (true) {
                if (take(".")) {
                    if (at("<")) typeArguments();
                    Token member = next();
                    if (member.is("class")) {
                        if (!(expr instanceof NameExpr name)) throw unexpected(member);
                        String written = String.join(".", name.parts);
                        expr = () -> classLiteral(canonical(written));
                    } else if (member.is("this")) {
                        if (!(expr instanceof NameExpr name)) throw unexpected(member);
                        String written = String.join(".", name.parts);
                        expr = () -> qualifiedThis(written);
                    } else if (member.is("new")) {
                        throw new EvaluationException("Qualified class instance creation is not supported in Evaluate");
                    } else if (member.kind == 'i' && !KEYWORDS.contains(member.text)) {
                        String name = member.text;
                        if (at("(")) {
                            List<Expr> args = arguments();
                            Expr receiver = expr;
                            expr = () -> call(receiver, name, args);
                        } else if (expr instanceof NameExpr names) {
                            List<String> parts = new ArrayList<>(names.parts);
                            parts.add(name);
                            expr = new NameExpr(parts);
                        } else {
                            Expr owner = expr;
                            expr = assignable(() -> field(owner.eval(), name));
                        }
                    } else throw unexpected(member);
                } else if (at("[")) {
                    if (peekAt(1).is("]")) {
                        if (!(expr instanceof NameExpr name)) throw unexpected(peekAt(1));
                        StringBuilder written = new StringBuilder(String.join(".", name.parts));
                        while (at("[") && peekAt(1).is("]")) {
                            pos += 2;
                            written.append("[]");
                        }
                        expect(".");
                        if (!take("class")) throw expected("'class'");
                        String typeName = written.toString();
                        expr = () -> classLiteral(canonical(typeName));
                        continue;
                    }
                    pos++;
                    Expr index = expression();
                    expect("]");
                    Expr array = expr;
                    expr = assignable(() -> element(array.eval(), index.eval()));
                } else if (at("++") || at("--")) {
                    expr = increment(expr, next().text, false);
                } else return expr;
            }
        }

        private Expr assignable(PlaceSource source) {
            return new Expr() {
                public Value eval() throws Exception {
                    return source.place().get();
                }

                public Place place() throws Exception {
                    return source.place();
                }
            };
        }

        private List<Expr> arguments() throws Exception {
            expect("(");
            List<Expr> args = new ArrayList<>();
            if (take(")")) return args;
            do args.add(expression());
            while (take(","));
            expect(")");
            return args;
        }

        private Expr primary() throws Exception {
            Token token = next();
            switch (token.kind) {
                case 'n': {
                    Object literal = token.literal;
                    if (literal == INT_MIN || literal == LONG_MIN)
                        throw new EvaluationException((literal == INT_MIN ? "Integer" : "Long") + " number too large: " + token.text);
                    return () -> literal instanceof Integer i ? vm.mirrorOf(i)
                            : literal instanceof Long l ? vm.mirrorOf(l)
                            : literal instanceof Float f ? vm.mirrorOf(f)
                            : vm.mirrorOf((Double) literal);
                }
                case 'c': {
                    char c = (Character) token.literal;
                    return () -> vm.mirrorOf(c);
                }
                case 's': {
                    String text = (String) token.literal;
                    return () -> pin(vm.mirrorOf(text));
                }
                case 'e':
                    throw unexpected(token);
                case 'o':
                    if (token.is("(")) {
                        Expr inner = expression();
                        expect(")");
                        return new Expr() {
                            public Value eval() throws Exception {
                                return inner.eval();
                            }

                            public Place place() throws Exception {
                                return inner.place();
                            }
                        };
                    }
                    throw unexpected(token);
                default:
                    break;
            }
            switch (token.text) {
                case "true":
                    return () -> vm.mirrorOf(true);
                case "false":
                    return () -> vm.mirrorOf(false);
                case "null":
                    return () -> null;
                case "this":
                    if (at("(")) throw new EvaluationException("Constructor calls are not supported in Evaluate");
                    return this::self;
                case "super": {
                    expect(".");
                    String name = identifier();
                    if (at("(")) {
                        List<Expr> args = arguments();
                        return () -> superCall(name, values(args));
                    }
                    return assignable(() -> superField(name));
                }
                case "new":
                    return creation();
                case "void":
                    expect(".");
                    if (!take("class")) throw expected("'class'");
                    return () -> classLiteral("void");
                case "switch":
                    throw new EvaluationException("'switch' is not supported in Evaluate");
                default:
                    break;
            }
            if (PRIMITIVES.contains(token.text)) {
                StringBuilder written = new StringBuilder(token.text);
                while (at("[") && peekAt(1).is("]")) {
                    pos += 2;
                    written.append("[]");
                }
                expect(".");
                if (!take("class")) throw expected("'class'");
                String typeName = written.toString();
                return () -> classLiteral(typeName);
            }
            if (KEYWORDS.contains(token.text)) throw unexpected(token);
            String name = token.text;
            if (at("(")) {
                List<Expr> args = arguments();
                return () -> unqualifiedCall(name, values(args));
            }
            return new NameExpr(new ArrayList<>(List.of(name)));
        }

        private Expr creation() throws Exception {
            Token first = peek();
            if (first.kind != 'i' || (KEYWORDS.contains(first.text) && !PRIMITIVES.contains(first.text))) throw expected("Type");
            StringBuilder written = new StringBuilder(next().text);
            if (!PRIMITIVES.contains(first.text)) {
                typeArguments();
                while (at(".")) {
                    pos++;
                    written.append('.').append(identifier());
                    typeArguments();
                }
            }
            String base = written.toString();
            if (at("(")) {
                if (PRIMITIVES.contains(base)) throw expected("'['");
                List<Expr> args = arguments();
                if (at("{")) throw new EvaluationException("Anonymous classes are not supported in Evaluate");
                return () -> construct(canonical(base), values(args));
            }
            if (!at("[")) throw expected("'(' or '['");
            List<Expr> sizes = new ArrayList<>();
            int dimensions = 0;
            while (at("[")) {
                pos++;
                if (take("]")) {
                    dimensions++;
                    continue;
                }
                if (dimensions > sizes.size()) throw expected("']'");
                sizes.add(expression());
                expect("]");
                dimensions++;
            }
            String arrayType = base + "[]".repeat(dimensions);
            if (sizes.isEmpty()) {
                if (!at("{")) throw new EvaluationException("Array initializer expected");
                List<Object> elements = arrayInitializer();
                return () -> array(canonical(arrayType), elements);
            }
            int depth = dimensions;
            return () -> {
                List<Integer> lengths = new ArrayList<>();
                for (Expr size : sizes) {
                    int length = index(size.eval());
                    if (length < 0) throw new EvaluationException("java.lang.NegativeArraySizeException: " + length);
                    lengths.add(length);
                }
                return newArray(canonical(arrayType), lengths, 0, depth);
            };
        }

        /** {a, b, {c}}: each element an Expr, or a nested initializer as a List. */
        private List<Object> arrayInitializer() throws Exception {
            expect("{");
            List<Object> elements = new ArrayList<>();
            while (!at("}")) {
                elements.add(at("{") ? arrayInitializer() : expression());
                if (!take(",")) break;
            }
            expect("}");
            return elements;
        }

        private List<Value> values(List<Expr> args) throws Exception {
            List<Value> values = new ArrayList<>();
            for (Expr arg : args) values.add(arg.eval());
            return values;
        }

        /** A dotted name, resolved when evaluated: a variable and its fields, or a type and its statics. */
        private final class NameExpr implements Expr {
            final List<String> parts;

            NameExpr(List<String> parts) {
                this.parts = parts;
            }

            public Value eval() throws Exception {
                Object resolved = resolve();
                if (resolved instanceof ReferenceType type)
                    throw new EvaluationException("Expression expected, but '" + type.name() + "' is a class");
                return ((Place) resolved).get();
            }

            public Place place() throws Exception {
                Object resolved = resolve();
                if (resolved instanceof Place place) return place;
                throw new EvaluationException("Cannot assign a value to a class");
            }

            Object resolve() throws Exception {
                Place place = variable(parts.get(0));
                int i = 1;
                if (place == null) {
                    ReferenceType type = null;
                    for (; i <= parts.size() && type == null; i++) type = resolveType(String.join(".", parts.subList(0, i)));
                    i--;
                    if (type == null) throw new EvaluationException("Cannot find local variable '" + parts.get(0) + "'");
                    while (place == null && i < parts.size()) {
                        String part = parts.get(i++);
                        Field field = type.fieldByName(part);
                        if (field != null && field.isStatic()) place = statics(type, field);
                        else {
                            ReferenceType nested = load(type.name() + "$" + part);
                            if (nested == null)
                                throw new EvaluationException("Cannot find static field '" + part + "' in " + type.name());
                            type = nested;
                        }
                    }
                    if (place == null) return type;
                }
                for (; i < parts.size(); i++) place = field(place.get(), parts.get(i));
                return place;
            }
        }

        // ------------------------------------------------------------------ names and places

        private StackFrame frame() throws Exception {
            return ref.frame();
        }

        private ObjectReference self() throws Exception {
            ObjectReference self = frame().thisObject();
            if (self == null) throw new EvaluationException("'this' is not available in a static context");
            return self;
        }

        /** The instance enclosing an inner class' instance (javac's this$0), or null. */
        private ObjectReference outer(ObjectReference object) {
            for (Field field : object.referenceType().fields())
                if (field.name().startsWith("this$") && object.getValue(field) instanceof ObjectReference enclosing)
                    return enclosing;
            return null;
        }

        private ReferenceType enclosing(ReferenceType type) {
            int dollar = type.name().lastIndexOf('$');
            return dollar < 0 ? null : load(type.name().substring(0, dollar));
        }

        private Place variable(String name) throws Exception {
            for (Map<String, Place> scope : scopes) {
                Place place = scope.get(name);
                if (place != null) return place;
            }
            StackFrame frame = frame();
            try {
                LocalVariable local = frame.visibleVariableByName(name);
                if (local != null)
                    return new Place(local.typeName(), () -> frame().getValue(local), v -> frame().setValue(local, v));
            } catch (AbsentInformationException ignored) {
            }
            for (ObjectReference object = frame.thisObject(); object != null; object = outer(object)) {
                Field field = object.referenceType().fieldByName(name);
                if (field != null) return field(object, field);
                // A local captured by an anonymous or local class.
                Field captured = object.referenceType().fieldByName("val$" + name);
                if (captured != null) return field(object, captured);
            }
            for (ReferenceType type = frame.location().declaringType(); type != null; type = enclosing(type)) {
                Field field = type.fieldByName(name);
                if (field != null && field.isStatic()) return statics(type, field);
            }
            for (Import i : imports()) {
                if (!i.statics()) continue;
                String owner = i.onDemand() ? i.name() : i.name().endsWith("." + name) ? i.name().substring(0, i.name().length() - name.length() - 1) : null;
                ReferenceType type = owner == null ? null : resolveType(owner);
                Field field = type == null ? null : type.fieldByName(name);
                if (field != null && field.isStatic()) return statics(type, field);
            }
            return null;
        }

        private Place field(ObjectReference object, Field field) {
            if (field.isStatic()) return statics(field.declaringType(), field);
            return new Place(field.typeName(), () -> object.getValue(field), v -> {
                if (field.isFinal()) throw new EvaluationException("Cannot assign a value to final variable '" + field.name() + "'");
                object.setValue(field, v);
            });
        }

        private Place field(Value owner, String name) throws EvaluationException {
            if (owner == null) throw new EvaluationException("java.lang.NullPointerException: Cannot read field \"" + name + "\" because value is null");
            if (owner instanceof ArrayReference array && name.equals("length")) {
                return new Place("int", () -> vm.mirrorOf(array.length()), v -> {
                    throw new EvaluationException("Cannot assign a value to final variable 'length'");
                });
            }
            if (!(owner instanceof ObjectReference object))
                throw new EvaluationException(owner.type().name() + " cannot be dereferenced");
            Field field = object.referenceType().fieldByName(name);
            if (field == null) throw new EvaluationException("Cannot find field '" + name + "' in " + object.referenceType().name());
            return field(object, field);
        }

        private Place statics(ReferenceType type, Field field) {
            return new Place(field.typeName(), () -> type.getValue(field), v -> {
                if (field.isFinal() || !(field.declaringType() instanceof ClassType kind))
                    throw new EvaluationException("Cannot assign a value to final variable '" + field.name() + "'");
                kind.setValue(field, v);
            });
        }

        private Place element(ArrayReference array, int i) throws EvaluationException {
            if (i < 0 || i >= array.length())
                throw new EvaluationException("java.lang.ArrayIndexOutOfBoundsException: Index " + i + " out of bounds for length " + array.length());
            return new Place(((ArrayType) array.referenceType()).componentTypeName(), () -> array.getValue(i), v -> array.setValue(i, v));
        }

        private Place element(Value array, Value index) throws Exception {
            if (array == null) throw new EvaluationException("java.lang.NullPointerException: Cannot load from array because it is null");
            if (!(array instanceof ArrayReference elements))
                throw new EvaluationException("Array type expected; found: '" + array.type().name() + "'");
            return element(elements, index(index));
        }

        private int index(Value value) throws Exception {
            Value index = unboxed(value);
            if (!(index instanceof PrimitiveValue number) || !widens(number.type().name(), "int"))
                throw new EvaluationException("incompatible types: " + (value == null ? "<null>" : value.type().name()) + " cannot be converted to int");
            return number.intValue();
        }

        private Place superField(String name) throws Exception {
            ObjectReference self = self();
            ClassType up = ((ClassType) frame().location().declaringType()).superclass();
            Field field = up == null ? null : up.fieldByName(name);
            if (field == null) throw new EvaluationException("Cannot find field '" + name + "' in the superclass");
            return field(self, field);
        }

        private Value qualifiedThis(String written) throws Exception {
            String type = canonical(written);
            for (ObjectReference object = self(); object != null; object = outer(object))
                if (instanceOf(object.referenceType(), type)) return object;
            throw new EvaluationException("'" + written + ".this' is not an enclosing instance");
        }

        private Value classLiteral(String type) throws Exception {
            if (PRIMITIVES.contains(type) || type.equals("void")) {
                ReferenceType box = load(type.equals("void") ? "java.lang.Void" : BOX_OF.get(type));
                return box.getValue(box.fieldByName("TYPE"));
            }
            if (type.endsWith("[]")) return newArray(type, List.of(0), 0, 1).type() instanceof ReferenceType array ? array.classObject() : null;
            return typeNamed(type).classObject();
        }

        // ------------------------------------------------------------------ types

        private List<Import> imports() throws Exception {
            Path path = source(frame().location());
            if (path == null) return List.of();
            List<Import> cached = importsBySource.get(path);
            if (cached != null) return cached;
            List<Import> found = new ArrayList<>();
            try {
                Matcher m = Pattern.compile("(?m)^\\s*import\\s+(static\\s+)?([\\w.]+?)(\\.\\*)?\\s*;?\\s*$").matcher(Files.readString(path));
                while (m.find()) found.add(new Import(m.group(2), m.group(1) != null, m.group(3) != null));
            } catch (IOException ignored) {
            }
            importsBySource.put(path, found);
            return found;
        }

        /** A type by its canonical or binary name, loaded in the target if it has not been yet. */
        private ReferenceType load(String name) {
            List<ReferenceType> loaded = vm.classesByName(name);
            if (!loaded.isEmpty()) return loaded.get(0);
            String simple = name.substring(Math.max(name.lastIndexOf('.'), name.lastIndexOf('$')) + 1);
            // Only names that look like classes, so packages in a qualified name are not loaded.
            if (sideEffectFree || suspended == null || simple.isEmpty() || !Character.isUpperCase(simple.charAt(0))) return null;
            try {
                ClassType classes = (ClassType) vm.classesByName("java.lang.Class").get(0);
                Method forName = classes.concreteMethodByName("forName", "(Ljava/lang/String;ZLjava/lang/ClassLoader;)Ljava/lang/Class;");
                ClassLoaderReference loader = frame().location().declaringType().classLoader();
                Value found = invoke(null, forName, Arrays.asList(pin(vm.mirrorOf(name)), vm.mirrorOf(true), loader), false);
                return found instanceof ClassObjectReference type ? type.reflectedType() : null;
            } catch (Exception e) {
                return null;
            }
        }

        private ReferenceType resolveType(String written) throws Exception {
            Optional<ReferenceType> cached = types.get(written);
            if (cached != null) return cached.orElse(null);
            ReferenceType found = null;
            List<String> candidates = candidates(written);
            // Loaded classes first: loading each candidate in the target is slow.
            for (String candidate : candidates) {
                List<ReferenceType> loaded = vm.classesByName(candidate);
                if (!loaded.isEmpty()) {
                    found = loaded.get(0);
                    break;
                }
            }
            for (int i = 0; found == null && i < candidates.size(); i++) found = load(candidates.get(i));
            types.put(written, Optional.ofNullable(found));
            return found;
        }

        private List<String> candidates(String name) throws Exception {
            Set<String> out = new LinkedHashSet<>();
            int dot = name.indexOf('.');
            String first = dot < 0 ? name : name.substring(0, dot);
            String nested = dot < 0 ? "" : name.substring(dot).replace('.', '$');
            String declaring = frame().location().declaringType().name();
            for (String type = declaring; type != null; type = type.lastIndexOf('$') > 0 ? type.substring(0, type.lastIndexOf('$')) : null)
                out.add(type + "$" + first + nested);
            List<Import> imports = imports();
            for (Import i : imports)
                if (!i.statics() && !i.onDemand() && (i.name().equals(first) || i.name().endsWith("." + first))) out.add(i.name() + nested);
            int lastDot = declaring.lastIndexOf('.');
            out.add((lastDot < 0 ? "" : declaring.substring(0, lastDot + 1)) + first + nested);
            for (Import i : imports)
                if (i.onDemand() && !i.statics()) {
                    out.add(i.name() + "." + first + nested);
                    out.add(i.name() + "$" + first + nested);
                }
            out.add("java.lang." + first + nested);
            if (dot >= 0) {
                String[] parts = name.split("\\.");
                for (int i = parts.length; i >= 1; i--) {
                    StringBuilder candidate = new StringBuilder(String.join(".", Arrays.copyOfRange(parts, 0, i)));
                    for (int j = i; j < parts.length; j++) candidate.append('$').append(parts[j]);
                    out.add(candidate.toString());
                }
            }
            return new ArrayList<>(out);
        }

        private ReferenceType typeNamed(String name) throws Exception {
            ReferenceType type = load(name);
            if (type == null) throw new EvaluationException("Cannot load class " + name);
            return type;
        }

        /** A written type as the target names it: primitives, binary class names, dimensions. */
        private String canonical(String written) throws Exception {
            int bracket = written.indexOf('[');
            String base = bracket < 0 ? written : written.substring(0, bracket);
            String dimensions = bracket < 0 ? "" : written.substring(bracket);
            if (PRIMITIVES.contains(base) || base.equals("void")) return written;
            ReferenceType type = resolveType(base);
            if (type == null) throw new EvaluationException("Cannot resolve symbol '" + base + "'");
            return type.name() + dimensions;
        }

        // ------------------------------------------------------------------ calls

        private void sideEffect() throws EvaluationException {
            if (sideEffectFree) throw new EvaluationException("This expression may have side effects and is not evaluated here");
        }

        private void canInvoke() throws EvaluationException {
            sideEffect();
            if (suspended == null)
                throw new EvaluationException("Methods can be called only when stopped at a breakpoint or step");
        }

        private Value invoke(ObjectReference object, Method method, List<Value> args, boolean nonVirtual) throws Exception {
            canInvoke();
            List<Value> converted = arguments(method, args);
            // A breakpoint hit inside the call would wait on the event thread, which waits on us.
            var manager = vm.eventRequestManager();
            List<BreakpointRequest> enabled = manager.breakpointRequests().stream().filter(EventRequest::isEnabled).toList();
            enabled.forEach(EventRequest::disable);
            ThreadReference thread = ref.thread();
            int options = ObjectReference.INVOKE_SINGLE_THREADED | (nonVirtual ? ObjectReference.INVOKE_NONVIRTUAL : 0);
            try {
                if (method.isConstructor())
                    return pin(((ClassType) method.declaringType()).newInstance(thread, method, converted, options));
                if (method.isStatic()) {
                    if (method.declaringType() instanceof ClassType kind)
                        return pin(kind.invokeMethod(thread, method, converted, options));
                    return pin(((InterfaceType) method.declaringType()).invokeMethod(thread, method, converted, options));
                }
                return pin(object.invokeMethod(thread, method, converted, options));
            } catch (InvocationException thrown) {
                throw new EvaluationException("Method threw '" + thrown.exception().referenceType().name() + "' exception." + detail(thrown.exception(), thread));
            } finally {
                enabled.forEach(EventRequest::enable);
            }
        }

        private String detail(ObjectReference exception, ThreadReference thread) {
            try {
                Method message = exception.referenceType().methodsByName("getMessage", "()Ljava/lang/String;").get(0);
                Value text = exception.invokeMethod(thread, message, List.of(), ObjectReference.INVOKE_SINGLE_THREADED);
                return text instanceof StringReference s ? " " + s.value() : "";
            } catch (Exception e) {
                return "";
            }
        }

        private String describe(ObjectReference object) {
            try {
                return object.referenceType().name() + ": " + string(call(object, "getMessage", List.of()));
            } catch (Exception e) {
                return object.referenceType().name();
            }
        }

        private Value call(Expr receiver, String name, List<Expr> args) throws Exception {
            if (receiver instanceof NameExpr names) {
                Object resolved = names.resolve();
                if (resolved instanceof ReferenceType type) return callStatic(type, name, values(args));
                return call(((Place) resolved).get(), name, values(args));
            }
            Value owner = receiver.eval();
            return call(owner, name, values(args));
        }

        private Value call(Value owner, String name, List<Value> args) throws Exception {
            if (owner == null)
                throw new EvaluationException("java.lang.NullPointerException: Cannot invoke \"" + name + "()\" because value is null");
            if (!(owner instanceof ObjectReference object))
                throw new EvaluationException(owner.type().name() + " cannot be dereferenced");
            ReferenceType type = object instanceof ArrayReference ? load("java.lang.Object") : object.referenceType();
            if (object instanceof ArrayReference && name.equals("clone") && args.isEmpty()) {
                ArrayReference array = (ArrayReference) object;
                ArrayReference copy = (ArrayReference) newArray(array.referenceType().name(), List.of(array.length()), 0, 1);
                if (array.length() > 0) copy.setValues(array.getValues());
                return copy;
            }
            Method method = choose(type.methodsByName(name), args, "method '" + name + "'", type);
            return invoke(object, method, args, false);
        }

        private Value callStatic(ReferenceType type, String name, List<Value> args) throws Exception {
            List<Method> statics = new ArrayList<>();
            for (Method method : type.methodsByName(name)) if (method.isStatic()) statics.add(method);
            if (statics.isEmpty() && !type.methodsByName(name).isEmpty())
                throw new EvaluationException("Non-static method '" + name + "' cannot be referenced from a static context");
            return invoke(null, choose(statics, args, "method '" + name + "'", type), args, false);
        }

        private Value unqualifiedCall(String name, List<Value> args) throws Exception {
            StackFrame frame = frame();
            for (ObjectReference object = frame.thisObject(); object != null; object = outer(object)) {
                List<Method> methods = object.referenceType().methodsByName(name);
                if (!methods.isEmpty()) return invoke(object, choose(methods, args, "method '" + name + "'", object.referenceType()), args, false);
            }
            for (ReferenceType type = frame.location().declaringType(); type != null; type = enclosing(type)) {
                if (!type.methodsByName(name).isEmpty()) return callStatic(type, name, args);
            }
            for (Import i : imports()) {
                if (!i.statics()) continue;
                String owner = i.onDemand() ? i.name() : i.name().endsWith("." + name) ? i.name().substring(0, i.name().length() - name.length() - 1) : null;
                ReferenceType type = owner == null ? null : resolveType(owner);
                if (type != null && type.methodsByName(name).stream().anyMatch(Method::isStatic)) return callStatic(type, name, args);
            }
            throw new EvaluationException("Cannot resolve method '" + name + "'");
        }

        private Value superCall(String name, List<Value> args) throws Exception {
            ObjectReference self = self();
            ClassType up = ((ClassType) frame().location().declaringType()).superclass();
            if (up == null) throw new EvaluationException("Cannot resolve method '" + name + "'");
            Method method = choose(up.methodsByName(name), args, "method '" + name + "'", up);
            if (method.isAbstract()) throw new EvaluationException("Abstract method '" + name + "' cannot be accessed directly");
            return invoke(self, method, args, !method.isStatic());
        }

        private Value construct(String typeName, List<Value> args) throws Exception {
            ReferenceType type = typeNamed(typeName);
            if (!(type instanceof ClassType kind) || kind.isAbstract())
                throw new EvaluationException("'" + type.name() + "' is abstract; cannot be instantiated");
            List<Method> constructors = new ArrayList<>();
            for (Method method : kind.methods()) if (method.isConstructor()) constructors.add(method);
            try {
                return invoke(null, choose(constructors, args, "constructor", kind), args, false);
            } catch (EvaluationException missing) {
                // An inner class' constructor takes its enclosing instance first.
                ReferenceType enclosing = enclosing(kind);
                if (enclosing == null || frame().thisObject() == null) throw missing;
                for (ObjectReference object = frame().thisObject(); object != null; object = outer(object)) {
                    if (!instanceOf(object.referenceType(), enclosing.name())) continue;
                    List<Value> withOuter = new ArrayList<>();
                    withOuter.add(object);
                    withOuter.addAll(args);
                    return invoke(null, choose(constructors, withOuter, "constructor", kind), withOuter, false);
                }
                throw missing;
            }
        }

        private Method choose(List<Method> methods, List<Value> args, String what, ReferenceType type) throws EvaluationException {
            for (int phase = 1; phase <= 3; phase++) {
                Method best = null;
                for (Method method : methods)
                    if (applicable(method, args, phase) && (best == null || moreSpecific(method, best))) best = method;
                if (best != null) return best;
            }
            List<String> found = new ArrayList<>();
            for (Value arg : args) found.add(arg == null ? "null" : arg.type().name());
            if (methods.isEmpty()) throw new EvaluationException("Cannot find " + what + " in " + type.name());
            throw new EvaluationException("Cannot find " + what + " in " + type.name() + " applicable to (" + join(found) + ")");
        }

        private boolean applicable(Method method, List<Value> args, int phase) {
            List<String> params = method.argumentTypeNames();
            if (phase < 3 || !method.isVarArgs()) {
                if (params.size() != args.size()) return false;
                for (int i = 0; i < args.size(); i++) if (!convertible(args.get(i), params.get(i), phase > 1)) return false;
                return true;
            }
            int fixed = params.size() - 1;
            if (args.size() < fixed) return false;
            for (int i = 0; i < fixed; i++) if (!convertible(args.get(i), params.get(i), true)) return false;
            String component = params.get(fixed).substring(0, params.get(fixed).length() - 2);
            for (int i = fixed; i < args.size(); i++) if (!convertible(args.get(i), component, true)) return false;
            return true;
        }

        private boolean convertible(Value value, String type, boolean boxing) {
            boolean primitive = PRIMITIVES.contains(type);
            if (value == null) return !primitive;
            if (value instanceof PrimitiveValue p) {
                String from = p.type().name();
                return primitive ? widens(from, type) : boxing && boxAccepts(from, type);
            }
            if (primitive) {
                String unboxed = UNBOX_OF.get(value.type().name());
                return boxing && unboxed != null && widens(unboxed, type);
            }
            return instanceOf(value.type(), type);
        }

        private boolean moreSpecific(Method a, Method b) {
            List<String> x = a.argumentTypeNames();
            List<String> y = b.argumentTypeNames();
            if (x.size() != y.size()) return false;
            for (int i = 0; i < x.size(); i++) {
                String from = x.get(i);
                String to = y.get(i);
                if (from.equals(to)) continue;
                if (PRIMITIVES.contains(from) || PRIMITIVES.contains(to)) {
                    if (!PRIMITIVES.contains(from) || !PRIMITIVES.contains(to) || !widens(from, to)) return false;
                    continue;
                }
                List<ReferenceType> loaded = vm.classesByName(from);
                if (to.equals("java.lang.Object")) continue;
                if (loaded.isEmpty() || !instanceOf(loaded.get(0), to)) return false;
            }
            return true;
        }

        private List<Value> arguments(Method method, List<Value> args) throws Exception {
            List<String> params = method.argumentTypeNames();
            List<Value> converted = new ArrayList<>();
            boolean spread =
                    method.isVarArgs()
                            && !(args.size() == params.size() && convertible(args.get(args.size() - 1), params.get(params.size() - 1), true));
            int fixed = spread ? params.size() - 1 : params.size();
            for (int i = 0; i < fixed; i++) converted.add(convert(args.get(i), params.get(i)));
            if (spread) {
                String arrayType = params.get(fixed);
                String component = arrayType.substring(0, arrayType.length() - 2);
                ArrayReference array = (ArrayReference) newArray(arrayType, List.of(args.size() - fixed), 0, 1);
                for (int i = fixed; i < args.size(); i++) array.setValue(i - fixed, convert(args.get(i), component));
                converted.add(array);
            }
            return converted;
        }

        // ------------------------------------------------------------------ arrays

        private Value newArray(String arrayType, List<Integer> lengths, int level, int dimensions) throws Exception {
            int length = lengths.get(level);
            ArrayReference array;
            List<ReferenceType> loaded = vm.classesByName(arrayType);
            if (!loaded.isEmpty()) array = ((ArrayType) loaded.get(0)).newInstance(length);
            else {
                // Not loaded yet: java.lang.reflect.Array.newInstance(component, length) loads it.
                String component = arrayType.substring(0, arrayType.length() - 2);
                Value componentClass = component.endsWith("[]")
                        ? ((ArrayReference) newArray(component, List.of(0), 0, 1)).referenceType().classObject()
                        : classLiteral(component);
                ClassType reflect = (ClassType) typeNamed("java.lang.reflect.Array");
                Method create = reflect.concreteMethodByName("newInstance", "(Ljava/lang/Class;I)Ljava/lang/Object;");
                array = (ArrayReference) invoke(null, create, List.of(componentClass, vm.mirrorOf(length)), false);
            }
            pin(array);
            if (level + 1 < lengths.size()) {
                String component = arrayType.substring(0, arrayType.length() - 2);
                for (int i = 0; i < length; i++) array.setValue(i, newArray(component, lengths, level + 1, dimensions - 1));
            }
            return array;
        }

        @SuppressWarnings("unchecked")
        private Value array(String arrayType, List<Object> elements) throws Exception {
            if (!arrayType.endsWith("[]")) throw new EvaluationException("Illegal initializer for " + arrayType);
            String component = arrayType.substring(0, arrayType.length() - 2);
            ArrayReference array = (ArrayReference) newArray(arrayType, List.of(elements.size()), 0, 1);
            for (int i = 0; i < elements.size(); i++) {
                Object element = elements.get(i);
                Value value = element instanceof List<?> nested
                        ? array(component, (List<Object>) nested)
                        : convert(((Expr) element).eval(), component);
                array.setValue(i, value);
            }
            return array;
        }

        // ------------------------------------------------------------------ conversions

        private Value unboxed(Value value) {
            if (value instanceof ObjectReference object && UNBOX_OF.containsKey(object.referenceType().name()))
                return object.getValue(object.referenceType().fieldByName("value"));
            return value;
        }

        private Value box(PrimitiveValue value) throws Exception {
            ClassType box = (ClassType) typeNamed(BOX_OF.get(value.type().name()));
            Method valueOf = box.concreteMethodByName("valueOf", "(" + value.type().signature() + ")" + box.signature());
            return invoke(null, valueOf, List.of(value), false);
        }

        private Value primitive(PrimitiveValue value, String type) throws EvaluationException {
            if (value instanceof BooleanValue != type.equals("boolean"))
                throw new EvaluationException("incompatible types: " + value.type().name() + " cannot be converted to " + type);
            return switch (type) {
                case "boolean" -> value;
                case "byte" -> vm.mirrorOf(value.byteValue());
                case "short" -> vm.mirrorOf(value.shortValue());
                case "char" -> vm.mirrorOf(value.charValue());
                case "int" -> vm.mirrorOf(value.intValue());
                case "long" -> vm.mirrorOf(value.longValue());
                case "float" -> vm.mirrorOf(value.floatValue());
                default -> vm.mirrorOf(value.doubleValue());
            };
        }

        private EvaluationException incompatible(Value value, String type) {
            return new EvaluationException("incompatible types: " + (value == null ? "<null>" : value.type().name()) + " cannot be converted to " + type);
        }

        /** Assignment conversion: widening, constant narrowing, boxing, unboxing, reference widening. */
        private Value convert(Value value, String type) throws Exception {
            if (value instanceof VoidValue) throw new EvaluationException("incompatible types: void cannot be converted to " + type);
            boolean primitive = PRIMITIVES.contains(type);
            if (value == null) {
                if (primitive) throw incompatible(null, type);
                return null;
            }
            if (primitive) {
                if (!(unboxed(value) instanceof PrimitiveValue p)) throw incompatible(value, type);
                String from = p.type().name();
                if (from.equals(type)) return p;
                if (from.equals("boolean") || type.equals("boolean")) throw incompatible(value, type);
                if (widens(from, type)) return primitive(p, type);
                boolean constant = from.equals("int") || from.equals("short") || from.equals("char") || from.equals("byte");
                if (constant && (type.equals("byte") || type.equals("short") || type.equals("char"))) {
                    int v = p.intValue();
                    boolean fits = switch (type) {
                        case "byte" -> v >= Byte.MIN_VALUE && v <= Byte.MAX_VALUE;
                        case "short" -> v >= Short.MIN_VALUE && v <= Short.MAX_VALUE;
                        default -> v >= Character.MIN_VALUE && v <= Character.MAX_VALUE;
                    };
                    if (fits) return primitive(p, type);
                }
                throw new EvaluationException("incompatible types: possible lossy conversion from " + from + " to " + type);
            }
            if (value instanceof PrimitiveValue p) {
                String unboxedType = UNBOX_OF.get(type);
                if (unboxedType != null) return box((PrimitiveValue) convert(p, unboxedType));
                if (!boxAccepts(p.type().name(), type)) throw incompatible(value, type);
                return box(p);
            }
            if (!instanceOf(value.type(), type)) throw incompatible(value, type);
            return value;
        }

        /** A compound assignment's or ++'s result cast back to the variable's type. */
        private Value narrowed(Value value, String type) throws Exception {
            if (type.equals("java.lang.String") || !(value instanceof PrimitiveValue p)) return convert(value, type);
            if (PRIMITIVES.contains(type)) return primitive(p, type);
            String unboxedType = UNBOX_OF.get(type);
            return unboxedType != null ? box((PrimitiveValue) primitive(p, unboxedType)) : convert(value, type);
        }

        private Value cast(Value value, String type) throws Exception {
            if (PRIMITIVES.contains(type)) {
                if (!(unboxed(value) instanceof PrimitiveValue p))
                    throw new EvaluationException(value == null ? "java.lang.NullPointerException" : "Inconvertible types; cannot cast '" + value.type().name() + "' to '" + type + "'");
                return primitive(p, type);
            }
            if (value == null) return null;
            if (value instanceof PrimitiveValue p) {
                String unboxedType = UNBOX_OF.get(type);
                if (unboxedType != null) return box((PrimitiveValue) primitive(p, unboxedType));
                if (!boxAccepts(p.type().name(), type))
                    throw new EvaluationException("Inconvertible types; cannot cast '" + p.type().name() + "' to '" + type + "'");
                return box(p);
            }
            if (!instanceOf(value.type(), type))
                throw new EvaluationException("java.lang.ClassCastException: class " + value.type().name() + " cannot be cast to class " + type);
            return value;
        }

        private boolean truth(Value value) throws Exception {
            if (unboxed(value) instanceof BooleanValue b) return b.value();
            throw incompatible(value, "boolean");
        }

        private String string(Value value) throws Exception {
            if (value == null) return "null";
            if (value instanceof StringReference text) return text.value();
            Value plain = unboxed(value);
            if (plain instanceof PrimitiveValue p) {
                return switch (p.type().name()) {
                    case "boolean" -> String.valueOf(((BooleanValue) p).value());
                    case "char" -> String.valueOf(p.charValue());
                    case "long" -> String.valueOf(p.longValue());
                    case "float" -> String.valueOf(p.floatValue());
                    case "double" -> String.valueOf(p.doubleValue());
                    default -> String.valueOf(p.intValue());
                };
            }
            if (value instanceof VoidValue) throw new EvaluationException("'void' type not allowed here");
            Value text = call(value, "toString", List.of());
            return text instanceof StringReference s ? s.value() : "null";
        }

        // ------------------------------------------------------------------ operators

        private PrimitiveValue numeric(Value value, String op) throws EvaluationException {
            Value plain = unboxed(value);
            if (plain instanceof PrimitiveValue p && !(p instanceof BooleanValue)) return p;
            if (value == null) throw new EvaluationException("java.lang.NullPointerException");
            throw new EvaluationException("Operator '" + op + "' cannot be applied to '" + value.type().name() + "'");
        }

        private Value unary(String op, Value value) throws Exception {
            if (op.equals("!")) {
                if (unboxed(value) instanceof BooleanValue b) return vm.mirrorOf(!b.value());
                throw new EvaluationException("Operator '!' cannot be applied to '" + (value == null ? "null" : value.type().name()) + "'");
            }
            PrimitiveValue p = numeric(value, op);
            String type = promoted(p.type().name());
            if (op.equals("~")) {
                if (type.equals("int")) return vm.mirrorOf(~p.intValue());
                if (type.equals("long")) return vm.mirrorOf(~p.longValue());
                throw new EvaluationException("Operator '~' cannot be applied to '" + type + "'");
            }
            boolean negate = op.equals("-");
            return switch (type) {
                case "int" -> vm.mirrorOf(negate ? -p.intValue() : p.intValue());
                case "long" -> vm.mirrorOf(negate ? -p.longValue() : p.longValue());
                case "float" -> vm.mirrorOf(negate ? -p.floatValue() : p.floatValue());
                default -> vm.mirrorOf(negate ? -p.doubleValue() : p.doubleValue());
            };
        }

        private boolean equal(Value a, Value b) throws Exception {
            boolean primitiveA = a instanceof PrimitiveValue;
            boolean primitiveB = b instanceof PrimitiveValue;
            if (primitiveA || primitiveB) {
                Value x = unboxed(a);
                Value y = unboxed(b);
                if (x == null || y == null) throw new EvaluationException("java.lang.NullPointerException");
                if (x instanceof BooleanValue p && y instanceof BooleanValue q) return p.value() == q.value();
                if (x instanceof BooleanValue || y instanceof BooleanValue || !(x instanceof PrimitiveValue) || !(y instanceof PrimitiveValue))
                    throw new EvaluationException("Operator '==' cannot be applied to '" + a.type().name() + "', '" + b.type().name() + "'");
                PrimitiveValue p = (PrimitiveValue) x;
                PrimitiveValue q = (PrimitiveValue) y;
                return switch (promoted(p.type().name(), q.type().name())) {
                    case "double" -> p.doubleValue() == q.doubleValue();
                    case "float" -> p.floatValue() == q.floatValue();
                    case "long" -> p.longValue() == q.longValue();
                    default -> p.intValue() == q.intValue();
                };
            }
            return Objects.equals(a, b);
        }

        private Value binary(String op, Value a, Value b) throws Exception {
            switch (op) {
                case "==":
                    return vm.mirrorOf(equal(a, b));
                case "!=":
                    return vm.mirrorOf(!equal(a, b));
                case "+":
                    if (a instanceof StringReference || b instanceof StringReference)
                        return pin(vm.mirrorOf(string(a) + string(b)));
                    break;
                default:
                    break;
            }
            if ((op.equals("&") || op.equals("|") || op.equals("^")) && unboxed(a) instanceof BooleanValue x && unboxed(b) instanceof BooleanValue y) {
                return vm.mirrorOf(switch (op) {
                    case "&" -> x.value() & y.value();
                    case "|" -> x.value() | y.value();
                    default -> x.value() ^ y.value();
                });
            }
            PrimitiveValue p = numeric(a, op);
            PrimitiveValue q = numeric(b, op);
            if (op.equals("<<") || op.equals(">>") || op.equals(">>>")) {
                String type = promoted(p.type().name());
                String distanceType = promoted(q.type().name());
                if (type.equals("float") || type.equals("double") || distanceType.equals("float") || distanceType.equals("double"))
                    throw bad(op, p, q);
                long distance = q.longValue();
                if (type.equals("int")) {
                    int x = p.intValue();
                    int d = (int) distance;
                    return vm.mirrorOf(op.equals("<<") ? x << d : op.equals(">>") ? x >> d : x >>> d);
                }
                long x = p.longValue();
                return vm.mirrorOf(op.equals("<<") ? x << distance : op.equals(">>") ? x >> distance : x >>> distance);
            }
            String type = promoted(p.type().name(), q.type().name());
            switch (type) {
                case "double": {
                    double x = p.doubleValue();
                    double y = q.doubleValue();
                    return switch (op) {
                        case "+" -> vm.mirrorOf(x + y);
                        case "-" -> vm.mirrorOf(x - y);
                        case "*" -> vm.mirrorOf(x * y);
                        case "/" -> vm.mirrorOf(x / y);
                        case "%" -> vm.mirrorOf(x % y);
                        case "<" -> vm.mirrorOf(x < y);
                        case ">" -> vm.mirrorOf(x > y);
                        case "<=" -> vm.mirrorOf(x <= y);
                        case ">=" -> vm.mirrorOf(x >= y);
                        default -> throw bad(op, p, q);
                    };
                }
                case "float": {
                    float x = p.floatValue();
                    float y = q.floatValue();
                    return switch (op) {
                        case "+" -> vm.mirrorOf(x + y);
                        case "-" -> vm.mirrorOf(x - y);
                        case "*" -> vm.mirrorOf(x * y);
                        case "/" -> vm.mirrorOf(x / y);
                        case "%" -> vm.mirrorOf(x % y);
                        case "<" -> vm.mirrorOf(x < y);
                        case ">" -> vm.mirrorOf(x > y);
                        case "<=" -> vm.mirrorOf(x <= y);
                        case ">=" -> vm.mirrorOf(x >= y);
                        default -> throw bad(op, p, q);
                    };
                }
                case "long": {
                    long x = p.longValue();
                    long y = q.longValue();
                    if ((op.equals("/") || op.equals("%")) && y == 0) throw new EvaluationException("java.lang.ArithmeticException: / by zero");
                    return switch (op) {
                        case "+" -> vm.mirrorOf(x + y);
                        case "-" -> vm.mirrorOf(x - y);
                        case "*" -> vm.mirrorOf(x * y);
                        case "/" -> vm.mirrorOf(x / y);
                        case "%" -> vm.mirrorOf(x % y);
                        case "&" -> vm.mirrorOf(x & y);
                        case "|" -> vm.mirrorOf(x | y);
                        case "^" -> vm.mirrorOf(x ^ y);
                        case "<" -> vm.mirrorOf(x < y);
                        case ">" -> vm.mirrorOf(x > y);
                        case "<=" -> vm.mirrorOf(x <= y);
                        case ">=" -> vm.mirrorOf(x >= y);
                        default -> throw bad(op, p, q);
                    };
                }
                default: {
                    int x = p.intValue();
                    int y = q.intValue();
                    if ((op.equals("/") || op.equals("%")) && y == 0) throw new EvaluationException("java.lang.ArithmeticException: / by zero");
                    return switch (op) {
                        case "+" -> vm.mirrorOf(x + y);
                        case "-" -> vm.mirrorOf(x - y);
                        case "*" -> vm.mirrorOf(x * y);
                        case "/" -> vm.mirrorOf(x / y);
                        case "%" -> vm.mirrorOf(x % y);
                        case "&" -> vm.mirrorOf(x & y);
                        case "|" -> vm.mirrorOf(x | y);
                        case "^" -> vm.mirrorOf(x ^ y);
                        case "<" -> vm.mirrorOf(x < y);
                        case ">" -> vm.mirrorOf(x > y);
                        case "<=" -> vm.mirrorOf(x <= y);
                        case ">=" -> vm.mirrorOf(x >= y);
                        default -> throw bad(op, p, q);
                    };
                }
            }
        }

        private EvaluationException bad(String op, Value a, Value b) {
            return new EvaluationException("Operator '" + op + "' cannot be applied to '" + a.type().name() + "', '" + b.type().name() + "'");
        }

        private void tick() throws EvaluationException {
            if (System.nanoTime() > deadline) throw new EvaluationException("Evaluation took too long and was stopped");
        }
    }

    private interface PlaceSource {
        Place place() throws Exception;
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
