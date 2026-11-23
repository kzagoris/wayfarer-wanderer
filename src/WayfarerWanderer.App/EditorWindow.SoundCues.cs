// From Passenger 8a1df7e4 src/Passenger.App/MainWindow.SoundCues.cs, MIT (notices/Passenger/LICENSE): one editor, not many,
// and Wayfarer's build, test run and debugger in place of upstream's; a cue plays through Sounds where a person sees the
// window, and a headless window keeps only what it would have played.
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell;

namespace WayfarerWanderer.App;

/// <summary>The sound cues (<see cref="SoundCues"/>): one as the caret comes to another line of the same file holding an
/// error, a warning, a breakpoint, a folded region or a change, the first of those Settings turns on, and nothing as it
/// moves along a line; one as a build or a test run passes or fails, as the debugger stops the person's program, as a
/// command fails in the terminal and as a save is refused.</summary>
public sealed partial class EditorWindow
{
    private readonly List<SoundCue> cued = [];

    /// <summary>The file and line the caret was last on, which a cue is told against.</summary>
    private (string? File, int Line) caretCuedAt;

    /// <summary>Whether a cue the system's player could not play has said why, which it says once.</summary>
    private bool cueRefusalSaid;

    /// <summary>Every cue the window played, in order, or would have where nobody sees it.</summary>
    public IReadOnlyList<SoundCue> CuesPlayed => cued;

    private void AddSoundCues()
    {
        Editor.SelectionChanged += (_, _) => CueCaretLine();
    }

    /// <summary>A project's builds, test runs and debugger stops cued while it is the window's.</summary>
    private void BindSoundCues(ProjectSession next)
    {
        next.BuildEnded += passed =>
        {
            if (Session == next)
            {
                Cue(passed ? SoundCue.BuildPassed : SoundCue.BuildFailed);
            }
        };
        next.TestsEnded += ended =>
        {
            if (Session == next && !ended.WasCancelled)
            {
                Cue(ended.ExitCode != 0 || ended.Cases.Any(c => c.Outcome == WayfarerWanderer.Jvm.TestOutcome.Failed) ? SoundCue.TestsFailed : SoundCue.TestsPassed);
            }
        };
        next.PausedAt += _ =>
        {
            if (Session == next)
            {
                Cue(SoundCue.DebuggerStopped);
            }
        };
    }

    /// <summary>Plays a cue where Settings turns it on and has cues play now (<see cref="SoundCues.Play"/>).</summary>
    public void Cue(SoundCue cue)
    {
        UiThread.Check();
        Shell.Settings.AppSettings settings = SettingsKept.Effective;
        if (!SoundCues.Play(settings, ScreenReaders.Running) || !SoundCues.IsOn(settings, cue))
        {
            return;
        }
        cued.Add(cue);
        if (EditorApplication.State is { } state)
        {
            IdeErrors.Observe(PlayCueAsync(cue, Path.Combine(state.Root, "cache", "sound-cues")));
        }
    }

    private async Task PlayCueAsync(SoundCue cue, string folder)
    {
        string file = await BackgroundWork.Run(() => SoundCues.FileOf(cue, folder));
        if (await Sounds.PlayAsync(file) is { } why && !cueRefusalSaid)
        {
            cueRefusalSaid = true;
            IdeErrors.Write($"Sound cues: {why}");
        }
    }

    /// <summary>The caret on another line of the file it was on: that line's cue, where it has one.</summary>
    private void CueCaretLine()
    {
        string? file = Editor.IsVisible ? SourcePath : null;
        int line = Editor.CaretPosition.Line;
        (string? was, int wasLine) = caretCuedAt;
        caretCuedAt = (file, line);
        if (file is null || file != was || line == wasLine || !SoundCues.Play(SettingsKept.Effective, ScreenReaders.Running))
        {
            return;
        }
        foreach (SoundCue cue in LineCues(line, wasLine))
        {
            if (SoundCues.IsOn(SettingsKept.Effective, cue))
            {
                Cue(cue);
                return;
            }
        }
    }

    /// <summary>What a line holds that a cue tells, the most pressing first: a change only where the caret came into it
    /// from <paramref name="from"/>, not along its lines.</summary>
    private IEnumerable<SoundCue> LineCues(int line, int from)
    {
        TextSnapshot text = Editor.Document.Snapshot;
        LineSpan span = text.GetLine(Math.Min(line, text.LineCount - 1));
        List<Marker> marked = [.. Editor.Markers.Where(m => m.Range.Start <= span.End && (m.Range.End > span.Start || m.Range.Start == span.Start))];
        if (marked.Any(m => m.Kind == MarkerKind.Error))
        {
            yield return SoundCue.ErrorLine;
        }
        if (marked.Any(m => m.Kind == MarkerKind.Warning))
        {
            yield return SoundCue.WarningLine;
        }
        if (Breakpoints.IsMarked(line))
        {
            yield return SoundCue.BreakpointLine;
        }
        if (Editor.FoldRegions.Any(f => f.Folded && f.Start >= span.Start && f.Start <= span.End))
        {
            yield return SoundCue.FoldedLine;
        }
        if (ChangeOn(line) is { } change && ChangeOn(from) != change)
        {
            yield return SoundCue.ChangedLine;
        }
    }

    /// <summary>The change bar on a line, where one is.</summary>
    private ChangeMark? ChangeOn(int line)
    {
        foreach (ChangeMark change in Editor.ChangeMarks)
        {
            if (change.Kind == ChangeMarkKind.Deleted ? change.Line == line : line >= change.Line && line < change.Line + Math.Max(1, change.Count))
            {
                return change;
            }
        }
        return null;
    }
}
