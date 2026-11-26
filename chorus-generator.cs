﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using ScriptPortal.Vegas;

/// <summary>
/// Generates and inserts copies of selected VEGAS media fragments.
/// </summary>
public class EntryPoint
{
    /// <summary>
    /// Default BPM used when no BPM marker or user value is available.
    /// </summary>
    private const double DEFAULT_BPM = 120.0;

    /// <summary>
    /// Number of beats in a 4/4 bar.
    /// </summary>
    private const int BEATS_PER_BAR = 4;

    /// <summary>
    /// Number of base grid steps in one bar.
    /// </summary>
    private const int BASE_GRID_DIVISIONS = 16;

    /// <summary>
    /// Maximum number of fragments supported (0-9 = skip/1-9; A-Z = 10-35).
    /// </summary>
    private const int MAX_FRAGMENTS = 35;

    /// <summary>
    /// Maximum allowed repetition count for a single <c>{n}</c> construct.
    /// </summary>
    private const int MAX_REPETITION = 10000;

    /// <summary>
    /// Maximum allowed length of the expanded pattern.
    /// </summary>
    private const int MAX_EXPANDED_LENGTH = 1000000;

    /// <summary>
    /// Main entry point called by VEGAS.
    /// </summary>
    /// <param name="vegas">
    /// Active VEGAS application.
    /// </param>
    public void FromVegas(Vegas vegas)
    {
        try
        {
            List<MediaSegment> segments =
                FindSelectedMediaSegments(vegas);

            if (segments.Count == 0)
            {
                MessageBox.Show(
                    "Select one or more audio or video fragments.",
                    "Chorus Generator",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return;
            }

            if (segments.Count > MAX_FRAGMENTS)
            {
                MessageBox.Show(
                    "Select no more than " +
                    MAX_FRAGMENTS +
                    " media fragments.",
                    "Chorus Generator",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            string pattern;

            using (PatternSelectorForm selector =
                new PatternSelectorForm())
            {
                if (!selector.TryGetPattern(out pattern))
                    return;
            }

            string expandedPattern;

            try
            {
                expandedPattern =
                    PreparePattern(
                        pattern,
                        segments.Count);
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

            double bpm =
                GetProjectBpm(vegas);

            Timecode baseStep =
                CalculateBaseGridStep(bpm);

            using (UndoBlock undo = new UndoBlock(
                "Insert media pattern " + pattern))
            {
                InsertPattern(
                    segments,
                    baseStep,
                    expandedPattern);

                RemoveSourceEvents(
                    vegas,
                    segments);
            }
        }
        catch (OperationCanceledException)
        {
            // BPM or pattern dialog was cancelled.
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

    // -----------------------------------------------------------------
    // Fragment discovery
    // -----------------------------------------------------------------

    /// <summary>
    /// Finds selected media fragments and expands each fragment
    /// to include overlapping audio and video events.
    /// </summary>
    private List<MediaSegment> FindSelectedMediaSegments(
        Vegas vegas)
    {
        List<TrackEvent> selectedEvents =
            new List<TrackEvent>();

        foreach (Track track in vegas.Project.Tracks)
        {
            foreach (TrackEvent trackEvent in track.Events)
            {
                if (!trackEvent.Selected)
                    continue;

                if (trackEvent.IsAudio() ||
                    trackEvent.IsVideo())
                {
                    selectedEvents.Add(trackEvent);
                }
            }
        }

        if (selectedEvents.Count == 0)
            return new List<MediaSegment>();

        selectedEvents.Sort(
            delegate(
                TrackEvent first,
                TrackEvent second)
            {
                int position =
                    first.Start.Nanos.CompareTo(
                        second.Start.Nanos);

                if (position != 0)
                    return position;

                return first.Track.Index.CompareTo(
                    second.Track.Index);
            });

        List<MediaSegment> segments =
            new List<MediaSegment>();

        foreach (TrackEvent selectedEvent in selectedEvents)
        {
            MediaSegment segment = null;

            foreach (MediaSegment existing in segments)
            {
                bool overlaps =
                    selectedEvent.Start.Nanos <
                    existing.End.Nanos &&
                    selectedEvent.End.Nanos >
                    existing.Start.Nanos;

                if (overlaps)
                {
                    segment = existing;
                    break;
                }
            }

            if (segment == null)
            {
                segment =
                    new MediaSegment
                    {
                        Start = selectedEvent.Start,
                        End = selectedEvent.End
                    };

                segments.Add(segment);
            }

            if (selectedEvent.Start.Nanos <
                segment.Start.Nanos)
            {
                segment.Start =
                    selectedEvent.Start;
            }

            if (selectedEvent.End.Nanos >
                segment.End.Nanos)
            {
                segment.End =
                    selectedEvent.End;
            }
        }

        segments.Sort(
            delegate(
                MediaSegment first,
                MediaSegment second)
            {
                return first.Start.Nanos.CompareTo(
                    second.Start.Nanos);
            });

        foreach (MediaSegment segment in segments)
        {
            AddOverlappingEvents(
                vegas,
                segment);
        }

        return segments;
    }

    /// <summary>
    /// Adds every audio and video event overlapping a fragment.
    /// </summary>
    private void AddOverlappingEvents(
        Vegas vegas,
        MediaSegment segment)
    {
        segment.Events.Clear();

        foreach (Track track in vegas.Project.Tracks)
        {
            foreach (TrackEvent trackEvent in track.Events)
            {
                if (!trackEvent.IsAudio() &&
                    !trackEvent.IsVideo())
                {
                    continue;
                }

                bool overlaps =
                    trackEvent.Start.Nanos < segment.End.Nanos &&
                    trackEvent.End.Nanos > segment.Start.Nanos;

                if (!overlaps)
                    continue;

                bool alreadyAdded = false;

                foreach (TrackEvent existing in segment.Events)
                {
                    if (existing == trackEvent)
                    {
                        alreadyAdded = true;
                        break;
                    }
                }

                if (!alreadyAdded)
                    segment.Events.Add(trackEvent);
            }
        }

        segment.Events.Sort(
            delegate(
                TrackEvent first,
                TrackEvent second)
            {
                int position =
                    first.Start.Nanos.CompareTo(
                        second.Start.Nanos);

                if (position != 0)
                    return position;

                return first.Track.Index.CompareTo(
                    second.Track.Index);
            });
    }

    // -----------------------------------------------------------------
    // BPM / grid
    // -----------------------------------------------------------------

    private double GetProjectBpm(Vegas vegas)
    {
        double bpm =
            vegas.Project.Ruler.BeatsPerMinute;

        if (bpm > 0)
            return bpm;

        return DEFAULT_BPM;
    }

    private Timecode CalculateBaseGridStep(
        double bpm)
    {
        if (bpm <= 0)
        {
            throw new InvalidOperationException(
                "BPM must be greater than zero.");
        }

        double quarterSeconds =
            60.0 / bpm;

        double barSeconds =
            quarterSeconds * BEATS_PER_BAR;

        double stepSeconds =
            barSeconds / BASE_GRID_DIVISIONS;

        return Timecode.FromSeconds(stepSeconds);
    }

    // -----------------------------------------------------------------
    // Pattern preparation (expansion + validation)
    // -----------------------------------------------------------------

    /// <summary>
    /// Expands regex-style constructs and validates the result.
    /// </summary>
    private string PreparePattern(
        string rawPattern,
        int fragmentCount)
    {
        if (String.IsNullOrEmpty(rawPattern))
        {
            throw new ArgumentException(
                "The pattern cannot be empty.");
        }

        string expanded =
            ExpandRegex(rawPattern);

        ValidateExpandedPattern(
            expanded,
            fragmentCount);

        return expanded;
    }

    /// <summary>
    /// Expands deterministic regex-style constructs <c>(...)</c> and
    /// <c>{n}</c> into a flat pattern string.
    /// </summary>
    private string ExpandRegex(string pattern)
    {
        int position = 0;

        string result =
            ParseSequence(
                pattern,
                ref position,
                false);

        if (position != pattern.Length)
        {
            throw new ArgumentException(
                "Unexpected characters after position " +
                position +
                ".");
        }

        return result;
    }

    /// <summary>
    /// Recursively parses a pattern sequence until the end of input or
    /// a closing parenthesis.
    /// </summary>
    private string ParseSequence(
        string pattern,
        ref int position,
        bool insideGroup)
    {
        StringBuilder result =
            new StringBuilder();

        while (position < pattern.Length)
        {
            char symbol =
                pattern[position];

            if (symbol == ')')
            {
                if (!insideGroup)
                {
                    throw new ArgumentException(
                        "Unmatched ')' at position " +
                        position +
                        ".");
                }

                return result.ToString();
            }

            string atom;
            bool atomFromGroup = false;

            if (symbol == '(')
            {
                position++;

                string inner =
                    ParseSequence(
                        pattern,
                        ref position,
                        true);

                if (position >= pattern.Length ||
                    pattern[position] != ')')
                {
                    throw new ArgumentException(
                        "Missing ')' at end of pattern.");
                }

                position++;
                atom = inner;
                atomFromGroup = true;
            }
            else if (symbol == '{')
            {
                throw new ArgumentException(
                    "'{' at position " +
                    position +
                    " must follow an atom (fragment, skip or group).");
            }
            else if (symbol == '}')
            {
                throw new ArgumentException(
                    "Unmatched '}' at position " +
                    position +
                    ".");
            }
            else if (symbol == '*' || symbol == '/')
            {
                throw new ArgumentException(
                    "Duration modifier '" +
                    symbol +
                    "' at position " +
                    position +
                    " must directly follow a fragment or skip symbol.");
            }
            else if (IsAtomChar(symbol))
            {
                StringBuilder atomBuilder =
                    new StringBuilder();

                atomBuilder.Append(symbol);
                position++;

                while (position < pattern.Length &&
                       (pattern[position] == '*' ||
                        pattern[position] == '/'))
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
                throw new ArgumentException(
                    "Invalid character '" +
                    symbol +
                    "' at position " +
                    position +
                    ". Allowed symbols: 0-9, A-Z, a-z, _, *, /, (, ), {, }.");
            }

            // Handle any number of consecutive {n} repetitions.
            bool hadRepetition = false;

            while (position < pattern.Length &&
                   pattern[position] == '{')
            {
                int count =
                    ParseRepetitionCount(
                        pattern,
                        ref position);

                atom =
                    RepeatString(
                        atom,
                        count);

                hadRepetition = true;
            }

            result.Append(atom);

            // A duration modifier cannot follow a group or repetition.
            if (position < pattern.Length &&
                (pattern[position] == '*' ||
                 pattern[position] == '/'))
            {
                if (hadRepetition)
                {
                    throw new ArgumentException(
                        "Duration modifier '" +
                        pattern[position] +
                        "' at position " +
                        position +
                        " cannot follow a repetition count. " +
                        "Place it before the count (e.g., '1*{2}' instead of '1{2}*').");
                }

                if (atomFromGroup)
                {
                    throw new ArgumentException(
                        "Duration modifier '" +
                        pattern[position] +
                        "' at position " +
                        position +
                        " cannot follow a group. " +
                        "Place it inside the group (e.g., '(1*2)' instead of '(12)*').");
                }
            }
        }

        if (insideGroup)
        {
            throw new ArgumentException(
                "Missing ')' at end of pattern.");
        }

        return result.ToString();
    }

    /// <summary>
    /// Parses a <c>{n}</c> repetition count.
    /// </summary>
    private int ParseRepetitionCount(
        string pattern,
        ref int position)
    {
        // pattern[position] == '{'
        int braceStart = position;
        position++;

        int start = position;

        while (position < pattern.Length &&
               pattern[position] >= '0' &&
               pattern[position] <= '9')
        {
            position++;
        }

        if (start == position)
        {
            throw new ArgumentException(
                "Missing repetition count after '{' at position " +
                braceStart +
                ".");
        }

        string countText =
            pattern.Substring(
                start,
                position - start);

        int count;

        if (!int.TryParse(countText, out count))
        {
            throw new ArgumentException(
                "Invalid repetition count '" +
                countText +
                "'.");
        }

        if (count <= 0)
        {
            throw new ArgumentException(
                "Repetition count must be positive (got " +
                count +
                ").");
        }

        if (count > MAX_REPETITION)
        {
            throw new ArgumentException(
                "Repetition count " +
                count +
                " exceeds the maximum of " +
                MAX_REPETITION +
                ".");
        }

        if (position >= pattern.Length ||
            pattern[position] != '}')
        {
            if (position < pattern.Length &&
                pattern[position] == ',')
            {
                throw new ArgumentException(
                    "Non-deterministic repetition '{n,m}' at position " +
                    braceStart +
                    " is not supported. Use '{n}' for exact repetition.");
            }

            throw new ArgumentException(
                "Missing '}' for repetition starting at position " +
                braceStart +
                ".");
        }

        position++;
        return count;
    }

    /// <summary>
    /// Repeats a string a given number of times with a size guard.
    /// </summary>
    private string RepeatString(
        string value,
        int count)
    {
        long totalLength =
            (long)value.Length * count;

        if (totalLength > MAX_EXPANDED_LENGTH)
        {
            throw new ArgumentException(
                "The expanded pattern is too large (limit " +
                MAX_EXPANDED_LENGTH +
                " symbols).");
        }

        if (count == 0 || value.Length == 0)
            return String.Empty;

        if (count == 1)
            return value;

        StringBuilder builder =
            new StringBuilder((int)totalLength);

        for (int i = 0; i < count; i++)
            builder.Append(value);

        return builder.ToString();
    }

    // -----------------------------------------------------------------
    // Character classification
    // -----------------------------------------------------------------

    /// <summary>
    /// Returns true if the character can begin an atom.
    /// </summary>
    private bool IsAtomChar(char symbol)
    {
        return symbol == '_' ||
               (symbol >= '0' && symbol <= '9') ||
               (symbol >= 'A' && symbol <= 'Z') ||
               (symbol >= 'a' && symbol <= 'z');
    }

    /// <summary>
    /// Returns true if the character denotes a skip/rest.
    /// </summary>
    private bool IsSkip(char symbol)
    {
        return symbol == '_' || symbol == '0';
    }

    /// <summary>
    /// Maps a fragment character to a 1-based fragment index.
    /// Returns -1 if the character is not a fragment reference.
    /// </summary>
    private int GetFragmentIndex(char symbol)
    {
        if (symbol >= '1' && symbol <= '9')
            return symbol - '0';

        if (symbol >= 'A' && symbol <= 'Z')
            return symbol - 'A' + 10;

        if (symbol >= 'a' && symbol <= 'z')
            return symbol - 'a' + 10;

        return -1;
    }

    /// <summary>
    /// Returns a human-readable fragment name.
    /// </summary>
    private string GetFragmentName(int fragmentIndex)
    {
        if (fragmentIndex >= 1 && fragmentIndex <= 9)
            return fragmentIndex.ToString();

        if (fragmentIndex >= 10 && fragmentIndex <= 35)
            return ((char)('A' + fragmentIndex - 10)).ToString();

        return "?";
    }

    /// <summary>
    /// Validates an expanded pattern against the available fragment count.
    /// </summary>
    private void ValidateExpandedPattern(
        string expanded,
        int fragmentCount)
    {
        int index = 0;

        while (index < expanded.Length)
        {
            char symbol = expanded[index];

            bool isSkip = IsSkip(symbol);
            int fragmentIndex = GetFragmentIndex(symbol);

            if (!isSkip && fragmentIndex < 0)
            {
                throw new ArgumentException(
                    "Invalid symbol '" +
                    symbol +
                    "' at position " +
                    index +
                    " in the expanded pattern.");
            }

            if (fragmentIndex > 0 &&
                fragmentIndex > fragmentCount)
            {
                throw new ArgumentException(
                    "The pattern references fragment " +
                    GetFragmentName(fragmentIndex) +
                    ", but only " +
                    fragmentCount +
                    " fragment(s) are available.");
            }

            index++;

            while (index < expanded.Length &&
                   (expanded[index] == '*' ||
                    expanded[index] == '/'))
            {
                index++;
            }
        }
    }

    // -----------------------------------------------------------------
    // Pattern insertion
    // -----------------------------------------------------------------

    private double CalculateMultiplier(
        int asteriskCount,
        int slashCount)
    {
        return Math.Pow(
            2.0,
            asteriskCount - slashCount);
    }

    /// <summary>
    /// Inserts copies according to the expanded pattern.
    /// </summary>
    private void InsertPattern(
        List<MediaSegment> segments,
        Timecode baseStep,
        string expandedPattern)
    {
        long cursor =
            segments[0].Start.Nanos;

        int index = 0;

        while (index < expandedPattern.Length)
        {
            char symbol =
                expandedPattern[index];

            index++;

            int asteriskCount = 0;
            int slashCount = 0;

            while (index < expandedPattern.Length &&
                   (expandedPattern[index] == '*' ||
                    expandedPattern[index] == '/'))
            {
                if (expandedPattern[index] == '*')
                    asteriskCount++;
                else
                    slashCount++;

                index++;
            }

            double multiplier =
                CalculateMultiplier(
                    asteriskCount,
                    slashCount);

            long duration =
                (long)Math.Round(
                    baseStep.Nanos * multiplier);

            if (duration <= 0)
            {
                throw new InvalidOperationException(
                    "The calculated duration is invalid.");
            }

            if (IsSkip(symbol))
            {
                cursor += duration;
                continue;
            }

            int fragmentIndex =
                GetFragmentIndex(symbol);

            if (fragmentIndex <= 0 ||
                fragmentIndex > segments.Count)
            {
                throw new InvalidOperationException(
                    "Fragment reference '" +
                    GetFragmentName(fragmentIndex) +
                    "' is out of range.");
            }

            MediaSegment sourceSegment =
                segments[fragmentIndex - 1];

            CopyMediaSegment(
                sourceSegment,
                Timecode.FromNanos(cursor),
                multiplier);

            cursor += duration;
        }
    }

    private void CopyMediaSegment(
        MediaSegment sourceSegment,
        Timecode destinationStart,
        double multiplier)
    {
        foreach (TrackEvent sourceEvent in sourceSegment.Events)
        {
            long relativeOffset =
                sourceEvent.Start.Nanos -
                sourceSegment.Start.Nanos;

            if (relativeOffset < 0)
                relativeOffset = 0;

            long adjustedOffset =
                (long)Math.Round(
                    relativeOffset * multiplier);

            Timecode copyStart =
                Timecode.FromNanos(
                    destinationStart.Nanos +
                    adjustedOffset);

            TrackEvent copy =
                sourceEvent.Copy(
                    sourceEvent.Track,
                    copyStart);

            copy.Start = copyStart;
            copy.Length =
                Timecode.FromNanos(
                    (long)Math.Round(
                        sourceEvent.Length.Nanos *
                        multiplier));

            SetCopyTakeOffset(
                sourceEvent,
                copy,
                sourceSegment.Start,
                multiplier);

            copy.Selected = true;
        }
    }

    private void SetCopyTakeOffset(
        TrackEvent sourceEvent,
        TrackEvent copy,
        Timecode sourceStart,
        double multiplier)
    {
        Take sourceTake =
            sourceEvent.ActiveTake;

        Take copyTake =
            copy.ActiveTake;

        if (sourceTake == null ||
            copyTake == null)
        {
            return;
        }

        long relativeOffset =
            sourceEvent.Start.Nanos -
            sourceStart.Nanos;

        if (relativeOffset < 0)
            relativeOffset = 0;

        copyTake.Offset =
            Timecode.FromNanos(
                sourceTake.Offset.Nanos +
                (long)Math.Round(
                    relativeOffset *
                    multiplier));
    }

    private void RemoveSourceEvents(
        Vegas vegas,
        List<MediaSegment> segments)
    {
        List<TrackEvent> eventsToRemove =
            new List<TrackEvent>();

        foreach (MediaSegment segment in segments)
        {
            foreach (TrackEvent trackEvent in segment.Events)
            {
                bool alreadyAdded = false;

                foreach (TrackEvent existing in eventsToRemove)
                {
                    if (existing == trackEvent)
                    {
                        alreadyAdded = true;
                        break;
                    }
                }

                if (!alreadyAdded)
                    eventsToRemove.Add(trackEvent);
            }
        }

        foreach (TrackEvent trackEvent in eventsToRemove)
        {
            if (trackEvent.Track != null &&
                trackEvent.Track.Events.Contains(trackEvent))
            {
                trackEvent.Track.Events.Remove(trackEvent);
            }
        }

        foreach (Track track in vegas.Project.Tracks)
        {
            foreach (TrackEvent trackEvent in track.Events)
            {
                trackEvent.Selected = false;
            }
        }
    }
}

/// <summary>
/// Represents one media fragment.
/// </summary>
public class MediaSegment
{
    public Timecode Start;
    public Timecode End;

    public List<TrackEvent> Events =
        new List<TrackEvent>();
}

/// <summary>
/// Represents a built-in pattern.
/// </summary>
public class PatternDefinition
{
    public string Name { get; private set; }
    public string Value { get; private set; }

    public PatternDefinition(
        string name,
        string value)
    {
        Name = name;
        Value = value;
    }

    public override string ToString()
    {
        return Name;
    }
}

/// <summary>
/// Provides built-in patterns.
/// </summary>
public static class PatternLibrary
{
    public static List<PatternDefinition> GetPatterns()
    {
        return new List<PatternDefinition>
        {
            new PatternDefinition(
                "Alternate",
                "1_2_"),

            new PatternDefinition(
                "Consecutive",
                "12"),

            new PatternDefinition(
                "Four fragments",
                "1234"),

            new PatternDefinition(
                "Four fragments with skips",
                "1_2_3_4_"),

            // ---- Regex-style demos ----

            new PatternDefinition(
                "Regex demo: (12){4}",
                "(12){4}"),

            new PatternDefinition(
                "Regex demo: 1{8}",
                "1{8}"),

            new PatternDefinition(
                "Regex demo: (1_2_){2}",
                "(1_2_){2}"),

            new PatternDefinition(
                "Regex demo: (12*){2}",
                "(12*){2}"),

            // ---- Existing Sparta set ----

            new PatternDefinition(
                "Sparta Remix Chorus",
                "11_11_111_1_1_11222_2_222_222_2_"),

            new PatternDefinition(
                "Sparta Remix Epicness main part",
                "1*__1*223*1_1_11__1_1*112_2_33331*2_1*223*1_1_111_11111111111111"),

            new PatternDefinition(
                "Sparta Remix Epicness second part",
                "2_2_____2__2_2222_22"),

            new PatternDefinition(
                "Sparta Remix Chorus (32)",
                "11_11_111_1_1_112/2/2/2/2/2/_2/2/_2/2/2/2/2/2/_2/2/2/2/2/2/_2/2/_"),

            new PatternDefinition(
                "Sparta Remix madness",
                "1*______________2*______________1*______________2*______________1*______2*______1*______2*______1*__2*__1*__2*__1*__1*__1*__1111"),

            new PatternDefinition(
                "KingSpartaX37 madness (no pitching)",
                "1/1/1/1/1/1/1/1/2/_/2/_/2/_/2/_/3/3/3/3/3/3/3/3/3/_/3/_/3/3/3/3/4/_/4/_/4/4/4/_/4/_/4/_/4/4/4/_/5**6/6/6/6/6/6/6/6/1/1/1/1/1/1/1/1/2/_/2/_/2/_/2/_/3/3/3/3/3/3/3/3/3/_/3/_/3/3/3/3/4/_/4/_/4/4/4/_/4/_/4/_/4/4/4/_/5**6/6/6/6/6/6/6/6/1*1*11112*2*22221*1*11112*2*22221*112*221*112*221122112211221111")
        };
    }
}

/// <summary>
/// Dialog for selecting a pattern.
/// </summary>
public class PatternSelectorForm : Form
{
    private RadioButton presetRadioButton;
    private RadioButton customRadioButton;
    private ComboBox presetComboBox;
    private TextBox patternTextBox;
    private Button insertButton;
    private Button cancelButton;
    private Button helpButton;

    public PatternSelectorForm()
    {
        Text = "Chorus Generator";
        Width = 600;
        Height = 210;
        StartPosition =
            FormStartPosition.CenterScreen;
        FormBorderStyle =
            FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        presetRadioButton =
            new RadioButton
            {
                Text = "Built-in pattern",
                Left = 15,
                Top = 15,
                Width = 180,
                Checked = true
            };

        customRadioButton =
            new RadioButton
            {
                Text = "Enter custom pattern",
                Left = 15,
                Top = 50,
                Width = 180
            };

        presetComboBox =
            new ComboBox
            {
                Left = 200,
                Top = 12,
                Width = 360,
                DropDownStyle =
                    ComboBoxStyle.DropDownList
            };

        foreach (PatternDefinition pattern
            in PatternLibrary.GetPatterns())
        {
            presetComboBox.Items.Add(pattern);
        }

        if (presetComboBox.Items.Count > 0)
            presetComboBox.SelectedIndex = 0;

        patternTextBox =
            new TextBox
            {
                Left = 200,
                Top = 48,
                Width = 360,
                Enabled = false
            };

        Label hintLabel =
            new Label
            {
                Text =
                    "0-9, A-Z: fragment (1-35);  _ or 0: skip;  " +
                    "*: x2;  /: x0.5;  (...){n}: repeat group n times",
                Left = 200,
                Top = 76,
                Width = 380,
                Height = 40
            };

        helpButton =
            new Button
            {
                Text = "Help",
                Left = 15,
                Top = 125,
                Width = 95
            };

        helpButton.Click +=
            HelpButton_Click;

        insertButton =
            new Button
            {
                Text = "Insert",
                Left = 360,
                Top = 125,
                Width = 95,
                DialogResult =
                    DialogResult.OK
            };

        cancelButton =
            new Button
            {
                Text = "Cancel",
                Left = 465,
                Top = 125,
                Width = 95,
                DialogResult =
                    DialogResult.Cancel
            };

        presetRadioButton.CheckedChanged +=
            PresetRadioButton_CheckedChanged;

        customRadioButton.CheckedChanged +=
            CustomRadioButton_CheckedChanged;

        presetComboBox.SelectedIndexChanged +=
            PresetComboBox_SelectedIndexChanged;

        Controls.Add(presetRadioButton);
        Controls.Add(customRadioButton);
        Controls.Add(presetComboBox);
        Controls.Add(patternTextBox);
        Controls.Add(hintLabel);
        Controls.Add(helpButton);
        Controls.Add(insertButton);
        Controls.Add(cancelButton);

        AcceptButton = insertButton;
        CancelButton = cancelButton;

        PresetComboBox_SelectedIndexChanged(
            this,
            EventArgs.Empty);
    }

    private void PresetRadioButton_CheckedChanged(
        object sender,
        EventArgs e)
    {
        presetComboBox.Enabled =
            presetRadioButton.Checked;

        patternTextBox.Enabled =
            customRadioButton.Checked;

        if (presetRadioButton.Checked)
        {
            PresetComboBox_SelectedIndexChanged(
                sender,
                e);
        }
    }

    private void CustomRadioButton_CheckedChanged(
        object sender,
        EventArgs e)
    {
        patternTextBox.Enabled =
            customRadioButton.Checked;

        if (customRadioButton.Checked)
        {
            patternTextBox.Focus();
            patternTextBox.SelectAll();
        }
    }

    private void PresetComboBox_SelectedIndexChanged(
        object sender,
        EventArgs e)
    {
        if (!presetRadioButton.Checked)
            return;

        PatternDefinition selected =
            presetComboBox.SelectedItem
            as PatternDefinition;

        if (selected != null)
            patternTextBox.Text =
                selected.Value;
    }

    public bool TryGetPattern(
        out string pattern)
    {
        pattern = null;

        if (ShowDialog() != DialogResult.OK)
            return false;

        if (customRadioButton.Checked)
        {
            pattern =
                patternTextBox.Text.Trim();
        }
        else
        {
            PatternDefinition selected =
                presetComboBox.SelectedItem
                as PatternDefinition;

            if (selected == null)
                return false;

            pattern =
                selected.Value.Trim();
        }

        return !String.IsNullOrEmpty(pattern);
    }

    private void HelpButton_Click(
        object sender,
        EventArgs e)
    {
        using (PatternHelpForm helpForm =
               new PatternHelpForm())
        {
            helpForm.ShowDialog(this);
        }
    }
}

/// <summary>
/// Dialog for manually entering BPM.
/// </summary>
public class BpmInputForm : Form
{
    public double Bpm { get; private set; }

    private NumericUpDown bpmInput;

    public BpmInputForm(double defaultBpm)
    {
        Text = "Enter BPM";
        Width = 320;
        Height = 165;
        StartPosition =
            FormStartPosition.CenterScreen;
        FormBorderStyle =
            FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        Label label =
            new Label
            {
                Text = "BPM:",
                Left = 15,
                Top = 18,
                Width = 50
            };

        bpmInput =
            new NumericUpDown
            {
                Left = 70,
                Top = 15,
                Width = 210,
                Minimum = 1,
                Maximum = 999,
                DecimalPlaces = 2,
                Increment = 1,
                Value = Convert.ToDecimal(
                    defaultBpm)
            };

        Button okButton =
            new Button
            {
                Text = "Continue",
                Left = 110,
                Top = 60,
                Width = 80,
                DialogResult =
                    DialogResult.OK
            };

        Button cancelButton =
            new Button
            {
                Text = "Cancel",
                Left = 200,
                Top = 60,
                Width = 80,
                DialogResult =
                    DialogResult.Cancel
            };

        okButton.Click +=
            OkButton_Click;

        Controls.Add(label);
        Controls.Add(bpmInput);
        Controls.Add(okButton);
        Controls.Add(cancelButton);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    private void OkButton_Click(
        object sender,
        EventArgs e)
    {
        Bpm =
            Convert.ToDouble(
                bpmInput.Value);
    }
}

/// <summary>
/// Displays documentation for the pattern notation.
/// </summary>
public class PatternHelpForm : Form
{
    private TextBox helpTextBox;
    private Button closeButton;

    public PatternHelpForm()
    {
        Text = "Pattern Help";
        Width = 780;
        Height = 640;
        StartPosition =
            FormStartPosition.CenterParent;
        FormBorderStyle =
            FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;

        helpTextBox =
            new TextBox
            {
                Left = 10,
                Top = 10,
                Width = 740,
                Height = 530,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = false,
                Font = new System.Drawing.Font(
                    "Consolas",
                    10.0f),
                Text =
                    "CHORUS GENERATOR PATTERN NOTATION\r\n" +
                    "\r\n" +
                    "A pattern describes how selected media fragments\r\n" +
                    "are copied to the timeline.\r\n" +
                    "\r\n" +
                    "FRAGMENT REFERENCES\r\n" +
                    "\r\n" +
                    "  1-9   -> fragments 1 through 9.\r\n" +
                    "  A-Z   -> fragments 10 through 35.\r\n" +
                    "  a-z   -> alias for A-Z (case-insensitive).\r\n" +
                    "\r\n" +
                    "A pattern cannot reference more fragments than were\r\n" +
                    "detected in the timeline. Up to 35 fragments are supported.\r\n" +
                    "\r\n" +
                    "SKIP SYMBOLS\r\n" +
                    "\r\n" +
                    "  _     -> skip one base grid step.\r\n" +
                    "  0     -> same as '_' (alternate rest symbol).\r\n" +
                    "\r\n" +
                    "DURATION MODIFIERS\r\n" +
                    "\r\n" +
                    "  *     -> double the duration of the preceding fragment.\r\n" +
                    "  /     -> halve the duration of the preceding fragment.\r\n" +
                    "\r\n" +
                    "Modifiers may be repeated and mixed:\r\n" +
                    "\r\n" +
                    "  1*          x2\r\n" +
                    "  1**         x4\r\n" +
                    "  1/          x0.5\r\n" +
                    "  1//         x0.25\r\n" +
                    "  1*/         x1  (double then halve)\r\n" +
                    "  1**/        x2\r\n" +
                    "\r\n" +
                    "REGEX-STYLE EXPANSION (DETERMINISTIC SUBSET)\r\n" +
                    "\r\n" +
                    "  (...)       -> grouping. Used together with {n}.\r\n" +
                    "  {n}         -> repeat the preceding atom exactly n times.\r\n" +
                    "\r\n" +
                    "An atom is one of:\r\n" +
                    "  - a fragment reference (1-9, A-Z, a-z)\r\n" +
                    "  - a skip symbol (_ or 0)\r\n" +
                    "  - an atom with duration modifiers (e.g. 1*, 2/)\r\n" +
                    "  - a parenthesized group ( ... )\r\n" +
                    "\r\n" +
                    "Examples:\r\n" +
                    "\r\n" +
                    "  (12){4}         -> 12121212\r\n" +
                    "  1{8}            -> 11111111\r\n" +
                    "  (1_2_){2}       -> 1_2_1_2_\r\n" +
                    "  (12*){2}        -> 12*12*\r\n" +
                    "  (1*2){2}        -> 1*21*2\r\n" +
                    "  A{2}_B{2}       -> AAB B -> fragment 10 twice, skip, fragment 11 twice.\r\n" +
                    "\r\n" +
                    "Chained repetitions are allowed:\r\n" +
                    "\r\n" +
                    "  1{2}{3}         -> 1{6}  -> six copies of fragment 1.\r\n" +
                    "\r\n" +
                    "NOT SUPPORTED (non-deterministic regex features):\r\n" +
                    "\r\n" +
                    "  |               -> alternation would create ambiguity.\r\n" +
                    "  ?               -> optional atom is non-deterministic.\r\n" +
                    "  +               -> one-or-more is non-deterministic.\r\n" +
                    "  {n,m}           -> ranges are non-deterministic.\r\n" +
                    "  [...]           -> character classes are ambiguous.\r\n" +
                    "  . ^ $ \\         -> not applicable to pattern generation.\r\n" +
                    "\r\n" +
                    "IMPORTANT: The * and / symbols ALWAYS mean duration\r\n" +
                    "doubling/halving. They are never regex quantifiers.\r\n" +
                    "\r\n" +
                    "To double every fragment in a group, put * inside the group:\r\n" +
                    "\r\n" +
                    "  (1*2*3*){2}     -> 1*2*3*1*2*3*\r\n" +
                    "\r\n" +
                    "To double only the last fragment in a group, put * after it:\r\n" +
                    "\r\n" +
                    "  (123*){2}       -> 123*123*\r\n" +
                    "\r\n" +
                    "VALID EXAMPLE PATTERNS\r\n" +
                    "\r\n" +
                    "  1_2_                     Alternate two fragments with gaps.\r\n" +
                    "  1234                     Four fragments consecutively.\r\n" +
                    "  (12){4}                  Group of 12, repeated 4 times.\r\n" +
                    "  (1_2_){2}3*              Group with skips, then doubled 3.\r\n" +
                    "  1*__1*223*1_1_11__1_1*112_2_33331*2_1*223*1_1_111_11111111111111\r\n" +
                    "                           (Sparta Remix Epicness main part)\r\n" +
                    "\r\n" +
                    "BPM AND GRID\r\n" +
                    "\r\n" +
                    "The script reads BPM from the VEGAS project ruler.\r\n" +
                    "The base grid step = (60 / BPM) * 4 / 16 seconds.\r\n" +
                    "If the project ruler returns an invalid BPM, 120 is used.\r\n" +
                    "\r\n" +
                    "After successful insertion the original source events\r\n" +
                    "are removed, so only the generated pattern remains.\r\n" +
                    "\r\n" +
                    "The inserted copies remain selected when the script finishes.\r\n"
            };

        closeButton =
            new Button
            {
                Text = "Close",
                Left = 655,
                Top = 555,
                Width = 95,
                DialogResult =
                    DialogResult.OK
            };

        Controls.Add(helpTextBox);
        Controls.Add(closeButton);

        AcceptButton = closeButton;
        CancelButton = closeButton;
    }
}