using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using ScriptPortal.Vegas;

/// <summary>
/// Скрипт вставки до четырех выделенных видео-фрагментов
/// по пользовательскому ритмическому шаблону.
/// </summary>
public class EntryPoint
{
    #region Configuration

    /// <summary>
    /// BPM проекта по умолчанию.
    /// </summary>
    private const double DEFAULT_BPM = 120.0;

    /// <summary>
    /// Количество долей в такте.
    /// Для размера 4/4 значение равно 4.
    /// </summary>
    private const int BEATS_PER_BAR = 4;

    /// <summary>
    /// Количество базовых шагов сетки в одном такте.
    /// BASE_GRID_DIVISIONS = 16 означает сетку в одну 16-долю.
    /// </summary>
    private const int BASE_GRID_DIVISIONS = 16;

    #endregion

    #region VEGAS entry point

    /// <summary>
    /// Основная точка входа скрипта VEGAS.
    /// </summary>
    /// <param name="vegas">
    /// Текущий объект проекта VEGAS.
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
                    "Выделите от одного до четырех видео-событий.",
                    "Chorus Generator",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return;
            }

            if (selectedEvents.Length > 4)
            {
                MessageBox.Show(
                    "Выделено больше четырех видео-событий.",
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
                "Вставить паттерн " + pattern))
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

    #endregion

    #region Selected events

    /// <summary>
    /// Находит выделенные видео-события на всех треках.
    /// </summary>
    /// <param name="vegas">
    /// Текущий объект проекта VEGAS.
    /// </param>
    /// <returns>
    /// Массив выделенных событий, отсортированных слева направо.
    /// Первый элемент соответствует символу 1,
    /// второй — символу 2 и т. д.
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
                return first.Start.Nanos.CompareTo(
                    second.Start.Nanos);
            });

        return selectedEvents.ToArray();
    }

    /// <summary>
    /// Определяет начало исходного сегмента.
    /// </summary>
    /// <param name="vegas">
    /// Текущий объект проекта VEGAS.
    /// </param>
    /// <param name="selectedEvents">
    /// Выделенные слева направо события.
    /// </param>
    /// <returns>
    /// Левую границу выделения или начало первого выделенного события.
    /// </returns>
    private Timecode GetSegmentStart(
        Vegas vegas,
        TrackEvent[] selectedEvents)
    {
        if (vegas.SelectionLength.Nanos > 0)
            return vegas.SelectionStart;

        return selectedEvents[0].Start;
    }

    #endregion

    #region Grid and BPM

    /// <summary>
    /// Получает BPM проекта из маркера формата BPM=значение.
    /// </summary>
    /// <param name="vegas">
    /// Текущий объект проекта VEGAS.
    /// </param>
    /// <returns>
    /// Найденное значение BPM или DEFAULT_BPM.
    /// </returns>
    /// <example>
    /// Для установки темпа создайте маркер с названием:
    /// BPM=128
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
    /// Рассчитывает длительность базового шага сетки.
    /// Базовый шаг представляет одну 16-долю при 4/4.
    /// </summary>
    /// <param name="vegas">
    /// Текущий объект проекта VEGAS.
    /// </param>
    /// <returns>
    /// Длительность одного базового шага сетки.
    /// </returns>
    private Timecode CalculateBaseGridStep(Vegas vegas)
    {
        double bpm =
            GetProjectBpm(vegas);

        if (bpm <= 0)
        {
            throw new InvalidOperationException(
                "BPM должен быть больше нуля.");
        }

        double quarterSeconds =
            60.0 / bpm;

        double barSeconds =
            quarterSeconds * BEATS_PER_BAR;

        double stepSeconds =
            barSeconds / BASE_GRID_DIVISIONS;

        return Timecode.FromSeconds(stepSeconds);
    }

    #endregion

    #region Pattern parser

    /// <summary>
    /// Проверяет синтаксис ритмического шаблона.
    /// Допустимая грамматика:
    /// _ | [1-4] [* или /]*.
    /// </summary>
    /// <param name="pattern">
    /// Строка шаблона.
    /// </param>
    /// <param name="eventCount">
    /// Количество выделенных фрагментов.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Возникает при пустом шаблоне, недопустимом символе,
    /// смешении символов * и / или отсутствии указанного фрагмента.
    /// </exception>
    private void ValidatePattern(
        string pattern,
        int eventCount)
    {
        if (String.IsNullOrEmpty(pattern))
        {
            throw new ArgumentException(
                "Паттерн не может быть пустым.");
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

            if (symbol < '1' || symbol > '4')
            {
                throw new ArgumentException(
                    "Недопустимый символ '" +
                    symbol +
                    "'. Ожидались цифры 1–4 или символ _.");
            }

            int eventNumber =
                symbol - '0';

            if (eventNumber > eventCount)
            {
                throw new ArgumentException(
                    "Паттерн использует фрагмент " +
                    eventNumber +
                    ", но выделено только " +
                    eventCount +
                    ".");
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
                        "Нельзя смешивать символы * и / " +
                        "в одном фрагменте.");
                }

                index++;
            }
        }
    }

    /// <summary>
    /// Рассчитывает длительность фрагмента.
    /// Каждый символ * удваивает базовую длительность.
    /// Каждый символ / уменьшает базовую длительность вдвое.
    /// </summary>
    /// <param name="baseStepSeconds">
    /// Длительность базового шага сетки.
    /// </param>
    /// <param name="starCount">
    /// Количество символов *.
    /// </param>
    /// <param name="slashCount">
    /// Количество символов /.
    /// </param>
    /// <returns>
    /// Длительность фрагмента в секундах.
    /// </returns>
    private double CalculateDurationSeconds(
        double baseStepSeconds,
        int starCount,
        int slashCount)
    {
        double multiplier =
            Math.Pow(
                2.0,
                starCount - slashCount);

        double duration =
            baseStepSeconds * multiplier;

        if (duration <= 0)
        {
            throw new InvalidOperationException(
                "Длительность фрагмента неположительная.");
        }

        return duration;
    }

    #endregion

    #region Insert pattern

    /// <summary>
    /// Вставляет выделенные фрагменты согласно паттерну.
    /// </summary>
    /// <param name="selectedEvents">
    /// События от одного до четырех,
    /// отсортированные слева направо.
    /// </param>
    /// <param name="segmentStart">
    /// Начало исходного сегмента или выделенного диапазона.
    /// </param>
    /// <param name="baseStep">
    /// Длительность базового шага сетки.
    /// </param>
    /// <param name="pattern">
    /// Строка ритмического шаблона.
    /// </param>
    /// <remarks>
    /// Символы 1–4 выбирают источник,
    /// символ * удваивает длительность,
    /// символ / уменьшает длительность,
    /// символ _ пропускает один базовый шаг.
    /// </remarks>
    private void InsertPattern(
        TrackEvent[] selectedEvents,
        Timecode segmentStart,
        Timecode baseStep,
        string pattern)
    {
        double cursorSeconds =
            segmentStart.ToSeconds();

        int index = 0;

        while (index < pattern.Length)
        {
            char symbol =
                pattern[index];

            if (symbol == '_')
            {
                cursorSeconds +=
                    baseStep.ToSeconds();

                index++;
                continue;
            }

            int eventNumber =
                symbol - '0';

            TrackEvent sourceEvent =
                selectedEvents[eventNumber - 1];

            index++;

            int starCount = 0;
            int slashCount = 0;

            while (index < pattern.Length &&
                   (pattern[index] == '*' ||
                    pattern[index] == '/'))
            {
                if (pattern[index] == '*')
                    starCount++;
                else
                    slashCount++;

                index++;
            }

            double durationSeconds =
                CalculateDurationSeconds(
                    baseStep.ToSeconds(),
                    starCount,
                    slashCount);

            Timecode copyStart =
                Timecode.FromSeconds(cursorSeconds);

            Timecode copyLength =
                Timecode.FromSeconds(durationSeconds);

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

            cursorSeconds +=
                durationSeconds;
        }
    }

    /// <summary>
    /// Настраивает смещение активного take скопированного события.
    /// </summary>
    /// <param name="sourceEvent">
    /// Исходное выделенное событие.
    /// </param>
    /// <param name="copy">
    /// Скопированное событие.
    /// </param>
    /// <param name="segmentStart">
    /// Начало исходного сегмента.
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
            sourceEvent.Start.ToSeconds() -
            segmentStart.ToSeconds();

        if (relativeOffset < 0)
            relativeOffset = 0;

        copyTake.Offset =
            Timecode.FromSeconds(
                sourceTake.Offset.ToSeconds() +
                relativeOffset);
    }

    #endregion
}

/// <summary>
/// Описание готового ритмического шаблона.
/// </summary>
public class PatternDefinition
{
    /// <summary>
    /// Название шаблона для отображения в интерфейсе.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    /// Строка шаблона.
    /// </summary>
    public string Value { get; private set; }

    /// <summary>
    /// Создает описание шаблона.
    /// </summary>
    /// <param name="name">
    /// Название шаблона.
    /// </param>
    /// <param name="value">
    /// Строка шаблона.
    /// </param>
    public PatternDefinition(
        string name,
        string value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>
    /// Возвращает название шаблона для ComboBox.
    /// </summary>
    /// <returns>
    /// Название шаблона.
    /// </returns>
    public override string ToString()
    {
        return Name;
    }
}

/// <summary>
/// Библиотека встроенных ритмических шаблонов.
/// </summary>
public static class PatternLibrary
{
    /// <summary>
    /// Возвращает готовые шаблоны.
    /// </summary>
    /// <returns>
    /// Список шаблонов для ComboBox.
    /// </returns>
    public static List<PatternDefinition> GetPatterns()
    {
        return new List<PatternDefinition>
        {
            new PatternDefinition(
                "Sparta Remix Chorus",
                "11_11_111_1_1_11222_2_222_222_2_"),

            new PatternDefinition(
                "Чередование 1 и 2",
                "1_2_"),

            new PatternDefinition(
                "Два фрагмента подряд",
                "12"),

            new PatternDefinition(
                "Четыре фрагмента",
                "1234"),

            new PatternDefinition(
                "Четыре фрагмента через шаг",
                "1_2_3_4_"),

            new PatternDefinition(
                "Длинный первый фрагмент",
                "1**_2"),

            new PatternDefinition(
                "Короткий второй фрагмент",
                "1_2/"),

            new PatternDefinition(
                "Двойной первый и одинарный второй",
                "1*__2")
        };
    }
}

/// <summary>
/// Окно выбора готового шаблона или ввода пользовательского шаблона.
/// </summary>
public class PatternSelectorForm : Form
{
    private RadioButton presetRadioButton;
    private RadioButton customRadioButton;
    private ComboBox presetComboBox;
    private TextBox patternTextBox;
    private Button selectButton;
    private Button cancelButton;

    /// <summary>
    /// Создает окно выбора ритмического шаблона.
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
            new RadioButton();

        presetRadioButton.Text =
            "Готовый шаблон";

        presetRadioButton.Left = 15;
        presetRadioButton.Top = 15;
        presetRadioButton.Width = 180;
        presetRadioButton.Checked = true;

        customRadioButton =
            new RadioButton();

        customRadioButton.Text =
            "Ввести свой шаблон";

        customRadioButton.Left = 15;
        customRadioButton.Top = 50;
        customRadioButton.Width = 180;

        presetComboBox =
            new ComboBox();

        presetComboBox.Left = 200;
        presetComboBox.Top = 12;
        presetComboBox.Width = 320;
        presetComboBox.DropDownStyle =
            ComboBoxStyle.DropDownList;

        foreach (PatternDefinition pattern
            in PatternLibrary.GetPatterns())
        {
            presetComboBox.Items.Add(pattern);
        }

        if (presetComboBox.Items.Count > 0)
            presetComboBox.SelectedIndex = 0;

        patternTextBox =
            new TextBox();

        patternTextBox.Left = 200;
        patternTextBox.Top = 48;
        patternTextBox.Width = 320;
        patternTextBox.Enabled = false;

        Label patternHint =
            new Label();

        patternHint.Text =
            "1–4: фрагмент; _: пропуск; *: удлинить; /: укоротить";

        patternHint.Left = 200;
        patternHint.Top = 76;
        patternHint.Width = 320;
        patternHint.Height = 30;

        selectButton =
            new Button();

        selectButton.Text =
            "Вставить";

        selectButton.Left = 320;
        selectButton.Top = 125;
        selectButton.Width = 95;
        selectButton.DialogResult =
            DialogResult.OK;

        cancelButton =
            new Button();

        cancelButton.Text =
            "Отмена";

        cancelButton.Left = 425;
        cancelButton.Top = 125;
        cancelButton.Width = 95;
        cancelButton.DialogResult =
            DialogResult.Cancel;

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
        Controls.Add(selectButton);
        Controls.Add(cancelButton);

        AcceptButton = selectButton;
        CancelButton = cancelButton;

        PresetComboBox_SelectedIndexChanged(
            this,
            EventArgs.Empty);
    }

    /// <summary>
    /// Обрабатывает переключение на готовые шаблоны.
    /// </summary>
    /// <param name="sender">
    /// Источник события.
    /// </param>
    /// <param name="e">
    /// Аргументы события.
    /// </param>
    private void PresetRadioButton_CheckedChanged(
        object sender,
        EventArgs e)
    {
        presetComboBox.Enabled =
            presetRadioButton.Checked;

        patternTextBox.Enabled =
            !presetRadioButton.Checked;

        if (presetRadioButton.Checked)
        {
            PresetComboBox_SelectedIndexChanged(
                sender,
                e);
        }
    }

    /// <summary>
    /// Обрабатывает переключение на ручной ввод шаблона.
    /// </summary>
    /// <param name="sender">
    /// Источник события.
    /// </param>
    /// <param name="e">
    /// Аргументы события.
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
    /// Обновляет поле шаблона при выборе готового пресета.
    /// </summary>
    /// <param name="sender">
    /// Источник события.
    /// </param>
    /// <param name="e">
    /// Аргументы события.
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
    /// Показывает окно и получает пользовательский шаблон.
    /// </summary>
    /// <param name="pattern">
    /// Выбранный или введенный паттерн.
    /// </param>
    /// <returns>
    /// true, если пользователь подтвердил выбор.
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

            pattern = selected.Value.Trim();
        }

        return !String.IsNullOrEmpty(pattern);
    }
}