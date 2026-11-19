using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Drawing;

/// <summary>
/// Окно выбора готового или пользовательского паттерна.
/// </summary>
public class PatternSelectorForm : Form
{
    private ComboBox presetComboBox;
    private TextBox patternTextBox;
    private Button selectButton;
    private Button cancelButton;

    /// <summary>
    /// Создаёт окно выбора паттерна.
    /// </summary>
    public PatternSelectorForm()
    {
        Text = "Генератор паттерна";
        Width = 540;
        Height = 190;
        StartPosition =
            FormStartPosition.CenterScreen;
        FormBorderStyle =
            FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        Label presetLabel =
            new Label();

        presetLabel.Text =
            "Готовый паттерн:";

        presetLabel.Left = 10;
        presetLabel.Top = 16;
        presetLabel.Width = 125;

        presetComboBox =
            new ComboBox();

        presetComboBox.Left = 140;
        presetComboBox.Top = 12;
        presetComboBox.Width = 360;
        presetComboBox.DropDownStyle =
            ComboBoxStyle.DropDownList;

        Label patternLabel =
            new Label();

        patternLabel.Text =
            "Паттерн:";

        patternLabel.Left = 10;
        patternLabel.Top = 54;
        patternLabel.Width = 125;

        patternTextBox =
            new TextBox();

        patternTextBox.Left = 140;
        patternTextBox.Top = 50;
        patternTextBox.Width = 360;

        selectButton =
            new Button();

        selectButton.Text =
            "Выбрать";

        selectButton.Left =
            300;

        selectButton.Top =
            90;

        selectButton.Width =
            95;

        selectButton.DialogResult =
            DialogResult.OK;

        cancelButton =
            new Button();

        cancelButton.Text =
            "Отмена";

        cancelButton.Left =
            405;

        cancelButton.Top =
            90;

        cancelButton.Width =
            95;

        cancelButton.DialogResult =
            DialogResult.Cancel;

        List<PatternDefinition> patterns =
            PatternLibrary.GetPatterns();

        foreach (PatternDefinition pattern in patterns)
            presetComboBox.Items.Add(pattern);

        if (presetComboBox.Items.Count > 0)
            presetComboBox.SelectedIndex = 0;

        presetComboBox.SelectedIndexChanged +=
            PresetComboBox_SelectedIndexChanged;

        Controls.Add(presetLabel);
        Controls.Add(presetComboBox);
        Controls.Add(patternLabel);
        Controls.Add(patternTextBox);
        Controls.Add(selectButton);
        Controls.Add(cancelButton);

        AcceptButton = selectButton;
        CancelButton = cancelButton;

        PresetComboBox_SelectedIndexChanged(
            this,
            EventArgs.Empty);
    }

    /// <summary>
    /// Обрабатывает выбор готового паттерна.
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
        PatternDefinition selected =
            presetComboBox.SelectedItem
            as PatternDefinition;

        if (selected != null)
            patternTextBox.Text =
                selected.Value;
    }

    /// <summary>
    /// Показывает окно и получает итоговый паттерн.
    /// </summary>
    /// <param name="pattern">
    /// Выбранная строка паттерна.
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

        pattern =
            patternTextBox.Text.Trim();

        return !String.IsNullOrEmpty(pattern);
    }
}