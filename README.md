# Chorus Generator for VEGAS Pro

A VEGAS Script API utility for inserting one to four selected video events according to a configurable rhythmic pattern.

## Project Structure

```text
chorus-generator/
├── src/
│   └── chorus-generator.cs
└── README.md
```

The complete script is contained in:

```text
src/chorus-generator.cs
```

## Installation

1. Open `src/chorus-generator.cs` in the VEGAS Script Editor.
2. Make sure `ScriptPortal.Vegas.dll` is available.
3. Save the script.
4. Start it from **Tools → Scripting**.

Required assemblies:

```text
System.dll
System.Drawing.dll
System.Windows.Forms.dll
ScriptPortal.Vegas.dll
```

## BPM Configuration

Add a project marker using this format:

```text
BPM=120
```

Example values:

```text
BPM=128
BPM=140.5
BPM=180
```

If no valid BPM marker is found, the script uses:

```csharp
private const double DEFAULT_BPM = 120.0;
```

## Grid Calculation

The grid is calculated from project BPM and a 4/4 time signature:

```text
quarter-note duration = 60 / BPM
bar duration         = quarter-note duration * 4
base grid step       = bar duration / 16
```

For example, at `120 BPM`, the base grid step represents a sixteenth note:

```text
quarter-note duration = 0.500 seconds
bar duration          = 2.000 seconds
base grid step        = 0.125 seconds
```

## Selecting Source Events

Select one to four video events on the timeline.

Events are ordered from left to right:

```text
1 = leftmost selected event
2 = second selected event
3 = third selected event
4 = fourth selected event
```

The pattern symbols refer to these selected events.

## Pattern Syntax

| Symbol | Meaning |
|---|---|
| `1` | Insert the first selected event |
| `2` | Insert the second selected event |
| `3` | Insert the third selected event |
| `4` | Insert the fourth selected event |
| `_` | Skip one base grid step |
| `*` | Double the preceding event's duration |
| `/` | Halve the preceding event's duration |

Modifiers can be repeated:

```text
1*      Twice the base duration
1**     Four times the base duration
1***    Eight times the base duration

2/      Half of the base duration
2//     Quarter of the base duration
2///    One eighth of the base duration
```

For a sixteenth-note base grid:

```text
1       16th note
1*      8th note
1**     Quarter note
1***    Half note
1/      32nd note
1//     64th note
1///    128th note
```

Do not combine `*` and `/` on the same event:

```text
Invalid: 1*/
Invalid: 1/*
Valid:   1**
Valid:   2//
```

## Pattern Grammar

```text
pattern  ::= item+

item     ::= "_" | fragment

fragment ::= fragmentNumber modifier*

fragmentNumber ::= "1" | "2" | "3" | "4"

modifier ::= starModifier | slashModifier

starModifier  ::= "*"+
slashModifier ::= "/"+
```

Rules:

- The pattern cannot be empty.
- Only `1`, `2`, `3`, `4`, `_`, `*`, and `/` are valid.
- A modifier must follow an event number.
- A fragment cannot use both `*` and `/`.
- An event number cannot exceed the number of selected events.
- Consecutive `_` symbols represent multiple skipped grid steps.

## Regular Expression

The complete pattern can be validated with:

```regex
^(?:_|[1-4](?:\*+|\/+)*)+$
```

This expression means:

```text
(
    _ 
    or
    a number from 1 to 4 followed by a sequence of * or /
)
one or more times
```

Token matching can be performed with:

```regex
[1-4](?:\*+|\/+)*|_
```

For example:

```text
1_2**__3/
```

is parsed as:

```text
1
_
2**
_
_
3/
```

The regular expression only validates the pattern syntax. The script also verifies that the referenced event exists:

```csharp
eventNumber <= selectedEvents.Length
```

## Pattern Examples

### Alternate between events

```text
1_2_
```

```text
[event 1]
[one skipped step]
[event 2]
[one skipped step]
```

### Insert events consecutively

```text
12
```

```text
[event 1][event 2]
```

### Insert four events consecutively

```text
1234
```

### Insert four events with skips

```text
1_2_3_4_
```

### Insert a long event

```text
1**
```

### Insert a short event

```text
2/
```

### Combine a long event and a short event

```text
1**_2/
```

### Skip multiple grid steps

```text
__1___2_
```

### Sparta Remix Chorus preset

```text
11_11_111_1_1_11222_2_222_222_2_
```

## Duration Calculation

The duration formula is:

```text
duration =
    base grid step
    × 2^(asteriskCount)
    ÷ 2^(slashCount)
```

Equivalent formula:

```text
duration =
    base grid step
    × 2^(asteriskCount - slashCount)
```

Examples:

```text
1*   = base × 2
1**  = base × 4
1/   = base / 2
1//  = base / 4
1**/ = invalid
```

## Timeline Placement

Events are placed sequentially:

```text
event position =
    segment start
    + duration of all previous pattern elements
```

For example:

```text
1_2
```

means:

```text
event 1 starts at segment start
event 2 starts one base step later
```

For:

```text
1**_2
```

the result is:

```text
event 1 occupies four base steps
one base step is skipped
event 2 starts after five base steps
```

## Built-in Patterns

The pattern selector includes:

- Sparta Remix Chorus
- Alternating 1 and 2
- Two events consecutively
- Four events consecutively
- Four events with skips
- Long first event
- Short second event
- Double first event and single second event

A built-in pattern can be selected or edited.

## Custom Patterns

The script dialog provides:

- a preset selection list;
- a text field for manual pattern entry;
- an Insert button;
- a Cancel button.

Custom pattern examples:

```text
1_2_1_2_
1234
1**_2/
_1__2_3/
11_11_111_1_1_11222_2_222_222_2_
```

## Undo Behavior

Inserted events are created inside a VEGAS undo block:

```csharp
using (UndoBlock undo =
    new UndoBlock("Insert pattern " + pattern))
{
    // Insert events
}
```

All inserted events should therefore be combined into one undo operation.

## Limitations

- Only video events are supported.
- One to four events can be selected.
- A pattern cannot reference more events than are selected.
- `*` and `/` cannot be mixed within one fragment. *(This limitation may be removed in future versions)*
- BPM is read from a project marker named `BPM=value`.
- The script defaults to `120 BPM`.
- The script is intended to run as a single `.cs` file.
