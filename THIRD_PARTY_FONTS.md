# Third-party fonts

Fonts bundled with WagTerm and their licenses. The installer copies the font and
its license text from [`assets/fonts/`](assets/fonts/) next to `wagterm.exe`.

| Project | Version | URL | License | Used as | Modified? |
|---|---|---|---|---|---|
| Meslo LGL DZ Nerd Font | NF release (bundled) | https://github.com/ryanoasis/nerd-fonts | OFL 1.1 (Meslo: Apache 2.0) | `MesloLGLDZNerdFont-Regular.ttf` - default TrueType face | no |

Notes:
- Nerd Fonts patched fonts aggregate glyphs from several icon projects (Font
  Awesome, Devicons, Octicons, Codicons, Powerline Extra, Seti-UI, Weather, Font
  Logos, Pomicons); licensing/attribution per set is documented in the respective
  Nerd Fonts release.
- SIL OFL 1.1 permits redistribution and format conversion (including
  rasterization) provided the fonts are not sold standalone; WagTerm complies.

The upstream lite font catalog and bitmap-font generator are not part of WagTerm.
See the [historical bitmap-font report](docs/agwin-bitmap-report.md) for context.
