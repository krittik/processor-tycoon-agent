# Action feed icons

Original monochrome artwork for the agent overlay. Every SVG uses a 24 x 24 viewBox, transparent background, white 2-unit strokes, and rounded caps/joins. No fonts, scripts, or external assets. Simple silhouettes are intended for muted gray rendering at 14–18 UI pixels.

| Asset | Meaning |
| --- | --- |
| `search.svg` | Search or discover visible controls; magnifier |
| `read.svg` | Observe or inspect player-accessible UI; open book |
| `chart.svg` | Chart or market-related observation; rising line chart |
| `picture.svg` | Successful game screenshot; landscape picture |
| `edit.svg` | Applied field/value change; pen |
| `click.svg` | Dispatched click; mouse |
| `open.svg` | Open or focus a window; outward arrow in window |
| `close.svg` | Close a window; cross |
| `pause.svg` | Agent execution paused; two bars |
| `connection.svg` | Client session connection; plug |
| `message.svg` | Agent-authored message; speech bubble |
| `error.svg` | Failed action or reported error; circled exclamation |

Keep the accompanying text authoritative: connection is the client session lease, pause is agent execution, and an input dispatch alone does not prove a business outcome. Chart artwork does not imply any chart/gameplay adapter exists.

## Regeneration and Unity integration

From `Modding/assets`, run `npm ci --ignore-scripts --no-audit --no-fund`, then `npm run icons`. The pinned resvg renderer generates 48×48 PNGs beside the source SVGs. The plugin embeds these PNGs and creates tinted sprites at runtime; Node and SVG packages are not needed to run the game.

Treat these SVGs as source artwork; do not assume SVG runtime support. Rasterize offline with an existing SVG renderer to transparent white RGBA PNGs, preferably 48 x 48 or 72 x 72 for UI scaling, then package the PNGs with the plugin. Tint white pixels with the same gray and fading alpha as the feed text. In a Unity project, import PNGs as Sprite (2D and UI), preserve alpha, disable mipmaps and compression for these small UI assets, and use bilinear filtering. A runtime mod can instead load packaged PNG bytes into a texture and draw that texture through its existing UI path. No SVG package is required.

Inspect the rasterized results at actual 14, 16, and 18 pixel display sizes against the game background before treating the icons as visually accepted. These source assets alone do not establish runtime integration or visual acceptance.
