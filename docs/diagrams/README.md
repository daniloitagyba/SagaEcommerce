# Architecture diagram generator

`docs/images/architecture-dracula.png` is generated, not hand-drawn. This
directory is its source of truth - before this was committed, the
generator only ever lived in a scratch directory and was one editing
session away from being lost for good.

## Pipeline

1. `python3 generate.py` reads the brand icon paths from `icons.py` and
   writes `diagram.html` - a self-contained, absolutely-positioned HTML/SVG
   page (2000x1384px, Dracula palette) with no external requests or fonts
   required to render correctly (falls back from `JetBrains Mono` to
   `Menlo`/`Consolas`/monospace).
2. Open `diagram.html` in a browser at a 2000x1384 viewport and take a
   full-page screenshot. Any headless browser works (Chrome DevTools
   "Capture screenshot" in device-toolbar mode, Playwright, `wkhtmltoimage`,
   etc.) - the only requirement is a viewport that exactly matches `W, H` at
   the top of `generate.py`, so nothing gets clipped or scaled.
3. Save the screenshot as `docs/images/architecture-dracula.png`.

Regenerating `diagram.html` from an unmodified `generate.py`/`icons.py` is
deterministic - no timestamps, no randomness - so a diff against a
previously-generated copy is a reliable way to confirm nothing drifted.

## Editing the diagram

Every box, arrow, and panel is plain data in `generate.py`: positions,
colors, and label text are Python literals, not something derived from the
running system. To add a service or change a connection, edit the
`services`/`svc_positions` list, the relevant `box()`/`panel()`/`arrow()`
call, or the `GLYPHS`/`ICONS` dict, then re-run step 1 and re-screenshot.

## Icon attribution

`icons.py` holds single-`<path>` SVG shapes for third-party brand marks
(MongoDB, PostgreSQL, Redis, Kafka, Keycloak, Linkerd, Prometheus, Grafana,
OpenTelemetry, Argo), sourced from the
[Simple Icons](https://simpleicons.org/) project (CC0 1.0) and used here
purely to identify the corresponding technology in the diagram, the same
way a README badge or a slide deck would. This project's own services use
plain monoline glyphs defined directly in `generate.py` (`GLYPHS`) - no
official logo applies to them.
