# UI design

The visual contract for all 26 pages (E0-04 AC5). Tokens live in `src/EventPermits.Web/wwwroot/app.css` (`:root`). Page-specific styles go in a component stylesheet next to the page (`Page.razor.css`, Blazor CSS isolation), never in `app.css`.

## Direction

A calm, civic tool for memorial work: dignified rather than decorative, dense where staff work all day (tables, queues), spacious on the public landing page. Deep teal-slate header with a bronze rule, warm paper background, white cards, serif headings, system sans body. No web fonts or CDN assets.

## Colour tokens (contrast on white unless noted)

| Token | Value | Use | Contrast |
| --- | --- | --- | --- |
| `--ink` | #1c2430 | body text | 15.8:1 |
| `--ink-muted` | #4a5565 | secondary text | 7.6:1 |
| `--brand` | #1f4e5a | links, primary buttons | 9.0:1 |
| `--brand-strong` | #153840 | header, button hover | 12.6:1 |
| `--accent` | #7d5d22 | the single warm accent (rules, tags) | 5.9:1 |
| `--ok` / `--warn` / `--danger` / `--info` | #276338 / #7a4a00 / #a12f2f / #285889 | status text on their tints | ≥ 5.4:1 on their tint |
| `--paper` | #f5f3ee | page background | — |

Colour never carries meaning alone: status chips always show the status name, severity shows its word, the calendar legend names every kind.

## Type and spacing

Headings use `--font-head` (Iowan Old Style, Palatino, Georgia); everything else `--font-ui` (system UI). Base 16 px / 1.5. Spacing scale 4/8/12/16/24/32/48 (`--space-1..7`). Controls and buttons are at least 44 px tall.

## Patterns

| Pattern | Markup |
| --- | --- |
| Page title | `<PageHeader Title Lead Parent Actions>` (breadcrumbs, h1, lead, action buttons) |
| Feedback | `<ProblemAlert Problem>` (role=alert, code, correlation ID, Retry for 503) and `<LiveRegion Message>` (polite) |
| Stale write | `<ConflictPanel Rows OnReload OnReapply>` beside the form |
| Lists | `.card.card--flush > .table-wrap > table.table.table--cards` with `data-label` on every cell (cards on phones) and `<Pager>` |
| Filters | `form.filters` grid of labelled controls; state lives in the URL query |
| Forms | `.form`, `.form-grid` (`span-2` for wide fields), `.field` with `label`, input, `.field__hint`, `.field__error` (`aria-describedby` + `aria-invalid`) |
| Status | `<StatusChip Status>`; other chips `.chip.chip--good|bad|attention|progress|brand|draft|outline` |
| Empty | `<EmptyState Title>` with a next step |
| Key facts | `dl.dl` |
| KPIs | `a.kpi` with `.kpi__value` and `.kpi__label` (`kpi--warn|danger|ok`) |
| Timeline | `ul.timeline` with `.timeline__meta` |
| Wizard | `ol.steps` with `is-current` / `is-done` |
| Charts | `.bar-chart` of `.bar` rows (label, track, value) plus an equivalent data table |

## Layout and responsiveness

Breakpoints 1100 / 900 / 720 / 420 px. Below 900 px the sidebar becomes a disclosure menu; below 720 px tables become labelled cards and form grids collapse to one column. `prefers-reduced-motion` disables transitions. Print styles hide navigation (run-of-show, permit pages).

## Accessibility rules

Skip link to `#main`; one `h1` per page (focused on navigation); every control labelled; errors linked to fields and focus moved to the first invalid field; dynamic results announced in a live region; keyboard-only operation everywhere (no click-only elements); axe WCAG 2.1 A/AA clean on every page and key state.
