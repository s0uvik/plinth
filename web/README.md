# Plinth landing page

The marketing page for Plinth: one page, built with Vite, React, TypeScript, GSAP and Lenis. Static output, no backend.

## Run it

Needs Node 20 or later (tested on Node 24).

```sh
cd web
npm install          # exact versions, from package-lock.json
npm run dev          # http://localhost:5173 with hot reload
npm run build        # type-check, then build to web/dist
npm run preview      # serve the built web/dist
```

`npm run build` runs `tsc -b` first, so a TypeScript error fails the build. `vite.config.ts` sets `base: './'`, so
`dist/` works from any sub-path or straight from a static host. Every dependency is pinned to an exact version.

## The page

The page is laid out as one drawing sheet. Sections are grid lines A to J (I is skipped, as on drawings):

| Grid | Section | What it says |
| --- | --- | --- |
| A | Hero | Brief or drawings in, a checked Revit model out. The example flat draws itself, rises into 3D and issues a sheet and an estimate. |
| B | What Plinth does | A drawing register (sheet index): A-101 Brief to model, A-102 Drawings to model, A-201 Self-check, A-301 Drawing set, A-401 Estimate, M-501 Furniture, M-502 MEP, R-601 Talk to your model, R-602 Families, done safely. Each row has a small diagram that draws itself. |
| C | A run | The scroll story: read, ask, approve, build, check, issue. Then the test record from the one real run. |
| D | Talk to your Revit model | An illustrated Claude Desktop conversation whose prompts change the drawing (schedule, warning, PDF export), and the add-in's 267 commands by group. |
| E | Fit-out | Furniture and indicative MEP, with layer toggles on Bedroom 1. |
| F | Rules it keeps | General notes: approval, no guessing, no in-place edits, exact remove, logging, local, code off by default. |
| G | Setup | Add-in, Plinth service and Claude Desktop, inside "Your PC". |
| H | Where it stands | Early access, versions, what is in testing. |
| J | Request early access | The form (not connected, see below). |

The footer is the sheet's title block.

```
web/
  index.html               fonts (Archivo, IBM Plex Mono from Google Fonts), meta, root
  public/favicon.svg
  src/
    main.tsx               entry; imports the four stylesheets in order
    App.tsx                page order, smooth scrolling
    lib/
      gsap.ts              registers ScrollTrigger, SplitText, CustomEase and useGSAP once; the motion tokens
                           (EASE, DUR, STAGGER) and media queries (MQ). Import gsap from here.
      smoothScroll.ts      Lenis, driven by GSAP's ticker; in-page links glide and then move focus
      useMagnetic.ts       the magnetic pull on call-to-action buttons
      plan.ts              geometry of the example flat (walls, doors, windows, columns, grid, rooms, estimate rows,
                           door schedule, revision cloud), 1 unit = 20 mm
    components/
      PlanParts.tsx        SVG pieces of the plan, shared by the hero, the run story and the talk drawing
      SectionHead.tsx      grid bubble + dash-dot grid line + heading, with its reveal
      Magnetic.tsx         MagneticLink, MagneticButton
      Header.tsx           nav and the scroll-progress redline
      Hero.tsx             A. plan draws, extrudes, turns to an axonometric, issues a sheet and an estimate;
                           drafting crosshair with column snap
      Register.tsx         B. the drawing register; RegisterPreviews.tsx holds its nine diagrams
      RunStory.tsx         C. sticky drawing driven by one scroll-scrubbed timeline, and the test record
      Talk.tsx             D. the illustrated conversation, its drawing, the command groups
      FitOut.tsx           E. furniture and indicative MEP, with layer toggles
      Principles.tsx       F. general notes
      Setup.tsx            G. add-in, local service, Claude Desktop inside "Your PC"
      Status.tsx           H. early access, Revit versions
      EarlyAccess.tsx      J. the form (not connected, see below)
      TitleBlock.tsx       footer as a drawing title block, with its stamp
    styles/
      tokens.css           colours, type scale, spacing, elevation, motion eases (custom properties)
      base.css             reset, body, focus, buttons, Lenis and SplitText helpers, tags
      layout.css           sheet frame, header, section heads, title block
      sections.css         plan SVG classes, then one block per section
```

## Motion rules

- **One vocabulary.** Eases are custom (`pl.out`, `pl.inOut`, `pl.draw`, `pl.camera`, `pl.stamp`, `pl.in` in
  `lib/gsap.ts`); durations and staggers come from `DUR` and `STAGGER`. CSS transitions use the same curves through
  `--ease-*` and `--dur-*`. Don't use GSAP's default eases or raw numbers.
- **Compositor-only.** Only transforms, opacity and SVG stroke offsets are animated. No animated layout properties,
  no animated CSS variables. `will-change` is set only on the hero's 3D rig while it moves.
- **Scoped and cleaned up.** Every animation lives in a `useGSAP` hook scoped to its component, so contexts,
  ScrollTriggers and `gsap.matchMedia()` handlers are reverted on unmount. Timelines for offscreen parts (section
  heads, register previews, the conversation) are built only when they first come into view.
- **Nothing pinned, nothing hijacked.** Lenis smooths the mouse wheel only; keyboard, scrollbar and touch scrolling
  stay native. The run story uses CSS `position: sticky` and a scrubbed timeline.
- **Headlines** rise line by line with SplitText (lines masked), then the split is reverted so the text reflows
  normally. Splitting waits for `document.fonts.ready`.
- **Mouse and trackpad only** (`(pointer: fine)`): the hero's drafting crosshair and its column snap, and the
  magnetic buttons.
- **`prefers-reduced-motion: reduce`**: Lenis is off, nothing splits or tweens. The hero shows its finished 3D state
  with the sheet and estimate, the run story jumps to each step's settled state as you reach it, and the conversation
  shows in full with buttons that switch the drawing between its three states.

## The early-access form

There is no backend yet. The submit handler in `src/components/EarlyAccess.tsx` is marked
`// TODO: connect to the sign-up portal`; it sends nothing and tells the visitor so ("Sign-up opens soon. Nothing was
sent"). Replace it once the portal exists.

## Content rules

Copy states only facts about Plinth that are true today, checked against the repo:

- The command count (267) and the group counts come from the command handlers registered under
  `addin/src/Revit/Commands/*/`. Recount them if commands are added.
- MEP is always described as an indicative coordination model that an MEP engineer must verify, and as in testing:
  it has not been built on a live project yet.
- NBC 2016, KMC and West Bengal rule values are described as advisory, not yet verified.
- The only figures are from the one real run (West Bengal, G+4 RCC frame): about 1,365 elements, 0 Revit warnings,
  0 unexplained differences, 338 furniture items with no clashes.
- The example flat, its estimate rows and door schedule, and the Claude conversation are illustrations drawn for this
  page, and are labelled as such. The tool names in the conversation are real `revit_` tools.
- No pricing, logos, testimonials or invented numbers.
