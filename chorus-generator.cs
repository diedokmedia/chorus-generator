﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using ScriptPortal.Vegas;

/// <summary>
/// Generates and inserts copies of selected VEGAS media fragments based on custom patterns.
/// </summary>
/// <remarks>
/// This class serves as the main entry point for the VEGAS Pro script. It handles 
/// fragment selection, parses regex-like custom pattern notation, calculates timing 
/// based on the project's BPM, and inserts the generated media sequence.
/// </remarks>
public class EntryPoint
{
    /// <summary>
    /// Default BPM used when the project ruler does not specify a valid BPM.
    /// </summary>
    private const double DEFAULT_BPM = 120.0;

    /// <summary>
    /// Number of beats in a standard 4/4 bar.
    /// </summary>
    private const int BEATS_PER_BAR = 4;

    /// <summary>
    /// Number of base grid steps in one bar (16th notes).
    /// </summary>
    private const int BASE_GRID_DIVISIONS = 16;

    /// <summary>
    /// Maximum number of media fragments supported for pattern generation.
    /// </summary>
    private const int MAX_FRAGMENTS = 35;

    /// <summary>
    /// Maximum allowed repetition count for a single regex-like quantifier ({n}).
    /// </summary>
    private const int MAX_REPETITION = 10000;

    /// <summary>
    /// Maximum allowed length of the expanded pattern string to prevent memory issues.
    /// </summary>
    private const int MAX_EXPANDED_LENGTH = 1000000;

    /// <summary>
    /// Main entry point called by VEGAS Pro.
    /// </summary>
    /// <param name="vegas">The active VEGAS application instance.</param>
    public void FromVegas(Vegas vegas)
    {
        try
        {
            List<MediaSegment> segments = FindSelectedMediaSegments(vegas);

            if (segments.Count == 0)
            {
                MessageBox.Show(
                    "Select one or more audio or video fragments to use as a source.",
                    "Chorus Generator",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (segments.Count > MAX_FRAGMENTS)
            {
                MessageBox.Show(
                    "Select no more than " + MAX_FRAGMENTS + " media fragments.",
                    "Chorus Generator",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            string pattern;
            bool insertAtCursor;

            using (PatternSelectorForm selector = new PatternSelectorForm())
            {
                if (!selector.TryGetSettings(out pattern, out insertAtCursor))
                    return;
            }

            string expandedPattern;

            try
            {
                expandedPattern = PreparePattern(pattern, segments.Count);
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(
                    ex.Message,
                    "Chorus Generator — Invalid Pattern",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            double bpm = GetProjectBpm(vegas);
            Timecode baseStep = CalculateBaseGridStep(bpm);

            Timecode startPosition = insertAtCursor 
                ? vegas.Transport.CursorPosition 
                : segments[0].Start;

            using (UndoBlock undo = new UndoBlock("Insert media pattern " + pattern))
            {
                InsertPattern(segments, baseStep, expandedPattern, startPosition);

                if (!insertAtCursor)
                {
                    RemoveSourceEvents(vegas, segments);
                }
                else
                {
                    foreach (MediaSegment segment in segments)
                    {
                        foreach (TrackEvent trackEvent in segment.Events)
                        {
                            trackEvent.Selected = false;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // User canceled the pattern or BPM dialog.
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Chorus Generator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Finds selected media fragments and expands each fragment to include overlapping audio and video events.
    /// </summary>
    /// <param name="vegas">The active VEGAS application instance.</param>
    /// <returns>A list of detected media segments sorted chronologically.</returns>
    private List<MediaSegment> FindSelectedMediaSegments(Vegas vegas)
    {
        List<TrackEvent> selectedEvents = new List<TrackEvent>();

        foreach (Track track in vegas.Project.Tracks)
        {
            foreach (TrackEvent trackEvent in track.Events)
            {
                if (trackEvent.Selected && (trackEvent.IsAudio() || trackEvent.IsVideo()))
                {
                    selectedEvents.Add(trackEvent);
                }
            }
        }

        if (selectedEvents.Count == 0)
            return new List<MediaSegment>();

        selectedEvents.Sort(delegate(TrackEvent first, TrackEvent second)
        {
            int position = first.Start.Nanos.CompareTo(second.Start.Nanos);
            return position != 0 ? position : first.Track.Index.CompareTo(second.Track.Index);
        });

        List<MediaSegment> segments = new List<MediaSegment>();

        foreach (TrackEvent selectedEvent in selectedEvents)
        {
            MediaSegment segment = null;

            foreach (MediaSegment existing in segments)
            {
                if (selectedEvent.Start.Nanos < existing.End.Nanos &&
                    selectedEvent.End.Nanos > existing.Start.Nanos)
                {
                    segment = existing;
                    break;
                }
            }

            if (segment == null)
            {
                segment = new MediaSegment
                {
                    Start = selectedEvent.Start,
                    End = selectedEvent.End
                };
                segments.Add(segment);
            }

            if (selectedEvent.Start.Nanos < segment.Start.Nanos)
                segment.Start = selectedEvent.Start;

            if (selectedEvent.End.Nanos > segment.End.Nanos)
                segment.End = selectedEvent.End;
        }

        segments.Sort(delegate(MediaSegment first, MediaSegment second)
        {
            return first.Start.Nanos.CompareTo(second.Start.Nanos);
        });

        foreach (MediaSegment segment in segments)
        {
            AddOverlappingEvents(vegas, segment);
        }

        return segments;
    }

    /// <summary>
    /// Populates the specified segment with all audio and video events that overlap its time range.
    /// </summary>
    /// <param name="vegas">The active VEGAS application instance.</param>
    /// <param name="segment">The media segment to expand.</param>
    private void AddOverlappingEvents(Vegas vegas, MediaSegment segment)
    {
        segment.Events.Clear();

        foreach (Track track in vegas.Project.Tracks)
        {
            foreach (TrackEvent trackEvent in track.Events)
            {
                if (!trackEvent.IsAudio() && !trackEvent.IsVideo())
                    continue;

                if (trackEvent.Start.Nanos < segment.End.Nanos &&
                    trackEvent.End.Nanos > segment.Start.Nanos)
                {
                    if (!segment.Events.Contains(trackEvent))
                        segment.Events.Add(trackEvent);
                }
            }
        }

        segment.Events.Sort(delegate(TrackEvent first, TrackEvent second)
        {
            int position = first.Start.Nanos.CompareTo(second.Start.Nanos);
            return position != 0 ? position : first.Track.Index.CompareTo(second.Track.Index);
        });
    }

    /// <summary>
    /// Retrieves the Beats Per Minute (BPM) from the VEGAS project ruler.
    /// </summary>
    /// <param name="vegas">The active VEGAS application instance.</param>
    /// <returns>The project BPM, or the default BPM if the project value is invalid.</returns>
    private double GetProjectBpm(Vegas vegas)
    {
        double bpm = vegas.Project.Ruler.BeatsPerMinute;
        return bpm > 0 ? bpm : DEFAULT_BPM;
    }

    /// <summary>
    /// Calculates the duration of one base grid step (a 16th note) based on the provided BPM.
    /// </summary>
    /// <param name="bpm">The tempo in beats per minute.</param>
    /// <returns>A Timecode representing the duration of one base step.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the BPM is less than or equal to zero.</exception>
    private Timecode CalculateBaseGridStep(double bpm)
    {
        if (bpm <= 0) throw new InvalidOperationException("BPM must be greater than zero.");
        double quarterSeconds = 60.0 / bpm;
        double barSeconds = quarterSeconds * BEATS_PER_BAR;
        double stepSeconds = barSeconds / BASE_GRID_DIVISIONS;
        return Timecode.FromSeconds(stepSeconds);
    }

    /// <summary>
    /// Expands regex-like constructs and validates the resulting pattern string.
    /// </summary>
    /// <param name="rawPattern">The raw pattern string provided by the user.</param>
    /// <param name="fragmentCount">The number of detected fragments available for referencing.</param>
    /// <returns>The validated, flat expanded pattern string.</returns>
    /// <exception cref="ArgumentException">Thrown when the pattern is empty, malformed, or contains out-of-bounds fragment references.</exception>
    private string PreparePattern(string rawPattern, int fragmentCount)
    {
        if (String.IsNullOrEmpty(rawPattern))
            throw new ArgumentException("The pattern cannot be empty.");

        string expanded = ExpandRegex(rawPattern);
        ValidateExpandedPattern(expanded, fragmentCount);
        return expanded;
    }

    /// <summary>
    /// Recursively parses and expands regex-like deterministic constructs into a flat pattern string.
    /// </summary>
    /// <param name="pattern">The raw pattern string.</param>
    /// <returns>The fully expanded pattern string.</returns>
    /// <exception cref="ArgumentException">Thrown when unexpected characters remain after parsing.</exception>
    private string ExpandRegex(string pattern)
    {
        int position = 0;
        string result = ParseSequence(pattern, ref position, false);

        if (position != pattern.Length)
            throw new ArgumentException("Unexpected characters after position " + position + ".");

        return result;
    }

    /// <summary>
    /// Parses a sequence of atoms, groups, and repetitions.
    /// </summary>
    /// <param name="pattern">The raw pattern string.</param>
    /// <param name="position">The current index in the string. Updated as parsing progresses.</param>
    /// <param name="insideGroup">True if parsing occurs inside a parenthesized group.</param>
    /// <returns>The expanded string for the current sequence.</returns>
    /// <exception cref="ArgumentException">Thrown when encountering syntax errors like mismatched brackets or invalid modifiers.</exception>
    private string ParseSequence(string pattern, ref int position, bool insideGroup)
    {
        StringBuilder result = new StringBuilder();

        while (position < pattern.Length)
        {
            char symbol = pattern[position];

            if (symbol == ')')
            {
                if (!insideGroup)
                    throw new ArgumentException("Unmatched ')' at position " + position + ".");
                return result.ToString();
            }

            string atom;
            bool atomFromGroup = false;

            if (symbol == '(')
            {
                position++;
                string inner = ParseSequence(pattern, ref position, true);
                if (position >= pattern.Length || pattern[position] != ')')
                    throw new ArgumentException("Missing ')' at end of pattern.");
                position++;
                atom = inner;
                atomFromGroup = true;
            }
            else if (symbol == '{')
            {
                throw new ArgumentException("'{' at position " + position + " must follow an atom.");
            }
            else if (symbol == '}')
            {
                throw new ArgumentException("Unmatched '}' at position " + position + ".");
            }
            else if (symbol == '*' || symbol == '/')
            {
                throw new ArgumentException("Duration modifier '" + symbol + "' at position " + position + " must directly follow a fragment.");
            }
            else if (IsAtomChar(symbol))
            {
                StringBuilder atomBuilder = new StringBuilder();
                atomBuilder.Append(symbol);
                position++;

                while (position < pattern.Length && (pattern[position] == '*' || pattern[position] == '/'))
                {
                    atomBuilder.Append(pattern[position]);
                    position++;
                }
                atom = atomBuilder.ToString();
            }
            else if (char.IsWhiteSpace(symbol))
            {
                position++;
                continue;
            }
            else
            {
                throw new ArgumentException("Invalid character '" + symbol + "' at position " + position + ".");
            }

            bool hadRepetition = false;

            while (position < pattern.Length && pattern[position] == '{')
            {
                int count = ParseRepetitionCount(pattern, ref position);
                atom = RepeatString(atom, count);
                hadRepetition = true;
            }

            result.Append(atom);

            if (position < pattern.Length && (pattern[position] == '*' || pattern[position] == '/'))
            {
                if (hadRepetition)
                    throw new ArgumentException("Duration modifier '" + pattern[position] + "' cannot follow a repetition.");
                if (atomFromGroup)
                    throw new ArgumentException("Duration modifier '" + pattern[position] + "' cannot follow a group. Put it inside.");
            }
        }

        if (insideGroup)
            throw new ArgumentException("Missing ')' at end of pattern.");

        return result.ToString();
    }

    /// <summary>
    /// Parses a repetition count inside curly braces (e.g., {5}).
    /// </summary>
    /// <param name="pattern">The raw pattern string.</param>
    /// <param name="position">The current index in the string, starting at the opening brace.</param>
    /// <returns>The parsed integer repetition count.</returns>
    /// <exception cref="ArgumentException">Thrown for missing counts, invalid characters, non-deterministic ranges ({n,m}), or missing closing braces.</exception>
    private int ParseRepetitionCount(string pattern, ref int position)
    {
        int braceStart = position;
        position++;
        int start = position;

        while (position < pattern.Length && pattern[position] >= '0' && pattern[position] <= '9')
            position++;

        if (start == position)
            throw new ArgumentException("Missing repetition count after '{' at position " + braceStart + ".");

        string countText = pattern.Substring(start, position - start);
        int count;

        if (!int.TryParse(countText, out count))
            throw new ArgumentException("Invalid repetition count '" + countText + "'.");

        if (count <= 0) throw new ArgumentException("Repetition count must be positive.");
        if (count > MAX_REPETITION) throw new ArgumentException("Repetition count exceeds max limit.");

        if (position >= pattern.Length || pattern[position] != '}')
        {
            if (position < pattern.Length && pattern[position] == ',')
                throw new ArgumentException("Non-deterministic '{n,m}' is not supported.");
            throw new ArgumentException("Missing '}' for repetition starting at " + braceStart + ".");
        }

        position++;
        return count;
    }

    /// <summary>
    /// Repeats a string a specified number of times, enforcing a maximum size limit.
    /// </summary>
    /// <param name="value">The string to repeat.</param>
    /// <param name="count">The number of times to repeat the string.</param>
    /// <returns>The repeated string.</returns>
    /// <exception cref="ArgumentException">Thrown when the resulting string would exceed the maximum allowed length.</exception>
    private string RepeatString(string value, int count)
    {
        long totalLength = (long)value.Length * count;
        if (totalLength > MAX_EXPANDED_LENGTH) throw new ArgumentException("Pattern is too large.");

        if (count == 0 || value.Length == 0) return String.Empty;
        if (count == 1) return value;

        StringBuilder builder = new StringBuilder((int)totalLength);
        for (int i = 0; i < count; i++) builder.Append(value);
        return builder.ToString();
    }

    /// <summary>
    /// Determines whether the character can be used as a fragment reference or skip symbol.
    /// </summary>
    /// <param name="symbol">The character to test.</param>
    /// <returns>True if the character is valid for an atom.</returns>
    private bool IsAtomChar(char symbol)
    {
        return symbol == '_' || (symbol >= '0' && symbol <= '9') ||
               (symbol >= 'A' && symbol <= 'Z') || (symbol >= 'a' && symbol <= 'z');
    }

    /// <summary>
    /// Determines whether the character represents a skip/rest step.
    /// </summary>
    /// <param name="symbol">The character to test.</param>
    /// <returns>True if the character is a skip symbol.</returns>
    private bool IsSkip(char symbol)
    {
        return symbol == '_' || symbol == '0';
    }

    /// <summary>
    /// Converts a fragment character into a 1-based fragment index.
    /// </summary>
    /// <param name="symbol">The fragment character (1-9, A-Z, a-z).</param>
    /// <returns>The 1-based fragment index, or -1 if the character is not a fragment reference.</returns>
    private int GetFragmentIndex(char symbol)
    {
        if (symbol >= '1' && symbol <= '9') return symbol - '0';
        if (symbol >= 'A' && symbol <= 'Z') return symbol - 'A' + 10;
        if (symbol >= 'a' && symbol <= 'z') return symbol - 'a' + 10;
        return -1;
    }

    /// <summary>
    /// Converts a 1-based fragment index back to its character representation.
    /// </summary>
    /// <param name="fragmentIndex">The 1-based fragment index.</param>
    /// <returns>The character representing the fragment.</returns>
    private string GetFragmentName(int fragmentIndex)
    {
        if (fragmentIndex >= 1 && fragmentIndex <= 9) return fragmentIndex.ToString();
        if (fragmentIndex >= 10 && fragmentIndex <= 35) return ((char)('A' + fragmentIndex - 10)).ToString();
        return "?";
    }

    /// <summary>
    /// Validates the expanded pattern string to ensure all fragment references are valid and within bounds.
    /// </summary>
    /// <param name="expanded">The fully expanded pattern string.</param>
    /// <param name="fragmentCount">The number of available fragments.</param>
    /// <exception cref="ArgumentException">Thrown if an invalid symbol is found or a fragment reference is out of bounds.</exception>
    private void ValidateExpandedPattern(string expanded, int fragmentCount)
    {
        int index = 0;
        while (index < expanded.Length)
        {
            char symbol = expanded[index];
            bool isSkip = IsSkip(symbol);
            int fragmentIndex = GetFragmentIndex(symbol);

            if (!isSkip && fragmentIndex < 0)
                throw new ArgumentException("Invalid symbol '" + symbol + "' at position " + index + ".");

            if (fragmentIndex > 0 && fragmentIndex > fragmentCount)
                throw new ArgumentException("Pattern references fragment " + GetFragmentName(fragmentIndex) + ", but only " + fragmentCount + " are available.");

            index++;
            while (index < expanded.Length && (expanded[index] == '*' || expanded[index] == '/'))
                index++;
        }
    }

    /// <summary>
    /// Calculates the duration multiplier based on the number of asterisk (*) and slash (/) modifiers.
    /// </summary>
    /// <param name="asteriskCount">The number of '*' modifiers (doubling).</param>
    /// <param name="slashCount">The number of '/' modifiers (halving).</param>
    /// <returns>The calculated duration multiplier.</returns>
    private double CalculateMultiplier(int asteriskCount, int slashCount)
    {
        return Math.Pow(2.0, asteriskCount - slashCount);
    }

    /// <summary>
    /// Iterates through the expanded pattern and inserts the generated media segments at the specified position.
    /// </summary>
    /// <param name="segments">The list of source media fragments.</param>
    /// <param name="baseStep">The duration of one base grid step.</param>
    /// <param name="expandedPattern">The fully expanded pattern string.</param>
    /// <param name="startPosition">The starting timecode for the generated sequence.</param>
    /// <exception cref="InvalidOperationException">Thrown if a calculated duration is invalid or a fragment reference is out of range.</exception>
    private void InsertPattern(List<MediaSegment> segments, Timecode baseStep, string expandedPattern, Timecode startPosition)
    {
        long cursor = startPosition.Nanos;
        int index = 0;

        while (index < expandedPattern.Length)
        {
            char symbol = expandedPattern[index];
            index++;

            int asteriskCount = 0;
            int slashCount = 0;

            while (index < expandedPattern.Length && (expandedPattern[index] == '*' || expandedPattern[index] == '/'))
            {
                if (expandedPattern[index] == '*') asteriskCount++;
                else slashCount++;
                index++;
            }

            double multiplier = CalculateMultiplier(asteriskCount, slashCount);
            long duration = (long)Math.Round(baseStep.Nanos * multiplier);

            if (duration <= 0) throw new InvalidOperationException("Calculated duration is invalid.");

            if (IsSkip(symbol))
            {
                cursor += duration;
                continue;
            }

            int fragmentIndex = GetFragmentIndex(symbol);
            if (fragmentIndex <= 0 || fragmentIndex > segments.Count)
                throw new InvalidOperationException("Fragment reference out of range.");

            MediaSegment sourceSegment = segments[fragmentIndex - 1];
            CopyMediaSegment(sourceSegment, Timecode.FromNanos(cursor), multiplier);
            cursor += duration;
        }
    }

    /// <summary>
    /// Copies all audio and video events belonging to a source media segment to a new destination, applying time scaling.
    /// </summary>
    /// <param name="sourceSegment">The source media fragment.</param>
    /// <param name="destinationStart">The starting timecode for the copy.</param>
    /// <param name="multiplier">The relative duration and position multiplier.</param>
    private void CopyMediaSegment(MediaSegment sourceSegment, Timecode destinationStart, double multiplier)
    {
        foreach (TrackEvent sourceEvent in sourceSegment.Events)
        {
            long relativeOffset = sourceEvent.Start.Nanos - sourceSegment.Start.Nanos;
            if (relativeOffset < 0) relativeOffset = 0;

            long adjustedOffset = (long)Math.Round(relativeOffset * multiplier);
            Timecode copyStart = Timecode.FromNanos(destinationStart.Nanos + adjustedOffset);

            TrackEvent copy = sourceEvent.Copy(sourceEvent.Track, copyStart);
            copy.Start = copyStart;
            copy.Length = Timecode.FromNanos((long)Math.Round(sourceEvent.Length.Nanos * multiplier));

            SetCopyTakeOffset(sourceEvent, copy, sourceSegment.Start, multiplier);
            copy.Selected = true;
        }
    }

    /// <summary>
    /// Adjusts the source offset of a copied media event to match its relative position within the resized segment.
    /// </summary>
    /// <param name="sourceEvent">The original track event.</param>
    /// <param name="copy">The newly created track event copy.</param>
    /// <param name="sourceStart">The starting timecode of the source segment.</param>
    /// <param name="multiplier">The relative offset multiplier.</param>
    private void SetCopyTakeOffset(TrackEvent sourceEvent, TrackEvent copy, Timecode sourceStart, double multiplier)
    {
        Take sourceTake = sourceEvent.ActiveTake;
        Take copyTake = copy.ActiveTake;

        if (sourceTake == null || copyTake == null) return;

        long relativeOffset = sourceEvent.Start.Nanos - sourceStart.Nanos;
        if (relativeOffset < 0) relativeOffset = 0;

        copyTake.Offset = Timecode.FromNanos(sourceTake.Offset.Nanos + (long)Math.Round(relativeOffset * multiplier));
    }

    /// <summary>
    /// Removes the original source events from the timeline after the generation process is complete.
    /// </summary>
    /// <param name="vegas">The active VEGAS application instance.</param>
    /// <param name="segments">The source media fragments to remove.</param>
    private void RemoveSourceEvents(Vegas vegas, List<MediaSegment> segments)
    {
        List<TrackEvent> eventsToRemove = new List<TrackEvent>();

        foreach (MediaSegment segment in segments)
        {
            foreach (TrackEvent trackEvent in segment.Events)
            {
                if (!eventsToRemove.Contains(trackEvent))
                    eventsToRemove.Add(trackEvent);
            }
        }

        foreach (TrackEvent trackEvent in eventsToRemove)
        {
            if (trackEvent.Track != null && trackEvent.Track.Events.Contains(trackEvent))
                trackEvent.Track.Events.Remove(trackEvent);
        }

        foreach (Track track in vegas.Project.Tracks)
        {
            foreach (TrackEvent trackEvent in track.Events)
                trackEvent.Selected = false;
        }
    }
}

/// <summary>
/// Represents a distinct media fragment on the timeline, including all overlapping events.
/// </summary>
public class MediaSegment
{
    /// <summary>
    /// The starting timecode of the media fragment.
    /// </summary>
    public Timecode Start;

    /// <summary>
    /// The ending timecode of the media fragment.
    /// </summary>
    public Timecode End;

    /// <summary>
    /// A list of all audio and video track events that overlap this fragment.
    /// </summary>
    public List<TrackEvent> Events = new List<TrackEvent>();
}

/// <summary>
/// Represents a built-in pattern with a display name and its corresponding expression string.
/// </summary>
public class PatternDefinition
{
    /// <summary>
    /// Gets the user-friendly display name of the pattern.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    /// Gets the actual pattern expression string.
    /// </summary>
    public string Value { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PatternDefinition"/> class.
    /// </summary>
    /// <param name="name">The display name of the pattern.</param>
    /// <param name="value">The pattern expression string.</param>
    public PatternDefinition(string name, string value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>
    /// Returns the display name of the pattern.
    /// </summary>
    /// <returns>The pattern display name.</returns>
    public override string ToString() { return Name; }
}

/// <summary>
/// Provides a collection of built-in pattern definitions.
/// </summary>
public static class PatternLibrary
{
    /// <summary>
    /// Retrieves the list of all built-in patterns available in the script.
    /// </summary>
    /// <returns>A list of <see cref="PatternDefinition"/> objects.</returns>
    public static List<PatternDefinition> GetPatterns()
    {
        return new List<PatternDefinition>
        {
            new PatternDefinition("Alternate", "1_2_"),
            new PatternDefinition("Consecutive", "12"),
            new PatternDefinition("Four fragments", "1234"),
            new PatternDefinition("Four fragments with skips", "1_2_3_4_"),
            new PatternDefinition("Regex demo: (12){4}", "(12){4}"),
            new PatternDefinition("Regex demo: 1{8}", "1{8}"),
            new PatternDefinition("Regex demo: (1_2_){2}", "(1_2_){2}"),
            new PatternDefinition("Regex demo: (12*){2}", "(12*){2}"),
            new PatternDefinition("Sparta Remix Chorus", "11_11_111_1_1_11222_2_222_222_2_"),
            new PatternDefinition("Sparta Remix Epicness main part", "1*__1*223*1_1_11__1_1*112_2_33331*2_1*223*1_1_111_11111111111111"),
            new PatternDefinition("Sparta Remix Epicness second part", "2_2_____2__2_2222_22"),
            new PatternDefinition("Sparta Remix Chorus (32)", "11_11_111_1_1_112/2/2/2/2/2/_2/2/_2/2/2/2/2/2/_2/2/2/2/2/2/_2/2/_"),
            new PatternDefinition("Sparta Remix madness", "1*______________2*______________1*______________2*______________1*______2*______1*______2*______1*__2*__1*__2*__1*__1*__1*__1111"),
            new PatternDefinition("KingSpartaX37 madness (no pitching)", "1/1/1/1/1/1/1/1/2/_/2/_/2/_/2/_/3/3/3/3/3/3/3/3/3/_/3/_/3/3/3/3/4/_/4/_/4/4/4/_/4/_/4/_/4/4/4/_/5**6/6/6/6/6/6/6/6/1/1/1/1/1/1/1/1/2/_/2/_/2/_/2/_/3/3/3/3/3/3/3/3/3/_/3/_/3/3/3/3/4/_/4/_/4/4/4/_/4/_/4/_/4/4/4/_/5**6/6/6/6/6/6/6/6/1*1*11112*2*22221*1*11112*2*22221*112*221*112*221122112211221111")
        };
    }
}

/// <summary>
/// Dialog for selecting a pattern and specifying the generation location.
/// </summary>
public class PatternSelectorForm : Form
{
    /// <summary>The radio button for choosing a built-in pattern.</summary>
    private RadioButton presetRadioButton;

    /// <summary>The radio button for entering a custom pattern.</summary>
    private RadioButton customRadioButton;

    /// <summary>The combo box displaying available built-in patterns.</summary>
    private ComboBox presetComboBox;

    /// <summary>The text box for typing or displaying a pattern expression.</summary>
    private TextBox patternTextBox;

    /// <summary>The combo box for selecting where to generate the pattern.</summary>
    private ComboBox generateLocationComboBox;

    /// <summary>The button to confirm and insert the pattern.</summary>
    private Button insertButton;

    /// <summary>The button to cancel the operation.</summary>
    private Button cancelButton;

    /// <summary>The button to open the help documentation.</summary>
    private Button helpButton;

    /// <summary>
    /// Initializes a new instance of the <see cref="PatternSelectorForm"/> class.
    /// </summary>
    public PatternSelectorForm()
    {
        Text = "Chorus Generator";
        Width = 600;
        Height = 260; 
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        presetRadioButton = new RadioButton
        {
            Text = "Built-in pattern",
            Left = 15,
            Top = 15,
            Width = 180,
            Checked = true
        };

        customRadioButton = new RadioButton
        {
            Text = "Enter custom pattern",
            Left = 15,
            Top = 50,
            Width = 180
        };

        presetComboBox = new ComboBox
        {
            Left = 200,
            Top = 12,
            Width = 360,
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        foreach (PatternDefinition pattern in PatternLibrary.GetPatterns())
            presetComboBox.Items.Add(pattern);

        if (presetComboBox.Items.Count > 0)
            presetComboBox.SelectedIndex = 0;

        patternTextBox = new TextBox
        {
            Left = 200,
            Top = 48,
            Width = 360,
            Enabled = false
        };

        Label hintLabel = new Label
        {
            Text = "0-9, A-Z: fragment (1-35);  _ or 0: skip;  *: x2;  /: x0.5;  (...){n}: repeat group",
            Left = 200,
            Top = 76,
            Width = 380,
            Height = 30
        };

        Label locationLabel = new Label
        {
            Text = "Generate at:",
            Left = 15,
            Top = 100,
            Width = 180
        };

        generateLocationComboBox = new ComboBox
        {
            Left = 200,
            Top = 97,
            Width = 360,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        
        generateLocationComboBox.Items.Add("Replace selected track event(s)");
        generateLocationComboBox.Items.Add("At cursor position (keep sources)");
        generateLocationComboBox.SelectedIndex = 0;

        helpButton = new Button
        {
            Text = "Help",
            Left = 15,
            Top = 170,
            Width = 95
        };
        helpButton.Click += HelpButton_Click;

        insertButton = new Button
        {
            Text = "Insert",
            Left = 360,
            Top = 170,
            Width = 95,
            DialogResult = DialogResult.OK
        };

        cancelButton = new Button
        {
            Text = "Cancel",
            Left = 465,
            Top = 170,
            Width = 95,
            DialogResult = DialogResult.Cancel
        };

        presetRadioButton.CheckedChanged += PresetRadioButton_CheckedChanged;
        customRadioButton.CheckedChanged += CustomRadioButton_CheckedChanged;
        presetComboBox.SelectedIndexChanged += PresetComboBox_SelectedIndexChanged;

        Controls.Add(presetRadioButton);
        Controls.Add(customRadioButton);
        Controls.Add(presetComboBox);
        Controls.Add(patternTextBox);
        Controls.Add(hintLabel);
        Controls.Add(locationLabel);
        Controls.Add(generateLocationComboBox);
        Controls.Add(helpButton);
        Controls.Add(insertButton);
        Controls.Add(cancelButton);

        AcceptButton = insertButton;
        CancelButton = cancelButton;

        PresetComboBox_SelectedIndexChanged(this, EventArgs.Empty);
    }

    /// <summary>
    /// Handles the check state change for the built-in pattern mode.
    /// </summary>
    /// <param name="sender">The event source.</param>
    /// <param name="e">The event arguments.</param>
    private void PresetRadioButton_CheckedChanged(object sender, EventArgs e)
    {
        presetComboBox.Enabled = presetRadioButton.Checked;
        patternTextBox.Enabled = customRadioButton.Checked;

        if (presetRadioButton.Checked)
            PresetComboBox_SelectedIndexChanged(sender, e);
    }

    /// <summary>
    /// Handles the check state change for the custom pattern mode.
    /// </summary>
    /// <param name="sender">The event source.</param>
    /// <param name="e">The event arguments.</param>
    private void CustomRadioButton_CheckedChanged(object sender, EventArgs e)
    {
        patternTextBox.Enabled = customRadioButton.Checked;

        if (customRadioButton.Checked)
        {
            patternTextBox.Focus();
            patternTextBox.SelectAll();
        }
    }

    /// <summary>
    /// Updates the pattern text box when a different built-in pattern is selected.
    /// </summary>
    /// <param name="sender">The event source.</param>
    /// <param name="e">The event arguments.</param>
    private void PresetComboBox_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (!presetRadioButton.Checked) return;

        PatternDefinition selected = presetComboBox.SelectedItem as PatternDefinition;
        if (selected != null)
            patternTextBox.Text = selected.Value;
    }

    /// <summary>
    /// Displays the dialog and retrieves the user-selected settings.
    /// </summary>
    /// <param name="pattern">Outputs the chosen pattern expression string.</param>
    /// <param name="insertAtCursor">Outputs a value indicating whether to generate at the cursor position rather than replacing the source.</param>
    /// <returns>True if the user confirmed valid settings; otherwise, false.</returns>
    public bool TryGetSettings(out string pattern, out bool insertAtCursor)
    {
        pattern = null;
        insertAtCursor = false;

        if (ShowDialog() != DialogResult.OK)
            return false;

        if (customRadioButton.Checked)
        {
            pattern = patternTextBox.Text.Trim();
        }
        else
        {
            PatternDefinition selected = presetComboBox.SelectedItem as PatternDefinition;
            if (selected == null) return false;
            pattern = selected.Value.Trim();
        }

        insertAtCursor = (generateLocationComboBox.SelectedIndex == 1);

        return !String.IsNullOrEmpty(pattern);
    }

    /// <summary>
    /// Opens the pattern help dialog.
    /// </summary>
    /// <param name="sender">The event source.</param>
    /// <param name="e">The event arguments.</param>
    private void HelpButton_Click(object sender, EventArgs e)
    {
        using (PatternHelpForm helpForm = new PatternHelpForm())
        {
            helpForm.ShowDialog(this);
        }
    }
}

/// <summary>
/// Dialog for manually inputting a Beats Per Minute (BPM) value.
/// </summary>
public class BpmInputForm : Form
{
    /// <summary>
    /// Gets the BPM value entered by the user.
    /// </summary>
    public double Bpm { get; private set; }

    /// <summary>
    /// The numeric input control for the BPM.
    /// </summary>
    private NumericUpDown bpmInput;

    /// <summary>
    /// Initializes a new instance of the <see cref="BpmInputForm"/> class.
    /// </summary>
    /// <param name="defaultBpm">The initial BPM value to display.</param>
    public BpmInputForm(double defaultBpm)
    {
        Text = "Enter BPM";
        Width = 320;
        Height = 165;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        Label label = new Label { Text = "BPM:", Left = 15, Top = 18, Width = 50 };

        bpmInput = new NumericUpDown
        {
            Left = 70, Top = 15, Width = 210, Minimum = 1, Maximum = 999,
            DecimalPlaces = 2, Increment = 1, Value = Convert.ToDecimal(defaultBpm)
        };

        Button okButton = new Button { Text = "Continue", Left = 110, Top = 60, Width = 80, DialogResult = DialogResult.OK };
        Button cancelButton = new Button { Text = "Cancel", Left = 200, Top = 60, Width = 80, DialogResult = DialogResult.Cancel };

        okButton.Click += OkButton_Click;

        Controls.Add(label);
        Controls.Add(bpmInput);
        Controls.Add(okButton);
        Controls.Add(cancelButton);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    /// <summary>
    /// Stores the entered BPM value when the user confirms.
    /// </summary>
    /// <param name="sender">The event source.</param>
    /// <param name="e">The event arguments.</param>
    private void OkButton_Click(object sender, EventArgs e)
    {
        Bpm = Convert.ToDouble(bpmInput.Value);
    }
}

/// <summary>
/// Dialog displaying comprehensive documentation for the pattern notation system.
/// </summary>
public class PatternHelpForm : Form
{
    /// <summary>The read-only text box containing the help documentation.</summary>
    private TextBox helpTextBox;

    /// <summary>The button to close the help dialog.</summary>
    private Button closeButton;

    /// <summary>
    /// Initializes a new instance of the <see cref="PatternHelpForm"/> class.
    /// </summary>
    public PatternHelpForm()
    {
        Text = "Pattern Help";
        Width = 780;
        Height = 640;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;

        helpTextBox = new TextBox
        {
            Left = 10, Top = 10, Width = 740, Height = 530,
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            WordWrap = false, Font = new System.Drawing.Font("Consolas", 10.0f),
            Text = 
                "CHORUS GENERATOR PATTERN NOTATION\r\n" +
                "\r\n" +
                "A pattern describes how selected media fragments are copied.\r\n" +
                "\r\n" +
                "FRAGMENT REFERENCES\r\n" +
                "  1-9   -> fragments 1 through 9.\r\n" +
                "  A-Z   -> fragments 10 through 35.\r\n" +
                "  a-z   -> alias for A-Z (case-insensitive).\r\n" +
                "\r\n" +
                "SKIP SYMBOLS\r\n" +
                "  _     -> skip one base grid step.\r\n" +
                "  0     -> same as '_' (alternate rest symbol).\r\n" +
                "\r\n" +
                "DURATION MODIFIERS\r\n" +
                "  *     -> double the duration of the preceding fragment.\r\n" +
                "  /     -> halve the duration of the preceding fragment.\r\n" +
                "\r\n" +
                "REGEX-STYLE EXPANSION (DETERMINISTIC SUBSET)\r\n" +
                "  (...)       -> grouping. Used together with {n}.\r\n" +
                "  {n}         -> repeat the preceding atom exactly n times.\r\n" +
                "\r\n" +
                "Examples:\r\n" +
                "  (12){4}         -> 12121212\r\n" +
                "  1{8}            -> 11111111\r\n" +
                "  (1_2_){2}       -> 1_2_1_2_\r\n" +
                "  (12*){2}        -> 12*12*\r\n" +
                "\r\n" +
                "GENERATION LOCATION\r\n" +
                "\r\n" +
                "You can choose where the generated sequence is placed:\r\n" +
                "1. Replace selected track event(s) - removes the source media\r\n" +
                "   and builds the pattern starting exactly at the source's location.\r\n" +
                "2. At cursor position - leaves the source media intact and generates\r\n" +
                "   the pattern starting from the current timeline cursor."
        };

        closeButton = new Button
        {
            Text = "Close", Left = 655, Top = 555, Width = 95, DialogResult = DialogResult.OK
        };

        Controls.Add(helpTextBox);
        Controls.Add(closeButton);

        AcceptButton = closeButton;
        CancelButton = closeButton;
    }
}