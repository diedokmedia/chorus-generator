using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using ScriptPortal.Vegas;

#region Configuration

/// <summary>
/// Main entry point for the VEGAS Pro scripting API.
/// </summary>
public partial class EntryPoint
{
    /// <summary>
    /// Default BPM used when no valid project BPM marker is found.
    /// </summary>
    private const double DEFAULT_BPM = 120.0;

    /// <summary>
    /// Number of beats in one bar.
    /// </summary>
    private const int BEATS_PER_BAR = 4;

    /// <summary>
    /// Number of base grid steps in one bar.
    /// </summary>
    private const int BASE_GRID_DIVISIONS = 16;

    /// <summary>
    /// Entry point called by VEGAS when the script is launched.
    /// </summary>
    /// <param name="vegas">
    /// The active VEGAS application and project object.
    /// </param>
    public void FromVegas(Vegas vegas)
    {
        try
        {
            TrackEvent[] selectedEvents =
                FindSelectedVideoEvents(vegas);

            if (selectedEvents.Length == 0)
            {
                MessageBox.Show(
                    "Select one to four video events.",
                    "Chorus Generator",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return;
            }

            if (selectedEvents.Length > 4)
            {
                MessageBox.Show(
                    "More than four video events are selected.",
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
                selectedEvents.Length);

            Timecode segmentStart =
                GetSegmentStart(
                    vegas,
                    selectedEvents);

            Timecode baseStep =
                CalculateBaseGridStep(vegas);

            using (UndoBlock undo = new UndoBlock(
                "Insert pattern " + pattern))
            {
                InsertPattern(
                    selectedEvents,
                    segmentStart,
                    baseStep,
                    pattern);
            }
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
    /// Finds selected video events and orders them left to right.
    /// </summary>
    /// <param name="vegas">
    /// The active VEGAS application object.
    /// </param>
    /// <returns>
    /// Selected video events ordered by timeline position.
    /// </returns>
    private TrackEvent[] FindSelectedVideoEvents(Vegas vegas)
    {
        List<TrackEvent> selectedEvents =
            new List<TrackEvent>();

        foreach (Track track in vegas.Project.Tracks)
        {
            if (!(track is VideoTrack))
                continue;

            foreach (TrackEvent trackEvent in track.Events)
            {
                if (trackEvent is VideoEvent &&
                    trackEvent.Selected)
                {
                    selectedEvents.Add(trackEvent);
                }
            }
        }

        selectedEvents.Sort(
            delegate(
                TrackEvent first,
                TrackEvent second)
            {
                return first.Start.ToMilliseconds()
                    .CompareTo(
                        second.Start.ToMilliseconds());
            });

        return selectedEvents.ToArray();
    }

    /// <summary>
    /// Gets the start of the source segment.
    /// </summary>
    /// <param name="vegas">
    /// The active VEGAS application object.
    /// </param>
    /// <param name="selectedEvents">
    /// Selected video events ordered by timeline position.
    /// </param>
    /// <returns>
    /// The selection start or the start of the first selected event.
    /// </returns>
    private Timecode GetSegmentStart(
        Vegas vegas,
        TrackEvent[] selectedEvents)
    {
        if (vegas.SelectionLength.ToMilliseconds() > 0)
            return vegas.SelectionStart;

        return selectedEvents[0].Start;
    }
}

#endregion

#region Grid and BPM

/// <summary>
/// Provides project BPM and grid calculation helpers.
/// </summary>
public partial class EntryPoint
{
    /// <summary>
    /// Reads project BPM from a marker named <c>BPM=value</c>.
    /// </summary>
    /// <param name="vegas">
    /// The active VEGAS application object.
    /// </param>
    /// <returns>
    /// The project BPM or <see cref="DEFAULT_BPM"/>.
    /// </returns>
    /// <example>
    /// Create a project marker named:
    /// <code>BPM=128</code>
    /// </example>
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

            double bpm;

            bool parsed =
                Double.TryParse(
                    label.Substring(4),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out bpm);

            if (parsed && bpm > 0)
                return bpm;
        }

        return DEFAULT_BPM;
    }

    /// <summary>
    /// Calculates the duration of one base grid step.
    /// </summary>
    /// <param name="vegas">
    /// The active VEGAS application object.
    /// </param>
    /// <returns>
    /// Duration of one base grid step.
    /// </returns>
    private Timecode CalculateBaseGridStep(Vegas vegas)
    {
        double bpm =
            GetProjectBpm(vegas);

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

        return Timecode.FromMilliseconds(
            stepSeconds * 1000.0);
    }
}

#endregion

#region Pattern parser

/// <summary>
/// Provides pattern validation and duration calculation.
/// </summary>
public partial class EntryPoint
{
    /// <summary>
    /// Validates a pattern against the source event count.
    /// </summary>
    /// <param name="pattern">
    /// Pattern containing event numbers, underscores,
    /// asterisks, and slashes.
    /// </param>
    /// <param name="eventCount">
    /// Number of selected source events.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The pattern is empty or contains an invalid symbol,
    /// mixed modifiers, or an unavailable event number.
    /// </exception>
    private void ValidatePattern(
        string pattern,
        int eventCount)
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
                    "Invalid symbol '" +
                    symbol +
                    "'. Use 1-4 or underscore.");
            }

            int eventNumber =
                symbol - '0';

            if (eventNumber > eventCount)
            {
                throw new ArgumentException(
                    "The pattern uses event " +
                    eventNumber +
                    ", but only " +
                    eventCount +
                    " event(s) are selected.");
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
    /// Calculates an event duration.
    /// </summary>
    /// <param name="baseStepMilliseconds">
    /// Duration of the base grid step.
    /// </param>
    /// <param name="asteriskCount">
    /// Number of * modifiers.
    /// </param>
    /// <param name="slashCount">
    /// Number of / modifiers.
    /// </param>
    /// <returns>
    /// Calculated duration in milliseconds.
    /// </returns>
    private double CalculateDurationMilliseconds(
        double baseStepMilliseconds,
        int asteriskCount,
        int slashCount)
    {
        double multiplier =
            Math.Pow(
                2.0,
                asteriskCount - slashCount);

        double duration =
            baseStepMilliseconds * multiplier;

        if (duration <= 0)
        {
            throw new InvalidOperationException(
                "The fragment duration is not positive.");
        }

        return duration;
    }
}

#endregion

#region Pattern insertion

/// <summary>
/// Inserts copied events according to a parsed pattern.
/// </summary>
public partial class EntryPoint
{
    /// <summary>
    /// Inserts source events according to the supplied pattern.
    /// </summary>
    /// <param name="selectedEvents">
    /// Source events ordered from left to right.
    /// </param>
    /// <param name="segmentStart">
    /// Start of the source segment.
    /// </param>
    /// <param name="baseStep">
    /// Duration of one base grid step.
    /// </param>
    /// <param name="pattern">
    /// Pattern such as <c>1_2_</c> or <c>11_11_111_</c>.
    /// </param>
    private void InsertPattern(
        TrackEvent[] selectedEvents,
        Timecode segmentStart,
        Timecode baseStep,
        string pattern)
    {
        double cursorMilliseconds =
            segmentStart.ToMilliseconds();

        int index = 0;

        while (index < pattern.Length)
        {
            char symbol =
                pattern[index];

            if (symbol == '_')
            {
                cursorMilliseconds +=
                    baseStep.ToMilliseconds();

                index++;
                continue;
            }

            int eventNumber =
                symbol - '0';

            TrackEvent sourceEvent =
                selectedEvents[eventNumber - 1];

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

            double durationMilliseconds =
                CalculateDurationMilliseconds(
                    baseStep.ToMilliseconds(),
                    asteriskCount,
                    slashCount);

            Timecode copyStart =
                Timecode.FromMilliseconds(
                    cursorMilliseconds);

            Timecode copyLength =
                Timecode.FromMilliseconds(
                    durationMilliseconds);

            TrackEvent copy =
                sourceEvent.Copy(
                    sourceEvent.Track,
                    copyStart);

            copy.Start = copyStart;
            copy.Length = copyLength;

            SetCopyTakeOffset(
                sourceEvent,
                copy,
                segmentStart);

            copy.Selected = true;

            cursorMilliseconds +=
                durationMilliseconds;
        }
    }

    /// <summary>
    /// Sets the media offset of a copied event.
    /// </summary>
    /// <param name="sourceEvent">
    /// Source selected event.
    /// </param>
    /// <param name="copy">
    /// Copied event.
    /// </param>
    /// <param name="segmentStart">
    /// Start of the source segment.
    /// </param>
    private void SetCopyTakeOffset(
        TrackEvent sourceEvent,
        TrackEvent copy,
        Timecode segmentStart)
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

        double relativeOffset =
            sourceEvent.Start.ToMilliseconds() -
            segmentStart.ToMilliseconds();

        if (relativeOffset < 0)
            relativeOffset = 0;

        copyTake.Offset =
            Timecode.FromMilliseconds(
                sourceTake.Offset.ToMilliseconds() +
                relativeOffset);
    }
}

#endregion

#region Built-in patterns

/// <summary>
/// Represents a named built-in pattern.
/// </summary>
public class PatternDefinition
{
    /// <summary>
    /// Display name of the pattern.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    /// Pattern expression.
    /// </summary>
    public string Value { get; private set; }

    /// <summary>
    /// Creates a pattern definition.
    /// </summary>
    /// <param name="name">
    /// Display name.
    /// </param>
    /// <param name="value">
    /// Pattern expression.
    /// </param>
    public PatternDefinition(
        string name,
        string value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>
    /// Returns the pattern name for display controls.
    /// </summary>
    /// <returns>
    /// The pattern name.
    /// </returns>
    public override string ToString()
    {
        return Name;
    }
}

/// <summary>
/// Provides built-in chorus and rhythmic patterns.
/// </summary>
public static class PatternLibrary
{
    /// <summary>
    /// Gets the built-in pattern list.
    /// </summary>
    /// <returns>
    /// Available pattern definitions.
    /// </returns>
    public static List<PatternDefinition> GetPatterns()
    {
        return new List<PatternDefinition>
        {
            new PatternDefinition(
                "Sparta Remix Chorus",
                "11_11_111_1_1_11222_2_222_222_2_"),

            new PatternDefinition(
                "Alternate 1 and 2",
                "1_2_"),

            new PatternDefinition(
                "Two events consecutively",
                "12"),

            new PatternDefinition(
                "Four events consecutively",
                "1234"),

            new PatternDefinition(
                "Four events with skips",
                "1_2_3_4_"),

            new PatternDefinition(
                "Long first event",
                "1**_2"),

            new PatternDefinition(
                "Short second event",
                "1_2/"),

            new PatternDefinition(
                "Double first and short second",
                "1*__2/")
        };
    }
}

#endregion

#region Pattern selector

/// <summary>
/// Window for selecting a built-in pattern or entering a custom pattern.
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
    /// Creates the pattern selector window.
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
                Text = "Enter a custom pattern",
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

        Label patternHint =
            new Label
            {
                Text =
                    "1-4: event; _: skip; *: double; /: halve",
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
        Controls.Add(patternHint);
        Controls.Add(insertButton);
        Controls.Add(cancelButton);

        AcceptButton = insertButton;
        CancelButton = cancelButton;

        PresetComboBox_SelectedIndexChanged(
            this,
            EventArgs.Empty);
    }

    /// <summary>
    /// Handles switching to the built-in pattern mode.
    /// </summary>
    /// <param name="sender">
    /// Event source.
    /// </param>
    /// <param name="e">
    /// Event arguments.
    /// </param>
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
    /// Handles switching to custom pattern mode.
    /// </summary>
    /// <param name="sender">
    /// Event source.
    /// </param>
    /// <param name="e">
    /// Event arguments.
    /// </param>
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
    /// Updates the text field when a built-in pattern is selected.
    /// </summary>
    /// <param name="sender">
    /// Event source.
    /// </param>
    /// <param name="e">
    /// Event arguments.
    /// </param>
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
        {
            patternTextBox.Text =
                selected.Value;
        }
    }

    /// <summary>
    /// Displays the dialog and returns the resulting pattern.
    /// </summary>
    /// <param name="pattern">
    /// Selected or entered pattern.
    /// </param>
    /// <returns>
    /// True when the user confirms a non-empty pattern.
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

#endregion