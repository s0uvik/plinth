# Plinth landing page

The marketing page for Plinth: one page, built with Vite, React, TypeScript and GSAP. Static output, no backend.

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
`dist/` works from any sub-path or straight from a static host.

## Structure

```
web/
  index.html               fonts (Archivo, IBM Plex Mono from Google Fonts), meta, root
  public/favicon.svg
  src/
    main.tsx               entry; imports the four stylesheets in order
    App.tsx                page order: header, sections A-H, title-block footer
    lib/
      gsap.ts              registers ScrollTrigger and useGSAP once; import gsap from here
      plan.ts              geometry of the example flat (walls, doors, windows, columns, grid,
                           rooms, estimate rows, revision cloud), 1 unit = 20 mm
    components/
      PlanParts.tsx        SVG pieces of the plan, shared by the hero and the run story
      SectionHead.tsx      grid bubble + dash-dot grid line + heading
      Header.tsx
      Hero.tsx             A. plan draws itself, doors swing, lifts into an axonometric
      RunStory.tsx         B. sticky drawing driven by one scroll-scrubbed timeline
      Deliverables.tsx     C. outputs register and the real test record
      FitOut.tsx           D. furniture and indicative MEP, with layer toggles
      Principles.tsx       E. "never" rules as general notes
      Setup.tsx            F. add-in, local service, Claude Desktop inside "Your PC"
      Status.tsx           G. early access, Revit versions
      EarlyAccess.tsx      H. the form (not connected, see below)
      TitleBlock.tsx       footer as a drawing title block
    styles/
      tokens.css           colours, type scale, spacing, elevation (custom properties)
      base.css             reset, body, focus, buttons, utilities
      layout.css           sheet frame, header, section heads, title block
      sections.css         plan SVG classes, then one block per section
```

## Animation rules

- Every animation lives in a `useGSAP` hook scoped to its component, so GSAP contexts, ScrollTriggers and
  `gsap.matchMedia()` handlers are reverted on unmount.
- Only transforms, opacity and SVG stroke offsets are animated.
- `prefers-reduced-motion: reduce` is handled through `gsap.matchMedia()`: the hero shows its finished 3D state, and
  the run story jumps to each step's settled state as you reach it, with no tweening.
- Nothing is pinned. The run story uses CSS `position: sticky` and a scrubbed timeline, so the page scrolls normally.

## The early-access form

There is no backend yet. The submit handler in `src/components/EarlyAccess.tsx` is marked
`// TODO: connect to the sign-up portal`; it sends nothing and tells the visitor so ("Sign-up opens soon. Nothing was
sent"). Replace it once the portal exists.

## Content rules

Copy states only facts about Plinth that are true today. MEP is always described as an indicative coordination model
that an MEP engineer must verify. The figures in the test record come from the one real run (West Bengal, G+4 RCC
frame); the example flat and its estimate rows are an illustration drawn for this page.
