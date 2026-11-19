# Chorus Generator for VEGAS Pro

A VEGAS Pro Script API utility for copying selected audio and video fragments according to a rhythmic pattern.

## Releases

The latest stable release is **0.7.0**.

Download it from the [Chorus Generator 0.7.0 release page](https://github.com/diedokmedia/chorus-generator/releases/tag/0.5.0).

## Project Structure

```text
chorus-generator/
├── chorus-generator.cs
└── README.md
```

## Installation

1. Open `src/chorus-generator.cs` in VEGAS Script Editor.
2. Ensure `ScriptPortal.Vegas.dll` is available.
3. Save the script.
4. Run it through **Tools → Scripting**.

Required assemblies:

```text
System.dll
System.Drawing.dll
System.Windows.Forms.dll
ScriptPortal.Vegas.dll
```

## Supported Selection

The script supports:

- one to four selected media fragments;
- audio events;
- video events;
- combined audio and video selections;
- audio-only or video-only selections.

When only audio or only video is selected, the script includes all audio and video events overlapping the selected time range.

Each fragment is identified by the order of its start position:

```text
1 = first fragment
2 = second fragment
3 = third fragment
4 = fourth fragment
```

## BPM

Add a project marker named:

```text
BPM=120
```

Examples:

```text
BPM=128
BPM=140.5
BPM=180
```

If no valid BPM marker is found, an input dialog allows entering BPM manually.

The grid calculation assumes 4/4:

```text
quarter duration = 60 / BPM
bar duration     = quarter duration × 4
base grid step   = bar duration / 16
```

At `120 BPM`, one base grid step is approximately:

```text
0.125 seconds
```

## Pattern Syntax

| Symbol | Meaning |
|---|---|
| `1`–`9` | Copy the corresponding media fragment |
| `_` | Skip one base grid step |
| `*` | Double the fragment duration |
| `/` | Halve the fragment duration |

Modifiers may be repeated:

```text
1*      2× duration
1**     4× duration
1***    8× duration
1/      1/2 duration
1//     1/4 duration
1///    1/8 duration
```

`*` and `/` may be combined inside one fragment.

Each modifier is applied sequentially:

```text
1*/
```

means:

```text
double, then halve
```

Thus, `1*/` has the effective multiplier `1`, while `1**/` has the multiplier `2`.

## Pattern Grammar

```text
pattern  ::= item+

item     ::= "_" | fragment

fragment ::= fragmentNumber modifier*

fragmentNumber ::= "1" | "2" | "3" | "4"

modifier ::= "*" | "/"
```

Rules:

- A pattern cannot be empty.
- Valid symbols are `1-9`, `_`, `*`, and `/`.
- A fragment number cannot exceed the number of detected fragments.
- Consecutive `_` symbols represent multiple skipped steps.
- `*` and `/` may be combined in any order.
- Modifiers only follow a fragment number.

## Regular Expressions

Complete-pattern validation:

```regex
^(?:_|[1-9][*/]*)+$
```

Fragment token extraction:

```regex
[1-9][*/]*|_
```
*In the future, it is possible to support regular expressions from the user, but this will be in the distant future.*

Example:

```text
1*/_2**/__
```

is parsed as:

```text
1*/
_
2**/
_
_
```

The regular expression validates syntax. The script additionally verifies that the referenced fragment exists:

```csharp
fragmentNumber <= segments.Count
```

## Duration Calculation

The effective modifier is calculated as:

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
1*     ×2
1**    ×4
1/     ×0.5
1//    ×0.25
1*/    ×1
1**/   ×2
1/*/   ×1
1//**  ×1
```

Example pattern:

```text
1**/_2/
```

is interpreted as:

```text
1** = four times the base duration
_   = skip one base step
2/  = half the base duration
```

## Pattern Examples

```text
1_2_
12
1234
1_2_3_4_
1**_2/
1*/_2/**
__1___2_
11_11_111_1_1_11222_2_222_222_2_
```

Built-in presets include:

- Alternate
- Consecutive
- Four fragments
- Four fragments with skips
- Sparta Remix Chorus

## Media Copying

A fragment copy includes every overlapping event:

- all overlapping video events;
- all overlapping audio events;
- the source track of every event;
- the relative timeline position inside the fragment.

If two fragments are selected:

```text
fragment 1: audio + video
fragment 2: audio + video
```

then:

```text
1_2_
```

copies:

```text
fragment 1
skip
fragment 2
skip
```

Each copy preserves the relative audio and video positions.

## Source Removal

After copying, the original source events are removed.

The copies remain on the timeline, and source removal is performed inside the VEGAS `UndoBlock`. The complete operation can be undone in one step.

## BPM and Ruler Settings

The script reads BPM directly from the VEGAS project ruler:

vegas.Project.Ruler.BeatsPerMinute
Cancelling the dialog stops the script.

## Pattern Dialog

The dialog supports:

- built-in patterns;
- manually entered patterns;
- Insert and Cancel actions.

Custom pattern example:

```text
1**/_2*__3//_4
```

## Undo Behavior

Insert and source-removal operations are wrapped in:

```csharp
using (UndoBlock undo =
    new UndoBlock("Insert media pattern " + pattern))
{
    // Copy media fragments
    // Remove source events
}
```

This creates a single undo operation.

## Limitations

- Supports one to four media fragments.
- Supports audio and video events.
- Fragment numbering follows left-to-right timeline position.
- Audio and video events must overlap for them to belong to the same fragment.
- `*` and `/` may be mixed and are applied in written order.
- BPM is read from `BPM=value`; otherwise manual BPM input is requested.
- Source events are removed after copying.