using System.Threading.Channels;
using Passenger.Dap;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

/// <summary>Evaluate interprets Java as IntelliJ IDEA's Evaluate Expression does: every operator, casts, instanceof,
/// assignment, boxing, overloads, varargs, object and array creation, outer instances, static imports and code
/// fragments with loops; Set Value writes a row; completion offers members and names in scope.</summary>
[Collection(JvmTrustCollection.Name)]
public sealed class JvmEvaluateTests
{
    private const string Program = """
        package com.example.console;

        import java.util.*;
        import static java.lang.Math.abs;

        public class App {
            static int counter = 7;
            private final String greeting = "Hello";
            private List<String> names = new ArrayList<>(List.of("Ada", "Linus"));
            private Integer boxed = 41;
            private long big = 10L;
            private char letter = 'a';
            private int[] primes = {2, 3, 5};

            class Inner {
                int depth = 2;

                int run(int factor) {
                    int local = factor * depth + names.size() * 0;
                    return local; // stop
                }
            }

            public static void main(String[] args) {
                App app = new App();
                System.out.println(app.new Inner().run(3));
            }

            String greet(String name) { return greeting + ", " + name; }
            static String join(String... parts) { return String.join("-", parts); }
            static String pick(Object o) { return "object"; }
            static String pick(String s) { return "string"; }
            static String pick(int i) { return "int"; }
            static String pick(long l) { return "long"; }
        }
        """;

    private static readonly (string Expression, string Expected)[] Cases =
    [
        ("local", "6"),
        ("factor * depth + 1", "7"),
        ("local = 4", "4"),
        ("local", "4"),
        ("local += 3", "7"),
        ("local++", "7"),
        ("local", "8"),
        ("--local", "7"),
        ("local *= 2.5", "17"),
        ("counter", "7"),
        ("App.counter", "7"),
        ("greeting", "\"Hello\""),
        ("this.depth", "2"),
        ("App.this.greeting.length()", "5"),
        ("greet(\"Bob\")", "\"Hello, Bob\""),
        ("names.size()", "2"),
        ("names.get(0) + \"!\"", "\"Ada!\""),
        ("boxed + 1", "42"),
        ("boxed == 41", "true"),
        ("big * 2", "20"),
        ("letter + 1", "98"),
        ("(char) (letter + 1)", "'b'"),
        ("(int) 3.9", "3"),
        ("(byte) 200", "-56"),
        ("10 / 4", "2"),
        ("10 / 4.0", "2.5"),
        ("7 % 3", "1"),
        ("1 << 4", "16"),
        ("-8 >>> 28", "15"),
        ("~5", "-6"),
        ("5 & 3 | 8 ^ 1", "9"),
        ("0x1F + 0b11 + 010 + 1_000", "1042"),
        ("1.5f * 2", "3.0"),
        ("Integer.MAX_VALUE + 1", "-2147483648"),
        ("-2147483648", "-2147483648"),
        ("Long.MIN_VALUE", "-9223372036854775808"),
        ("true && !false", "true"),
        ("false || 1 > 2", "false"),
        ("3 > 2 ? \"yes\" : \"no\"", "\"yes\""),
        ("\"a\" + 1 + 2", "\"a12\""),
        ("1 + 2 + \"a\"", "\"3a\""),
        ("\"tab\\t\" + 'x' + null", "\"tab\\tx" + "null\""),
        ("names instanceof List", "true"),
        ("greeting instanceof String s && s.isEmpty()", "false"),
        ("(Object) greeting instanceof Integer", "false"),
        ("primes[1] + primes.length", "6"),
        ("primes[0] = 11", "11"),
        ("primes[0]", "11"),
        ("pick(1)", "\"int\""),
        ("pick(1L)", "\"long\""),
        ("pick(\"x\")", "\"string\""),
        ("pick(names)", "\"object\""),
        ("pick(null)", "\"string\""),
        ("join(\"a\", \"b\", \"c\")", "\"a-b-c\""),
        ("join()", "\"\""),
        ("abs(-3)", "3"),
        ("Math.max(2, 9)", "9"),
        ("java.util.Collections.emptyList().size()", "0"),
        ("new ArrayList<String>().isEmpty()", "true"),
        ("new StringBuilder(\"ab\").reverse().toString()", "\"ba\""),
        ("new int[]{1, 2, 3}.length", "3"),
        ("(new int[3][2])[1].length", "2"),
        ("String.valueOf(new char[]{'o', 'k'})", "\"ok\""),
        ("int.class.getName()", "\"int\""),
        ("String.class.getSimpleName()", "\"String\""),
        ("Map.Entry.class.getName()", "\"java.util.Map$Entry\""),
        ("int sum = 0; for (int i = 1; i <= 4; i++) sum += i; sum", "10"),
        ("var total = 0L; for (String n : names) total += n.length(); return total;", "8"),
        ("int x = 1; while (true) { x *= 2; if (x > 50) break; } x", "64"),
        ("int n = 0; outer: for (int i = 0; i < 5; i++) for (int j = 0; j < 5; j++) { if (j == 2) continue outer; n++; } n", "10"),
        ("StringBuilder b = new StringBuilder(); for (int p : primes) b.append(p).append(','); b.toString()", "\"11,3,5,\""),
        ("String text = \"\"\"\n    hi\n    there\"\"\"; text", "\"hi\\nthere\""),
        ("boxed = 50", "50"),
        ("boxed++", "50"),
        ("boxed.intValue()", "51"),
    ];

    private static readonly (string Expression, string Error)[] Errors =
    [
        ("1 / 0", "ArithmeticException"),
        ("missing", "Cannot find local variable 'missing'"),
        ("local = \"text\"", "incompatible types"),
        ("local = 2.5", "lossy conversion"),
        ("x -> x", "Lambdas"),
        ("names.get(5)", "Method threw 'java.lang.IndexOutOfBoundsException'"),
        ("primes[9]", "ArrayIndexOutOfBoundsException"),
        ("greeting = \"x\"", "final"),
        ("(Integer) (Object) greeting", "ClassCastException"),
        ("1 +", "Incomplete expression"),
        ("k=4", "Cannot find local variable 'k'"),
    ];

    [Fact]
    public async Task EvaluateInterpretsJava()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await using JvmConsoleDebugTests.Module module = await JvmConsoleDebugTests.Module.CreateAsync(cancel, Program);
        int line = Array.FindIndex(Program.Split('\n'), text => text.Contains("// stop")) + 1;
        await using var debug = new JvmInteractiveDebugger();
        var stops = Channel.CreateUnbounded<JvmDebugPause>();
        debug.Paused += pause => stops.Writer.TryWrite(pause);
        await debug.StartAsync(module.Snapshot, module.Launch(), new Dictionary<string, int[]> { [module.Source] = [line] }, cancel);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        JvmDebugPause paused = await stops.Reader.ReadAsync(timeout.Token);
        int frame = paused.Frames[0].Id;

        List<string> failures = [];
        foreach (var (expression, expected) in Cases)
        {
            try
            {
                string actual = (await debug.EvaluateAsync(expression, frame)).Value;
                if (actual != expected && !actual.EndsWith("} " + expected, StringComparison.Ordinal))
                    failures.Add($"{expression} => {actual}, expected {expected}");
            }
            catch (DapException error) { failures.Add($"{expression} => error {error.Message}, expected {expected}"); }
        }
        foreach (var (expression, error) in Errors)
        {
            try { failures.Add($"{expression} => {(await debug.EvaluateAsync(expression, frame)).Value}, expected an error with {error}"); }
            catch (DapException thrown) when (thrown.Message.Contains(error, StringComparison.Ordinal)) { }
            catch (DapException thrown) { failures.Add($"{expression} => error {thrown.Message}, expected {error}"); }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // Hovers never change the program.
        await Assert.ThrowsAsync<DapException>(() => debug.EvaluateAsync("local = 1", frame, "hover"));
        await Assert.ThrowsAsync<DapException>(() => debug.EvaluateAsync("names.clear()", frame, "hover"));
        Assert.Equal("2", (await debug.EvaluateAsync("depth", frame, "hover")).Value);

        // Set Value on a local of the frame, then on an element of an array.
        Assert.Equal("103", (await debug.SetVariableAsync(frame, "local", "factor + 100")).Value);
        Assert.Equal("103", (await debug.EvaluateAsync("local", frame)).Value);
        JvmDebugVariable primes = await debug.EvaluateAsync("primes", frame);
        Assert.Equal("7", (await debug.SetVariableAsync(primes.Reference, "[2]", "7")).Value);
        Assert.Equal("7", (await debug.EvaluateAsync("primes[2]", frame)).Value);

        IReadOnlyList<JvmDebugCompletion> names = await debug.CompletionsAsync("loc", 3, frame);
        Assert.Contains(names, item => item.Label == "local" && item.Start == 0 && item.Length == 3);
        IReadOnlyList<JvmDebugCompletion> members = await debug.CompletionsAsync("names.si", 8, frame);
        Assert.Contains(members, item => item.Label == "size()" && item.Text == "size()" && item.Start == 6);
        IReadOnlyList<JvmDebugCompletion> statics = await debug.CompletionsAsync("Math.ma", 7, frame);
        Assert.Contains(statics, item => item.Label == "max(int, int)");

        await debug.ContinueAsync();
    }
}
