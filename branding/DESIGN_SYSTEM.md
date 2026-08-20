# vela - Brand & Design System

> **Build Dataverse with confidence.**
> Modern Dataverse development - plugins, Custom APIs & more.

*Brand & Design System · v1.0*

---

**Vela** is an **AI-ready** framework and CLI for building Microsoft Dataverse
extensions - plugins, Custom APIs, and client-side surfaces later - through a safe,
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

**Vela** - Latin / Italian / Spanish for *"the sails"*, and a southern-hemisphere
**constellation** carved out of the ship *Argo* (Jason's vessel).

The story it tells: **set your course, catch the wind, sail forward** - a natural fit
for a tool that takes you from `plan` to `apply`. The name is abstract and timeless: it
depends on no product terminology, so it ages well as the project grows beyond plugins
(server-side) into client-side surfaces.

- **Always** - wordmark is always lowercase: **vela**.
- **Never** - "Vela Framework" or "Dataverse Vela." Vela is the brand; "for Dataverse" is descriptive.

## 02 · Tagline

| Use | Text |
| --- | --- |
| **Slogan** (brand) | Build Dataverse with confidence |
| **Description** (NuGet / repo subtitle) | Modern Dataverse development - plugins, Custom APIs & more |

## 03 · Logo

The symbol is a **chevron**: a sail filled by the wind that also reads as an **upward
arrow** ("apply", "ship", "move forward"). The mark is **flat** - a single filled
chevron, no inner fold or shading, identical across every size and asset.

### Symbol

| Asset | File | Use |
| --- | --- | --- |
| Symbol - gradient | [`symbol/vela-chevron.svg`](./symbol/vela-chevron.svg) | primary mark |
| Symbol - mono black | [`symbol/vela-chevron-black.svg`](./symbol/vela-chevron-black.svg) | on light backgrounds |
| Symbol - mono white | [`symbol/vela-chevron-white.svg`](./symbol/vela-chevron-white.svg) | on dark / colored backgrounds |
| Symbol - mono badge | [`symbol/vela-chevron-mono.svg`](./symbol/vela-chevron-mono.svg) | single-color outline badge |
| App tile | [`symbol/vela-tile.svg`](./symbol/vela-tile.svg) | GitHub org avatar, NuGet icon |
| Favicon | [`symbol/vela-favicon.svg`](./symbol/vela-favicon.svg) | browser tab (legible at 16px) |

### Wordmark

The logotype **vela** (lowercase). Pick the fill that fits the background:

| Asset | File | Use |
| --- | --- | --- |
| Wordmark - indigo | [`wordmark/vela-wordmark-indigo.svg`](./wordmark/vela-wordmark-indigo.svg) | on white / light |
| Wordmark - ink | [`wordmark/vela-wordmark-ink.svg`](./wordmark/vela-wordmark-ink.svg) | on white / light, neutral |
| Wordmark - white | [`wordmark/vela-wordmark-white.svg`](./wordmark/vela-wordmark-white.svg) | on dark surfaces |

### Lockup

Symbol beside the wordmark - the formal logo for cards, splash and docs headers:

| Asset | File | Use |
| --- | --- | --- |
| Lockup - indigo | [`lockup/vela-lockup-indigo.svg`](./lockup/vela-lockup-indigo.svg) | on white / light |
| Lockup - ink | [`lockup/vela-lockup-ink.svg`](./lockup/vela-lockup-ink.svg) | on white / light, neutral |
| Lockup - white | [`lockup/vela-lockup-white.svg`](./lockup/vela-lockup-white.svg) | on dark surfaces |

### Stickers (die-cut, print)

Print-ready die-cut variants - symbols, wordmark, lockup, slogan strips and share cards -
live in [`stickers/`](./stickers/) (16 SVGs, numbered by family; the filenames say what each is).
Print notes: 3 mm bleed, and outline the type or supply Inter 700.

PNG renders for web/app icons live in [`png/`](./png/): `icon.png` (the NuGet / avatar app
tile) and `vela-chevron.png` (the chevron raster used in package READMEs).

### Clear space & sizing
- Keep clear space around the symbol equal to **half the chevron height**.
- Minimum symbol size: **16px** (favicon validated at this size).
- Rounded-corner radius is **~22%** of the icon everywhere.
- On busy / photographic backgrounds, use the **mono white** symbol or the **tile**.

### Don'ts
- ✕ Don't recolor the gradient outside the brand palette.
- ✕ Don't stretch, rotate, or add shadows / bevels to the symbol.
- ✕ Don't recreate the wordmark in another typeface.
- ✕ Don't add an inner fold or shading - the mark stays flat, identical across chevron,
  tile and favicon.

## 04 · Color palette

The **brand gradient** - 135°, indigo → cyan - is the signature: sky → sea, the world
Vela sails through. Use it for the symbol and accent details, never for large surfaces.

**Brand gradient · 135°** - `#6366F1` → `#22D3EE`

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

**Inter** - fallbacks: Segoe UI, Helvetica, Arial, sans-serif.

- **Wordmark** - Inter **700**, lowercase, tracking ~`1px`.
- **Tagline / accents** - Inter **500-600**.
- **Body** - Inter **400**.

## 06 · Voice
- **Confident, calm, precise.** Nautical metaphors are welcome but never forced
  (*set your course, chart, ship, on course*).
- **Descriptive about Dataverse.** Always "for Microsoft Dataverse," never part of the
  brand name - Vela is not affiliated with Microsoft.

## 07 · Asset map

```
branding/
  DESIGN_SYSTEM.md   this file

  symbol/            chevron (gradient / black / white / mono), tile, favicon
  wordmark/          vela logotype (indigo / ink / white)
  lockup/            symbol + wordmark (indigo / ink / white)
  stickers/          16 die-cut SVGs for print (symbols, wordmark, lockup, slogan, cards)
  png/               icon.png (NuGet / avatar tile) + vela-chevron.png (README raster)
```

All marks share one canonical chevron path (`M32 8 L54 50 L40 50 L32 32 L24 50 L10 50 Z`)
and the brand palette, so the families stay pixel-consistent across every size.

## 08 · Package naming

Packages are brand-first under `Vela.*` so the prefix can be reserved on nuget.org and the
brand scales to client-side. "Dataverse" is intentionally dropped from the ID - like
`Serilog.*` or `MediatR` - discoverability is carried by Title, Description and Tags.

```
Vela.Plugins      # runtime, contracts, primitives
Vela.Cli          # command-line tool - command: `vela`
@vela/client, @vela/pcf, @vela/cli   # npm, client-side (later)
```

```xml
<PackageTags>dataverse;dynamics365;power-platform;plugin;custom-api;xrm;cli</PackageTags>
```

---

*vela - Build Dataverse with confidence.*
