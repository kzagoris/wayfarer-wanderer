using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Passenger.Text;

namespace WayfarerWanderer.App;

public static class AcceptanceRunner
{
    public static int Run(AcceptanceOptions options)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            Directory.CreateDirectory(options.Results);
            using JsonDocument source = JsonDocument.Parse(File.ReadAllText(options.CaseFile));
            string name = source.RootElement.TryGetProperty("name", out JsonElement named)
                ? named.GetString() ?? "unnamed"
                : "unnamed";

            Program.BuildAvaloniaApp()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();

            TextDocument document = new("class Main {\n    int count;\n}");
            EditorWindow window = EditorApplication.CreateWindow(document);
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            bool actionRan = window.Actions.TryExecute(EditorActions.ToggleBreakpoint);
            Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
            if (frame is null)
            {
                throw new InvalidOperationException("The acceptance window rendered no frame.");
            }
            string image = Path.Combine(options.Results, "editor.png");
            using (frame)
            {
                frame.Save(image, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            window.Close();

            string json = JsonSerializer.Serialize(new
            {
                name,
                exited = 0,
                actionRan,
                breakpoint = window.Breakpoints.IsMarked(0),
                image,
                elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
            File.WriteAllText(Path.Combine(options.Results, "observations.json"), json);
            return actionRan ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            Console.Error.WriteLine($"acceptance: {error.Message}");
            return 2;
        }
    }
}
