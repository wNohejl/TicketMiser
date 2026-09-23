---
name: apple-mudblazor
description: Use when building, restyling or reviewing any window, panel, sheet, alert or primitive in TicketMiser's desk (src/TicketMiser.Desk, src/TicketMiser.Web/Components) — the Apple-feel MudBlazor design system, ADR 0016. Covers the tokens in desk.css, the MudBlazor seam in mud-bridge.css, the Desk* primitives, and when to be modal.
---

# Apple-feel MudBlazor

The design system the desk implements: Apple's Human Interface Guidelines applied to
MudBlazor. It is recorded as [ADR 0016](../../../docs/adr/0016-apple-feel-design-system.md)
and it is already built. This skill exists so that every new window, panel and primitive
lands inside it rather than beside it.

## Where the system lives in this repository

The skill was generalised from LineOps files. Those files now live here under the desk's
own names, so **do not copy templates in; use what is there.**

| The system's part | Where it is |
|---|---|
| Tokens (surfaces, materials, type ramp, motion, depth), both theme blocks | `src/TicketMiser.Desk/wwwroot/css/desk.css` |
| The MudBlazor seam (bridge rules; never `!important`) | `src/TicketMiser.Desk/wwwroot/css/mud-bridge.css` |
| `MudTheme` mirroring the CSS palette, the theme service and mode | `src/TicketMiser.Desk/Theming/` |
| Buttons, fields, sheet, alert, tabs, switch, tags, toasts, metrics, chart, grid | `src/TicketMiser.Desk/Primitives/Desk*.razor` and `.cs` |
| Emphasis and state enums (Filled / Tinted / Plain × Normal / Destructive) | `Primitives/DeskEmphasis.cs`, `Primitives/DeskState.cs` |
| The floating layer and follow-ups | `Primitives/FollowUpLayer.razor`, `Primitives/SnippetShell.razor` |
| Window chrome (Desk, AppWindow, WindowBar, header, footer, rail) | `src/TicketMiser.Desk/Windowing/` |
| Render tests on the `DeskTestContext` bench | `tests/TicketMiser.Desk.Tests/` |

## Workflow

1. **Read `references/tokens.md`** once, then `desk.css`. Every colour, radius, shadow and
   duration in a new component is a token from there. If the token you need does not
   exist, add it to both theme blocks and test the mirror; never write a literal.
2. **Build on the primitives.** A new panel is `DeskPanel` (cascaded window id, header via
   `PanelHeader`, actions via `RowActions`), its controls are `DeskButton`, `DeskField`,
   `DeskSelect`, `DeskSwitch`, `DeskTabs`, its data is `DeskGrid` or `Metric`/`MetricRow`,
   its empty state is `EmptyState`, its chart is `DeskChart`. Read
   `references/components.md` for the conventions each one carries.
3. **A field is a plain element carrying `.field`**, not a component, so `@bind`,
   validation and native attributes keep working. `DeskField` exists for the labelled
   case; it wraps, it does not replace.
4. **Read `references/modality.md` before adding any dialog, sheet, or alert.** The desk is
   never modal for comparison; sheets are for self-contained tasks, alerts for one
   decision.
5. **Read `references/mudblazor-seam.md` before touching an unstyled Mud component.** The
   failure mode is a correct rule that loses the cascade; the eight instances are
   catalogued there.
6. **Write the render test on `DeskTestContext`** in the same commit. It registers Mud
   services and the theme service and puts JS interop in loose mode; every desk test sits
   on it.

Load order in `App.razor` is load-bearing:

```
MudBlazor.min.css → _content/TicketMiser.Desk/css/desk.css → _content/TicketMiser.Desk/css/mud-bridge.css
```

## The rules that are not negotiable

These are the decisions that make the system cohere. Breaking one does not produce
a variation; it produces an interface that looks like MudBlazor wearing a costume.

- **One accent.** Interactivity, focus, and selection, and nothing else. A second
  accent is how an interface stops meaning anything. *Which* accent is the operator's
  (five, from Desk settings); a component never names the hue.
- **No bare font sizes.** Every `font-size` is a ramp token or
  `calc(Npx * var(--type-scale))`, so the Text size setting reaches it. Every script
  that measures the desk divides by `currentCSSZoom`, so the Scale setting does not
  break it. See the "two dials" section of `references/tokens.md`.
- **Exactly one Filled button per context.** If a panel seems to need two, one of
  them is not the primary action.
- **Hue is not an affordance channel.** State colours (green, red, orange) belong
  on values, tags, and status — not on the controls that act on them. The one
  exception is `Role.Destructive`.
- **Materials are for things that float.** Menus, popovers, dialogs, sheets, and
  chrome. Anything holding data stays opaque, because legibility beats depth.
- **Hairlines separate; they never outline.** A border around a panel or a button
  is the Material habit this system replaces. Depth comes from fill and shadow.
- **Motion says where a thing came from.** Sheets rise, popovers scale from their
  anchor, controls settle. Nothing moves for decoration, and everything stops
  under `prefers-reduced-motion`.
- **Never `!important` in the bridge file.** If a rule needs it, the override is
  in the wrong place. The one legitimate `!important` in the whole system is the
  `prefers-reduced-motion` block, because a preference a component can
  out-specify is not a preference.

## The light theme

`templates/apple-tokens.css` ships both blocks. Light mode is
`[data-theme="light"]` redefining the same names — no component changes, and if
adding one requires touching anything below the token blocks, the token that made
it necessary was named for a colour.

Three things are not obvious and are worth reading before you tune the values.
The surface ramp **re-derives rather than inverts** — grey ground, white content
on it, interactive tiers darkening — because a ground lighter than white does not
exist. Hover and press **deepen** instead of lightening, because against a white
page a control that lightens under the pointer reads as fading. And a forgotten
token **fails nowhere**: it silently keeps its dark value, so the mirror has to be
tested mechanically in both directions.

`references/tokens.md` has the full second-theme section — the ramp table, the
contrast arithmetic redone for light, what to test, and how to wire the toggle.

## The one warning worth reading twice

When you replace a design system **in place**, the dominant failure mode is not a
missing rule. It is **a correct rule that silently loses the cascade** — right
property, right value, out-specified by a MudBlazor compound selector or shadowed
by source order, so nothing appears wrong in the source and everything is wrong on
screen. Eight separate instances of this occurred in one migration; they are
catalogued in `references/mudblazor-seam.md`. Grep finds your tokens. Only the
rendered page finds these.
