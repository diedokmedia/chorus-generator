using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using ScriptPortal.Vegas;

/// <summary>
/// Основная точка входа скрипта VEGAS.
/// </summary>
public class EntryPoint
{
    /// <summary>
    /// BPM по умолчанию, если маркер BPM не найден.
    /// </summary>
    private const double DEFAULT_BPM = 120.0;

    /// <summary>
    /// Количество долей в такте. Для размера 4/4 значение равно 4.
    /// </summary>
    private const int BEATS_PER_BAR = 4;

    /// <summary>
    /// Базовая сетка содержит 16 шагов в одном такте.
    /// </summary>
    private const int BASE_GRID_DIVISIONS = 16;

    /// <summary>
    /// Основная точка входа скрипта VEGAS.
    /// </summary>
    /// <param name="vegas">
    /// Основной объект VEGAS Script API.
    /// </param>
    public void FromVegas(Vegas vegas)
    {
        try
        {
            TrackEvent[] selectedEvents =
                FindSelectedVideoEvents(vegas);

            if (selectedEvents.Length < 1 ||
                selectedEvents.Length > 4)
            {
                throw new InvalidOperationException(
                    "Выделите от одного до четырех видео-событий.");
            }

            using (PatternSelectorForm selector =
                new PatternSelectorForm())
            {
                string pattern;

                if (!selector.TryGetPattern(out pattern))
                    return;

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
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Ошибка генератора паттерна",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Находит выделенные видео-события и сортирует их слева направо.
    /// </summary>
    /// <param name="vegas">
    /// Основной объект VEGAS Script API.
    /// </param>
    /// <returns>
    /// Массив выделенных видео-событий от одного до четырех элементов.
    /// </returns>
    private TrackEvent[] FindSelectedVideoEvents(Vegas vegas)
    {
        List<TrackEvent> result =
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
                    result.Add(trackEvent);
                }
            }
        }

        result.Sort(
            delegate(
                TrackEvent first,
                TrackEvent second)
            {
                return first.Start.ToMilliseconds()
                    .CompareTo(
                        second.Start.ToMilliseconds());
            });

        return result.ToArray();
    }

    /// <summary>
    /// Определяет начало исходного сегмента.
    /// </summary>
    /// <param name="vegas">
    /// Основной объект VEGAS Script API.
    /// </param>
    /// <param name="selectedEvents">
    /// Выделенные видео-события.
    /// </param>
    /// <returns>
    /// Левая граница выделения или начало первого события.
    /// </returns>
    private Timecode GetSegmentStart(
        Vegas vegas,
        TrackEvent[] selectedEvents)
    {
        if (vegas.SelectionLength.ToMilliseconds() > 0)
            return vegas.SelectionStart;

        return selectedEvents[0].Start;
    }

    /// <summary>
    /// Получает BPM из маркера формата BPM=число.
    /// </summary>
    /// <param name="vegas">
    /// Основной объект VEGAS Script API.
    /// </param>
    /// <returns>
    /// Найденный BPM или BPM по умолчанию.
    /// </returns>
    /// <example>
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

            bool parsed = Double.TryParse(
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
    /// Рассчитывает длительность базового шага сетки 1/16.
    /// </summary>
    /// <param name="vegas">
    /// Основной объект VEGAS Script API.
    /// </param>
    /// <returns>
    /// Время одного базового шага сетки.
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

    /// <summary>
    /// Проверяет правильность ритмического паттерна.
    /// </summary>
    /// <param name="pattern">
    /// Паттерн из цифр 1–4, символа пропуска _,
    /// удлинения * и сокращения /.
    /// </param>
    /// <param name="eventCount">
    /// Количество выделенных фрагментов.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Паттерн пуст, содержит недопустимые символы
    /// или ссылается на отсутствующий фрагмент.
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

            if (symbol < '1' ||
                symbol > '4')
            {
                throw new ArgumentException(
                    "Недопустимый символ: " +
                    symbol +
                    ". Ожидалась цифра 1–4 или символ _.");
            }

            int eventNumber =
                symbol - '0';

            if (eventNumber > eventCount)
            {
                throw new ArgumentException(
                    "Паттерн использует фрагмент " +
                    eventNumber +
                    ", но выделено только " +
                    eventCount + ".");
            }

            index++;

            char modifier = '\0';

            while (index < pattern.Length &&
                   (pattern[index] == '*' ||
                    pattern[index] == '/'))
            {
                if (modifier == '\0')
                {
                    modifier = pattern[index];
                }
                else if (modifier != pattern[index])
                {
                    throw new ArgumentException(
                        "Нельзя смешивать * и / " +
                        "в одном фрагменте.");
                }

                index++;
            }
        }
    }

    /// <summary>
    /// Вставляет выделенные фрагменты согласно ритмическому паттерну.
    /// </summary>
    /// <param name="selectedEvents">
    /// Фрагменты от одного до четырех,
    /// отсортированные слева направо.
    /// </param>
    /// <param name="segmentStart">
    /// Начало исходного выделенного сегмента.
    /// </param>
    /// <param name="baseStep">
    /// Длительность базового шага сетки.
    /// </param>
    /// <param name="pattern">
    /// Строка паттерна.
    /// </param>
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
    /// Рассчитывает длительность одного фрагмента.
    /// </summary>
    /// <param name="baseStepSeconds">
    /// Длительность базового шага.
    /// </param>
    /// <param name="starCount">
    /// Количество символов удлинения *.
    /// </param>
    /// <param name="slashCount">
    /// Количество символов сокращения /.
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
                "Длительность фрагмента стала неположительной.");
        }

        return duration;
    }

    /// <summary>
    /// Устанавливает смещение источника у скопированного take.
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
}