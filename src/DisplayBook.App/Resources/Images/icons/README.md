# Top-bar action icons

Material Symbols (outlined, 24dp) from
[google/material-design-icons](https://github.com/google/material-design-icons),
licensed under Apache-2.0. Each file is the unmodified upstream `24px.svg`.

These are **source** assets, not the ones XAML references. `DisplayBook.App.csproj`
builds each file twice with a different `TintColor`, producing
`icon_<name>_light.png` and `icon_<name>_dark.png`; XAML picks between the pair with
`AppThemeBinding`. The `Resources\Images\*` glob in the csproj is single-star, so it
does not pick these up a second time.

To add an icon:

1. Drop the upstream `24px.svg` here as `ic_<name>.svg`.
2. Add the two `MauiImage` entries (light `#236B5A`, dark `#66E0C1`) to the csproj.
3. Reference it as `icon_<name>_light.png` / `icon_<name>_dark.png`, and set
   `SemanticProperties.Description` on the button — an icon has no text for a screen
   reader to announce.
