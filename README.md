# Chorus Generator for VEGAS Pro

A VEGAS Pro Script API utility for copying selected audio and video fragments according to a rhythmic pattern.

## Releases

The latest stable release is **0.8.0**.

Download it from the [Chorus Generator release page](https://github.com/diedokmedia/chorus-generator/releases).

## Project Structure

```text
chorus-generator/
├── chorus-generator.cs
└── README.md
```

## Installation

1. Open `chorus-generator.cs` in VEGAS Script Editor.
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
- 1 to 35 selected media fragments;
- audio events;
- video events;
- combined audio and video selections;
- audio-only or video-only selections.

When only audio or only video is selected, the script includes all audio and video events overlapping the selected time range.

Each fragment is identified by the order of its start position. If more than 9 fragments are selected, the numbering continues alphabetically:

```text
1-9  = fragments 1 through 9
A-Z  = fragments 10 through 35 (case-insensitive)
```

## BPM

The script reads BPM directly from the VEGAS project ruler (`vegas.Project.Ruler.BeatsPerMinute`). 

If the ruler returns an invalid value (less than or equal to zero), the script automatically falls back to the default **120 BPM**.

The grid calculation assumes 4/4 time signature:
```text
quarter duration = 60 / BPM
bar duration     = quarter duration × 4
base grid step   = bar duration / 16
```

At `120 BPM`, one base grid step is approximately `0.125 seconds`.

## Pattern Syntax

| Symbol | Meaning |
|---|---|
| `1`–`9`, `A`–`Z` | Copy the corresponding media fragment (1–35) |
| `_` or `0` | Skip one base grid step |
| `*` | Double the fragment duration |
| `/` | Halve the fragment duration |
| `(...)` | Grouping for regex repetition |
| `{n}` | Repeat the preceding atom or group exactly `n` times |

Modifiers may be repeated and combined:
```text
1*      2× duration
1**     4× duration
1/      1/2 duration
1*/     ×1 (double, then halve)
```

## Regex-like Expansion

The script supports a deterministic subset of regular expressions for pattern compression. This allows you to create complex rhythms much faster.

- Grouping `(...)` and exact repetition `{n}` are supported.
- Nested groups are fully supported (e.g., `((1*2){2}){2}`).
- Non-deterministic regex operators (`|`, `?`, `+`, `{n,m}`, `[...]`) are **not supported** and will throw an error, as they cannot be reliably mapped to a timeline.

**Examples:**
```text
(12){4}         -> 12121212
1{8}            -> 11111111
(1_2_){2}       -> 1_2_1_2_
(12*){2}        -> 12*12*
((1*){2}(1){2}) -> 1*1*11
```

## Generation Location

The script provides two ways to insert the generated pattern, selectable in the UI dropdown menu:

1. **Replace selected track event(s):** 
   The pattern is generated starting exactly at the beginning of the first selected fragment. After generation, the original selected media fragments are **removed** from the timeline.
2. **At cursor position (keep sources):** 
   The pattern is generated starting from the current timeline cursor (`vegas.Transport.CursorPosition`). The original selected media fragments are **preserved** on the timeline.

## Duration Calculation

The effective duration multiplier is calculated as:
```text
duration = base grid step × 2^(asteriskCount - slashCount)
```

Examples:
```text
1*     ×2
1/     ×0.5
1*/    ×1
1**/   ×2
```

## Pattern Examples

```text
1_2_
12
1234
1_2_3_4_
(12){4}_
1**_2/
(1_2_){2}3*
11_11_111_1_1_11222_2_222_222_2_
```

Built-in presets include:
- Alternate
- Consecutive
- Four fragments
- Regex demos (e.g., `(12){4}`)
- Various Sparta Remix patterns

## Media Copying

A fragment copy includes every overlapping event:
- all overlapping video events;
- all overlapping audio events;
- the source track of every event;
- the relative timeline position inside the fragment.

Each copy preserves the relative audio and video positions. Copies are automatically selected after insertion.

## Undo Behavior

Insert and optional source-removal operations are wrapped in a single `UndoBlock`. The complete operation (generation and source removal) can be undone in one step.

## Safety Limits

To prevent the script from freezing VEGAS due to memory or processing overflow:
- Maximum repetition count for `{n}` is **10,000**.
- Maximum expanded pattern length is **1,000,000** symbols.

## Limitations

- Supports up to 35 media fragments.
- Audio and video events must overlap in time to be grouped into the same fragment.
- `*` and `/` may be mixed and are applied in written order.
- Non-deterministic regex features (alternation, wildcards, ranges) are not supported.

