# Rule of Rose — Text Translator

A C# toolkit for translating the text of **Rule of Rose** (PlayStation 2, `SLUS_214.48`).

Most of the game's text does not live in a separate text file: it sits inside
`ELFR101` code modules packed in `.ARA` archives and is referenced directly by
MIPS instructions. This toolkit scans those modules, gives you an editable JSON
file, and rebuilds the archive from the **original** file with every reference
re-pointed — so a translation may be **longer** than the original line.

> Fan-made tool. It ships no game data and is not affiliated with or endorsed
> by the game's developers or publishers.

## Features

- **Archive-aware** — parses the RTPK container and locates the ELFR101 module
  instead of relying on fixed offsets.
- **Finds the text for you** — discovers strings referenced by `LUI`/`ADDIU`
  pairs and by GP-relative `ADDIU`, including Shift-JIS and in-game markup codes.
- **Longer translations supported** — changed lines are moved into a new pool
  and every referencing instruction is re-patched to follow them. A reference
  that used `$gp` keeps using `$gp` (gp itself is left at its original value);
  the tool never rewrites a reference to `$at`, because the game does not read
  text reached that way.
- **Safe rebuilds** — output is always built from the original archive, and a
  JSON that belongs to a different archive is rejected instead of silently
  corrupting the module.
- **Batch mode** — export every `.ARA` under a folder in one command.
- **Desktop editor** — a Windows GUI with search, an "untranslated only" filter,
  and one-click JSON save / archive build.

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows for the GUI (`RoRTranslator`). The CLI and core library are
  cross-platform.

## Build

```sh
dotnet build RuleOfRose.slnx -c Release
```

| Project | Output |
|---|---|
| `src/RuleOfRose.Cli` | `RoRText` (command line) |
| `src/RuleOfRose.Translator` | `RoRTranslator` (Windows GUI) |
| `src/RuleOfRose.Core` | `RuleOfRose.Core.dll` (library used by both) |

## Quick start

```sh
# 1. Extract the text
RoRText export "seq/w001/a164/a164.ARA" a164.text.json

# 2. Translate the "translation" fields in a164.text.json

# 3. Check your work
RoRText verify "seq/w001/a164/a164.ARA" a164.text.json

# 4. Build the patched archive — works from either the original .ARA or one
#    already patched (re-patch: pass the modified .ARA as input).
RoRText import "seq/w001/a164/a164.ARA" a164.text.json a164.translated.ARA
```

To pull the text out of the whole game at once:

```sh
RoRText export-dir "path/to/seq" "path/to/exported"
```

## CLI

| Command | Description |
|---|---|
| `export <ara> <json> [--merge <old.json>]` | Scan an archive and write a translation JSON. `--merge` carries your earlier translations over (same archive only). |
| `import <ara> <json> <output>` | Rebuild the archive with the translations from a JSON. |
| `scan <ara>` | Show the module, the entries found, and the remaining text budget. |
| `verify <ara> <json>` | Re-scan the archive and validate a JSON against it. |
| `scan-dir <root>` | Report every `.ARA` under a folder. |
| `export-dir <root> <outdir>` | Export one JSON per archive, mirroring the folder tree. |

`import` asks before overwriting an existing file. It works from either the
original .ARA or one already patched: entries are matched by `id`, so the same
JSON keeps working after you rebuild. A JSON in the older v2 layout (its `id`s
were text offsets) is matched by export order instead.

## GUI

```sh
RoRTranslator [path/to/a164.ARA]
```

1. **Open .ARA** — load the *original* archive.
2. Pick an entry and type your text in the **Translation** pane.
3. **Save JSON** — write the translation file.
4. **Build .ARA** — write the patched archive.

The entry list marks translated rows with `[+]`.

| Shortcut | Action |
|---|---|
| `Ctrl+S` | Save JSON |
| `Ctrl+F` | Focus the filter box |
| `F4` | Jump to the next untranslated entry |
| `Alt+Up` / `Alt+Down` | Move the selection |

Scanning, loading, and building run in the background, so the window stays
responsive. Closing it with unsaved edits writes the JSON next to the archive.

## Writing a translation

- Edit only the `translation` field of each entry — everything else in the JSON
  is generated for you. Entries you leave alone keep their original text.
- An entry's `id` points at the instruction that references the text, not at
  the text itself, so it stays the same after the archive is rebuilt. Rebuild
  as often as you like and keep using the same JSON.
- Keep the `#R` / `#F` prefix; without it the scanner and the game's text
  renderer cannot read the line, and `verify` / `import` will reject it.
- Use `#n` for a line break. Control characters below `0x20` are not allowed;
  Shift-JIS and markup such as `#K003` pass through as ordinary characters.
- Longer is fine: the text is moved to a new pool and the references are
  re-patched for you. Text addressed through `$gp` stays `$gp` (gp itself is
  kept at its original value); the tool never rewrites a reference to `$at`,
  because the game's text reader cannot follow that form.
- Rebuild from the **original** .ARA whenever you can. Archives produced by
  older builds may still contain `$at` references; importing them repairs the
  lines you translated, but a fresh build from the original fixes all of them.

## Before you share a translation

- A patched archive has **not** been verified on real hardware or an emulator.
  Always test in PCSX2 or on a PS2 first, and keep a backup of your original
  game data.
- Only text that the module code actually references is exported — text reached
  through pointers the scanner does not model is not included.
- `import` re-resolves every reference in the rebuilt module and refuses to
  write a file whose references no longer land on the expected text; it cannot
  prove that the result plays correctly in-game.

## Legal

Rule of Rose and its assets are the property of their respective rights holders.
This repository contains tooling only — no game files are included. Use it only
with a copy of the game you legally own.
