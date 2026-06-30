# vela — Brand & Design System

> **Build Dataverse with confidence.**
> Modern Dataverse development — plugins, Custom APIs & more.

*Brand & Design System · v1.0*

---

**Vela** is an **AI-ready** framework and CLI for building Microsoft Dataverse
extensions — plugins, Custom APIs, and client-side surfaces later — through a safe,
preview-first `plan` → `apply` workflow. This document is the single source of truth for
the brand.

> **AI-ready by design**
> Vela gives humans and coding agents the same firm ground to build on: a
> **strongly-typed** framework with predictable conventions, a CLI that **scaffolds and
> generates** plugins & Custom APIs, and a previewable `plan` → `apply` diff before
> anything ships.
>
> `Strongly typed` · `CLI scaffolding & generators` · `Predictable conventions` ·
> `Previewable plan → apply` · `Agent-legible contracts`

---

## 01 · Name

**Vela** — Latin / Italian / Spanish for *"the sails"*, and a southern-hemisphere
**constellation** carved out of the ship *Argo* (Jason's vessel).

The story it tells: **set your course, catch the wind, sail forward** — a natural fit
for a tool that takes you from `plan` to `apply`. The name is abstract and timeless: it
depends on no product terminology, so it ages well as the project grows beyond plugins
(server-side) into client-side surfaces.

- **Always** — wordmark is always lowercase: **vela**.
- **Never** — "Vela Framework" or "Dataverse Vela." Vela is the brand; "for Dataverse" is descriptive.

## 02 · Tagline

| Use | Text |
| --- | --- |
| **Slogan** (brand) | Build Dataverse with confidence |
| **Description** (NuGet / repo subtitle) | Modern Dataverse development — plugins, Custom APIs & more |

## 03 · Logo

The symbol is a **chevron**: a sail filled by the wind that also reads as an **upward
arrow** ("apply", "ship", "move forward"). The mark is **flat** — a single filled
chevron, no inner fold or shading, identical across every size and asset.

### Symbol

| Asset | File | Use |
| --- | --- | --- |
| Symbol — gradient | [`vela-chevron.svg`](./vela-chevron.svg) | primary mark |
| Symbol — mono black | [`vela-chevron-black.svg`](./vela-chevron-black.svg) | on light backgrounds (`#0F172A`) |
| Symbol — mono white | [`vela-chevron-white.svg`](./vela-chevron-white.svg) | on dark / colored backgrounds |
| App tile | [`vela-tile.svg`](./vela-tile.svg) | GitHub org avatar, NuGet icon |
| Favicon | [`vela-favicon.svg`](./vela-favicon.svg) | browser tab (legible at 16px) |

**Sticker / die-cut variants** carry a white knock-out border for print and physical
stickers — use the clean symbols above for digital surfaces:

| Asset | File |
| --- | --- |
| Chevron — gradient, die-cut | [`vela-chevron-diecut.svg`](./vela-chevron-diecut.svg) |
| Chevron — mono ink, die-cut | [`vela-chevron-black-diecut.svg`](./vela-chevron-black-diecut.svg) |
| App tile — die-cut | [`vela-tile-diecut.svg`](./vela-tile-diecut.svg) |

### Wordmark

The logotype **vela** (lowercase). Pick the fill that fits the background:

| Asset | File | Use |
| --- | --- | --- |
| Wordmark — gradient | [`vela-wordmark-gradient.svg`](./vela-wordmark-gradient.svg) | on white / light |
| Wordmark — indigo | [`vela-wordmark-indigo.svg`](./vela-wordmark-indigo.svg) | on white / light |
| Wordmark — ink | [`vela-wordmark-ink.svg`](./vela-wordmark-ink.svg) | on white / light, neutral |
| Wordmark — white on night | [`vela-wordmark-white-on-night.svg`](./vela-wordmark-white-on-night.svg) | on dark surfaces |
| Wordmark — pill | [`vela-wordmark-pill.svg`](./vela-wordmark-pill.svg) | dark pill badge, e.g. avatars |

### Lockup

Symbol stacked above the wordmark — the formal logo for cards, splash and docs headers:

| Asset | File | Use |
| --- | --- | --- |
| Lockup — gradient | [`vela-lockup-gradient.svg`](./vela-lockup-gradient.svg) | on white / light |
| Lockup — indigo | [`vela-lockup-indigo.svg`](./vela-lockup-indigo.svg) | on white / light |
| Lockup — ink | [`vela-lockup-ink.svg`](./vela-lockup-ink.svg) | on white / light, neutral |
| Lockup — white on night | [`vela-lockup-white-on-night.svg`](./vela-lockup-white-on-night.svg) | on dark surfaces |

### Slogan & cards

Tagline treatments and social/share cards built from the marks above:

| Asset | File | Use |
| --- | --- | --- |
| Slogan — badge | [`vela-slogan-badge.svg`](./vela-slogan-badge.svg) | round "set your course" badge |
| Slogan — banner, dark | [`vela-slogan-banner-dark.svg`](./vela-slogan-banner-dark.svg) | tagline strip on night |
| Slogan — banner, gradient | [`vela-slogan-banner-gradient.svg`](./vela-slogan-banner-gradient.svg) | tagline strip on gradient |
| Slogan — banner, white | [`vela-slogan-white.svg`](./vela-slogan-white.svg) | tagline strip on white |
| Card — dark | [`vela-card-dark.svg`](./vela-card-dark.svg) | share / OG card on night |
| Card — white | [`vela-card-white.svg`](./vela-card-white.svg) | share / OG card on white |
| Card — gradient | [`vela-card-gradient.svg`](./vela-card-gradient.svg) | share / OG card on gradient |

PNG renders of the referenced web/app assets live in [`./png/`](./png/) — open them to
preview.

### Clear space & sizing
- Keep clear space around the symbol equal to **half the chevron height**.
- Minimum symbol size: **16px** (favicon validated at this size).
- Rounded-corner radius is **~22%** of the icon everywhere.
- On busy / photographic backgrounds, use the **mono white** symbol or the **tile**.

### Don'ts
- ✕ Don't recolor the gradient outside the brand palette.
- ✕ Don't stretch, rotate, or add shadows / bevels to the symbol.
- ✕ Don't recreate the wordmark in another typeface.
- ✕ Don't add an inner fold or shading — the mark stays flat, identical across chevron,
  tile and favicon.

## 04 · Color palette

The **brand gradient** — 135°, indigo → cyan — is the signature: sky → sea, the world
Vela sails through. Use it for the symbol and accent details, never for large surfaces.

**Brand gradient · 135°** — `#6366F1` → `#22D3EE`

| Token | Role | Hex |
| --- | --- | --- |
| Indigo | Brand | `#6366F1` |
| Cyan | Brand | `#22D3EE` |
| Ink | Text | `#1E293B` |
| Slate | Secondary text | `#94A3B8` |
| Night | Dark background | `#0B1120` |
| Night 2 | Alt dark background | `#111A33` |
| Line | Borders | `#1E293B` |
| Paper | Light background | `#FFFFFF` |

## 05 · Typography

**Inter** — fallbacks: Segoe UI, Helvetica, Arial, sans-serif.

- **Wordmark** — Inter **700**, lowercase, tracking ~`1px`.
- **Tagline / accents** — Inter **500–600**.
- **Body** — Inter **400**.

## 06 · Voice
- **Confident, calm, precise.** Nautical metaphors are welcome but never forced
  (*set your course, chart, ship, on course*).
- **Descriptive about Dataverse.** Always "for Microsoft Dataverse," never part of the
  brand name — Vela is not affiliated with Microsoft.

## 07 · Asset map

```
branding/
  DESIGN_SYSTEM.md      ← this file

  # symbol (clean, for digital)
  vela-chevron.svg        vela-chevron-black.svg   vela-chevron-white.svg
  vela-tile.svg           vela-favicon.svg

  # symbol (die-cut, for stickers / print)
  vela-chevron-diecut.svg vela-chevron-black-diecut.svg  vela-tile-diecut.svg

  # wordmark
  vela-wordmark-gradient.svg   vela-wordmark-indigo.svg   vela-wordmark-ink.svg
  vela-wordmark-white-on-night.svg   vela-wordmark-pill.svg

  # lockup (symbol + wordmark)
  vela-lockup-gradient.svg  vela-lockup-indigo.svg
  vela-lockup-ink.svg       vela-lockup-white-on-night.svg

  # slogan & cards
  vela-slogan-badge.svg          vela-slogan-banner-dark.svg
  vela-slogan-banner-gradient.svg  vela-slogan-white.svg
  vela-card-dark.svg   vela-card-white.svg   vela-card-gradient.svg

  png/                  ← PNG renders of the referenced web/app assets
```

All marks share one canonical chevron path (`M32 8 L54 50 L40 50 L32 32 L24 50 L10 50 Z`)
and the brand palette, so the families stay pixel-consistent across every size.

## 08 · Package naming

Packages are brand-first under `Vela.*` so the prefix can be reserved on nuget.org and the
brand scales to client-side. "Dataverse" is intentionally dropped from the ID — like
`Serilog.*` or `MediatR` — discoverability is carried by Title, Description and Tags.

```
Vela.Plugins      # runtime, contracts, primitives
Vela.Cli          # command-line tool — command: `vela`
@vela/client, @vela/pcf, @vela/cli   # npm, client-side (later)
```

```xml
<PackageTags>dataverse;dynamics365;power-platform;plugin;custom-api;xrm;cli</PackageTags>
```

---

*vela — Build Dataverse with confidence.*
