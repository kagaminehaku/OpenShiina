# Shared translations

The translations here are offered by the players (Windows, Linux, Android) to whoever has the
game: the first time a game with a translation here is played, the player asks whether to play
it translated, and downloads the translation chosen. A translation downloaded is updated when
it changes here. Nothing of a game is here: only the translation's tables, which hold the
original lines beside the translated ones, as OpenShiina Tools makes them.

## A translation's folder

One folder for each game (any name), one folder for each of its translations in it:

```
translations/
  Raillore no Ryakudatsusha/
    vi/
      info.json
      text.tsv
      names.tsv
      strings.tsv
```

The tables are the files of a game's `translation` folder (docs/tools.md, "Translation"): make
and fill them in OpenShiina Tools' Translation tab, then copy them here. Any other file of that
folder is downloaded with them.

`info.json` says what the translation is:

```json
{
  "game": "Raillore no Ryakudatsusha",
  "language": "vi",
  "title": "Tiếng Việt",
  "authors": ["your name"],
  "kind": "human",
  "progress": 100,
  "notes": "",
  "updated": "2026-11-28"
}
```

- `game`: the game's name as OpenShiina knows it (the name in its library and in
  `src/OpenShiina.Core/Data/Formats.Json`). A translation is offered for this game only.
- `language`: the language's code (`vi`, `en`, `zh`...); `title`: its name as its readers say it.
- `kind`: `human`, `machine`, or `machine+edited`. `progress`: how much of the game is
  translated, in percent. `notes`: anything players should know (which release of the game it is
  for, what is not translated yet). `updated`: the day of the last change.

## Adding or updating one

Open a pull request with the folder. Before, play it in OpenShiina Tools (▶ Play in the
Translation tab), and check the tab's summary: no line of the game left out of text.tsv, no row
the game no longer has, no letter without a code. A translation is for one release of a game:
say in `notes` which one when the game has several.

The player finds a translation by this folder's tree on GitHub, so a merged change reaches the
players without a new release of OpenShiina.
