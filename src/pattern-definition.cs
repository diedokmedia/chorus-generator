using System;
using System.Collections.Generic;

/// <summary>
/// Описание одного готового ритмического паттерна.
/// </summary>
public class PatternDefinition
{
    /// <summary>
    /// Название паттерна в интерфейсе.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    /// Строка паттерна.
    /// </summary>
    public string Value { get; private set; }

    /// <summary>
    /// Создаёт новый объект паттерна.
    /// </summary>
    /// <param name="name">
    /// Название паттерна.
    /// </param>
    /// <param name="value">
    /// Строка паттерна.
    /// </param>
    public PatternDefinition(
        string name,
        string value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>
    /// Возвращает имя паттерна для ComboBox.
    /// </summary>
    /// <returns>
    /// Название паттерна.
    /// </returns>
    public override string ToString()
    {
        return Name;
    }
}

/// <summary>
/// Библиотека готовых ритмических паттернов.
/// </summary>
public static class PatternLibrary
{
    /// <summary>
    /// Возвращает список встроенных паттернов.
    /// </summary>
    /// <returns>
    /// Коллекция объектов паттернов.
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
                "Очередная строка",
                "_1__2_")
        };
    }
}