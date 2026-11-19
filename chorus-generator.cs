using System;
using System.Collections.Generic;
using System.Globalization;
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
                    "Select one to four audio or video fragments.",
                    "Chorus Generator",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return;
            }

            if (segments.Count > 4)
            {
                MessageBox.Show(
                    "Select no more than four media fragments.",
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

            ValidatePattern(
                pattern,
                segments.Count);

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
                    pattern);

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

    /// <summary>
    /// Finds selected media fragments and expands each fragment
    /// to include overlapping audio and video events.
    /// </summary>
    /// <param name="vegas">
    /// Active VEGAS application.
    /// </param>
    /// <returns>
    /// Selected media fragments ordered from left to right.
    /// </returns>
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
            MediaSegment segment =
                FindOverlappingSegment(
                    segments,
                    selectedEvent);

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
    /// Finds a fragment overlapping a selected event.
    /// </summary>
    /// <param name="segments">
    /// Existing media fragments.
    /// </param>
    /// <param name="trackEvent">
    /// Selected media event.
    /// </param>
    /// <returns>
    /// Overlapping fragment or null.
    /// </returns>
    private MediaSegment FindOverlappingSegment(
        List<MediaSegment> segments,
        TrackEvent trackEvent)
    {
        foreach (MediaSegment segment in segments)
        {
            bool overlaps =
                trackEvent.Start.Nanos <= segment.End.Nanos &&
                trackEvent.End.Nanos >= segment.Start.Nanos;

            if (overlaps)
                return segment;
        }

        return null;
    }

    /// <summary>
    /// Adds every audio and video event overlapping a fragment.
    /// </summary>
    /// <param name="vegas">
    /// Active VEGAS application.
    /// </param>
    /// <param name="segment">
    /// Fragment to expand.
    /// </param>
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

    /// <summary>
    /// Gets BPM from a project marker or opens a BPM input dialog.
    /// </summary>
    /// <param name="vegas">
    /// Active VEGAS application.
    /// </param>
    /// <returns>
    /// Project BPM or user-entered BPM.
    /// </returns>
    private double GetProjectBpm(Vegas vegas)
    {
        foreach (Marker marker in vegas.Project.Markers)
        {
            if (marker.Label == null)
                continue;

            string label =
                marker.Label.Trim();

            if (!label.StartsWith(
                "BPM=",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            double markerBpm;

            bool parsed =
                Double.TryParse(
                    label.Substring(4),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out markerBpm);

            if (parsed && markerBpm > 0)
                return markerBpm;
        }

        using (BpmInputForm bpmForm =
            new BpmInputForm(DEFAULT_BPM))
        {
            if (bpmForm.ShowDialog() != DialogResult.OK)
                throw new OperationCanceledException();

            return bpmForm.Bpm;
        }
    }

    /// <summary>
    /// Calculates the duration of one base grid step.
    /// </summary>
    /// <param name="bpm">
    /// Tempo used for grid calculation.
    /// </param>
    /// <returns>
    /// Duration of one base grid step.
    /// </returns>
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

    /// <summary>
    /// Validates a pattern and fragment references.
    /// </summary>
    /// <param name="pattern">
    /// Pattern expression.
    /// </param>
    /// <param name="fragmentCount">
    /// Number of detected fragments.
    /// </param>
    private void ValidatePattern(
        string pattern,
        int fragmentCount)
    {
        if (String.IsNullOrEmpty(pattern))
        {
            throw new ArgumentException(
                "The pattern cannot be empty.");
        }

        int index = 0;

        while (index < pattern.Length)
        {
            char symbol =
                pattern[index];

            if (symbol == '_')
            {
                index++;
                continue;
            }

            if (symbol < '1' ||
                symbol > '4')
            {
                throw new ArgumentException(
                    "Only fragment numbers 1-4, _, * and / " +
                    "are allowed.");
            }

            int fragmentNumber =
                symbol - '0';

            if (fragmentNumber > fragmentCount)
            {
                throw new ArgumentException(
                    "The pattern references fragment " +
                    fragmentNumber +
                    ", but only " +
                    fragmentCount +
                    " fragment(s) are available.");
            }

            index++;

            char modifier = '\0';

            while (index < pattern.Length &&
                   (pattern[index] == '*' ||
                    pattern[index] == '/'))
            {
                char currentModifier =
                    pattern[index];

                if (modifier == '\0')
                {
                    modifier = currentModifier;
                }
                else if (modifier != currentModifier)
                {
                    throw new ArgumentException(
                        "A fragment cannot mix * and / modifiers.");
                }

                index++;
            }
        }
    }

    /// <summary>
    /// Calculates a pattern duration multiplier.
    /// </summary>
    /// <param name="asteriskCount">
    /// Number of asterisk modifiers.
    /// </param>
    /// <param name="slashCount">
    /// Number of slash modifiers.
    /// </param>
    /// <returns>
    /// Duration multiplier.
    /// </returns>
    private double CalculateMultiplier(
        int asteriskCount,
        int slashCount)
    {
        return Math.Pow(
            2.0,
            asteriskCount - slashCount);
    }

    /// <summary>
    /// Inserts copies according to the pattern.
    /// </summary>
    /// <param name="segments">
    /// Source media fragments.
    /// </param>
    /// <param name="baseStep">
    /// Duration of one base grid step.
    /// </param>
    /// <param name="pattern">
    /// Pattern expression.
    /// </param>
    private void InsertPattern(
        List<MediaSegment> segments,
        Timecode baseStep,
        string pattern)
    {
        long cursor =
            segments[0].Start.Nanos;

        int index = 0;

        while (index < pattern.Length)
        {
            char symbol =
                pattern[index];

            if (symbol == '_')
            {
                cursor += baseStep.Nanos;
                index++;
                continue;
            }

            int fragmentNumber =
                symbol - '0';

            MediaSegment sourceSegment =
                segments[fragmentNumber - 1];

            index++;

            int asteriskCount = 0;
            int slashCount = 0;

            while (index < pattern.Length &&
                   (pattern[index] == '*' ||
                    pattern[index] == '/'))
            {
                if (pattern[index] == '*')
                    asteriskCount++;
                else
                    slashCount++;

                index++;
            }

            double multiplier =
                CalculateMultiplier(
                    asteriskCount,
                    slashCount);

            long fragmentDuration =
                (long)Math.Round(
                    baseStep.Nanos * multiplier);

            if (fragmentDuration <= 0)
            {
                throw new InvalidOperationException(
                    "The fragment duration must be positive.");
            }

            CopyMediaSegment(
                sourceSegment,
                Timecode.FromNanos(cursor),
                multiplier);

            cursor += fragmentDuration;
        }
    }

    /// <summary>
    /// Copies every audio and video event in a media fragment.
    /// </summary>
    /// <param name="sourceSegment">
    /// Source media fragment.
    /// </param>
    /// <param name="destinationStart">
    /// Start position of the copy.
    /// </param>
    /// <param name="multiplier">
    /// Relative duration and position multiplier.
    /// </param>
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

    /// <summary>
    /// Sets the source offset of a copied take.
    /// </summary>
    /// <param name="sourceEvent">
    /// Source media event.
    /// </param>
    /// <param name="copy">
    /// Copied media event.
    /// </param>
    /// <param name="sourceStart">
    /// Source fragment start.
    /// </param>
    /// <param name="multiplier">
    /// Relative offset multiplier.
    /// </param>
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

    /// <summary>
    /// Removes original source events after copies are inserted.
    /// </summary>
    /// <param name="vegas">
    /// Active VEGAS application.
    /// </param>
    /// <param name="segments">
    /// Source media fragments.
    /// </param>
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
    /// <summary>
    /// Fragment start position.
    /// </summary>
    public Timecode Start;

    /// <summary>
    /// Fragment end position.
    /// </summary>
    public Timecode End;

    /// <summary>
    /// Overlapping audio and video events.
    /// </summary>
    public List<TrackEvent> Events =
        new List<TrackEvent>();
}

/// <summary>
/// Represents a built-in pattern.
/// </summary>
public class PatternDefinition
{
    /// <summary>
    /// Pattern display name.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    /// Pattern expression.
    /// </summary>
    public string Value { get; private set; }

    /// <summary>
    /// Creates a pattern definition.
    /// </summary>
    /// <param name="name">Display name.</param>
    /// <param name="value">Pattern expression.</param>
    public PatternDefinition(
        string name,
        string value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>
    /// Returns the pattern name.
    /// </summary>
    /// <returns>
    /// Pattern name.
    /// </returns>
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
    /// <summary>
    /// Gets built-in patterns.
    /// </summary>
    /// <returns>
    /// Pattern definitions.
    /// </returns>
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

            new PatternDefinition(
                "Sparta Remix Chorus",
                "11_11_111_1_1_11222_2_222_222_2_")
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

    /// <summary>
    /// Creates the pattern selection dialog.
    /// </summary>
    public PatternSelectorForm()
    {
        Text = "Chorus Generator";
        Width = 560;
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
                Width = 320,
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
                Width = 320,
                Enabled = false
            };

        Label hintLabel =
            new Label
            {
                Text =
                    "1-4: fragment; _: skip; *: double; /: halve",
                Left = 200,
                Top = 76,
                Width = 320,
                Height = 30
            };

        insertButton =
            new Button
            {
                Text = "Insert",
                Left = 320,
                Top = 125,
                Width = 95,
                DialogResult =
                    DialogResult.OK
            };

        cancelButton =
            new Button
            {
                Text = "Cancel",
                Left = 425,
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
        Controls.Add(insertButton);
        Controls.Add(cancelButton);

        AcceptButton = insertButton;
        CancelButton = cancelButton;

        PresetComboBox_SelectedIndexChanged(
            this,
            EventArgs.Empty);
    }

    /// <summary>
    /// Handles built-in pattern mode.
    /// </summary>
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

    /// <summary>
    /// Handles custom pattern mode.
    /// </summary>
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

    /// <summary>
    /// Updates the pattern field for a preset.
    /// </summary>
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

    /// <summary>
    /// Returns the selected or custom pattern.
    /// </summary>
    /// <param name="pattern">
    /// Pattern text.
    /// </param>
    /// <returns>
    /// True if the pattern is valid and non-empty.
    /// </returns>
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
}

/// <summary>
/// Dialog for manually entering BPM.
/// </summary>
public class BpmInputForm : Form
{
    /// <summary>
    /// Entered BPM value.
    /// </summary>
    public double Bpm { get; private set; }

    private NumericUpDown bpmInput;

    /// <summary>
    /// Creates the BPM input dialog.
    /// </summary>
    /// <param name="defaultBpm">
    /// Initial BPM value.
    /// </param>
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

    /// <summary>
    /// Stores the entered BPM.
    /// </summary>
    /// <param name="sender">
    /// Event source.
    /// </param>
    /// <param name="e">
    /// Event arguments.
    /// </param>
    private void OkButton_Click(
        object sender,
        EventArgs e)
    {
        Bpm =
            Convert.ToDouble(
                bpmInput.Value);
    }
}
