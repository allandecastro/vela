# Vela — Design System

> **Build Dataverse with confidence.**

The visual identity for **Vela**, a framework + CLI to build Microsoft Dataverse
extensions (plugins, Custom APIs — and client-side later) with a safe,
preview-first `plan` → `apply` workflow.

---

## 1. Name

**Vela** — Latin / Italian / Spanish for *"the sails"*, and a southern-hemisphere
**constellation** carved out of the ship *Argo* (Jason's vessel).

The story it tells: **set your course, catch the wind, sail forward** — a natural fit
for a tool that takes you from `plan` to `apply`. The name is abstract and timeless: it
depends on no product terminology, so it ages well as the project grows beyond plugins
(server-side) into client-side surfaces.

- Wordmark is always lowercase: **vela**
- Never "Vela Framework" / "Dataverse Vela" — Vela is the brand; "for Dataverse" is descriptive.

## 2. Tagline

| Use | Text |
| --- | --- |
| **Slogan** (brand) | Build Dataverse with confidence |
| **Description** (NuGet / repo subtitle) | Modern Dataverse development — plugins, Custom APIs & more |

## 3. Logo

The symbol is a **chevron**: a sail filled by the wind that also reads as an **upward
arrow** ("apply", "ship", "move forward").

| Asset | File | Use |
| --- | --- | --- |
| Symbol — gradient | [`vela-chevron.svg`](./vela-chevron.svg) | primary mark |
| Symbol — mono black | [`vela-chevron-black.svg`](./vela-chevron-black.svg) | on light backgrounds |
| Symbol — mono white | [`vela-chevron-white.svg`](./vela-chevron-white.svg) | on dark / colored backgrounds |
| App tile | [`vela-tile.svg`](./vela-tile.svg) | GitHub org avatar, NuGet icon |
| Favicon | [`vela-favicon.svg`](./vela-favicon.svg) | browser tab (legible at 16px) |

PNG renders of every asset live in [`./png/`](./png/) — open them to preview.

### Clear space & sizing
- Keep clear space around the symbol equal to **half the chevron height**.
- Minimum symbol size: **16px** (favicon validated at this size).
- On busy / photographic backgrounds, use the **mono white** symbol or the **tile**.

### Don'ts
- Don't recolor the gradient outside the brand palette.
- Don't stretch, rotate, or add effects (shadows, bevels) to the symbol.
- Don't recreate the wordmark in another typeface.
- The mark is **flat** — a single filled chevron, no inner fold/shading. Keep it identical
  across every size and asset (chevron, tile, favicon); rounded-corner radius is **~22%**
  of the icon size everywhere.

## 4. Color palette

| Role | Name | Hex |
| --- | --- | --- |
| Primary | Indigo | `#6366F1` |
| Primary | Cyan | `#22D3EE` |
| Brand gradient | Indigo → Cyan | `#6366F1` → `#22D3EE` (135°) |
| Text | Ink | `#1E293B` |
| Dark background | Night | `#0B1120` |
| Dark background alt | Night 2 | `#111A33` |
| Secondary text | Slate | `#94A3B8` |
| Hairlines / borders | Line | `#1E293B` |

The **brand gradient** (135°, indigo → cyan) is the signature: sky → sea, the world Vela
sails through. Use it for the symbol and accent details, not for large surfaces.

## 5. Typography

- **Typeface:** Inter (fallbacks: Segoe UI, Helvetica, Arial, sans-serif).
- **Wordmark:** Inter 700, lowercase, letter-spacing ~`1px`.
- **Tagline / accents:** Inter 500–600.
- **Body:** Inter 400.

## 6. Voice
- Confident, calm, precise. Nautical metaphors are welcome but never forced
  (*set your course, chart, ship, on course*).
- Always describe Dataverse compatibility **descriptively** ("for Microsoft Dataverse"),
  never as part of the brand name — Vela is not affiliated with Microsoft.

## 7. Asset map

```
branding/
  DESIGN_SYSTEM.md      ← this file
  vela-chevron.svg        vela-chevron-black.svg   vela-chevron-white.svg
  vela-tile.svg           vela-favicon.svg
  png/                  ← PNG renders of all of the above
```

---

## Package naming (brand-first)

Packages are brand-first under `Vela.*` so the prefix can be reserved on nuget.org and the
brand scales to client-side:

```
Vela.Plugins      # runtime, contracts, primitives        (was Dataverse.Framework.Core)
Vela.Cli       # command-line tool — command: `vela`    (was Dataverse.Framework.Tools)
@vela/client, @vela/pcf, @vela/cli   # npm, client-side (later)
```

The CLI is `Vela.Cli` (not `.Tools`/`.Tool`): clearest for users, future-proof as the CLI
grows to cover client-side, and symmetric with the eventual npm `@vela/cli`.

"Dataverse" is intentionally **dropped from the package ID** (brand-first, like
`Serilog.*` / `MediatR`). Discoverability on nuget.org is carried by the package
**Title, Description and Tags** instead:

```xml
<PackageTags>dataverse;dynamics365;power-platform;plugin;custom-api;xrm;cli</PackageTags>
```
