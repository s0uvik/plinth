import { useRef } from 'react';
import { DUR, EASE, gsap, MQ, SplitText, STAGGER, useGSAP } from '../lib/gsap';
import { COL, COLUMNS, ESTIMATE_ROWS, WALLS, gridRef } from '../lib/plan';
import {
  PLAN_VIEWBOX,
  PlanColumns,
  PlanDims,
  PlanDoors,
  PlanGrid,
  PlanRooms,
  PlanWalls,
  PlanWindows,
} from './PlanParts';
import { MagneticLink } from './Magnetic';

/** stacked wall slices that make the extrusion in the axonometric */
const SLICES = 16;
const SLICE_GAP = 3.4; // px between slices
const AXO = { tilt: 55, spin: -38, scale: 0.84, lift: -7 };

/** Wall + column footprint only, used for every extrusion slice. */
function Slice({ top }: { top: boolean }) {
  return (
    <svg className={`hp-slice${top ? ' hp-slice--top' : ''}`} viewBox={PLAN_VIEWBOX} aria-hidden="true">
      {WALLS.map((w, i) => (
        <line key={i} x1={w.x1} y1={w.y1} x2={w.x2} y2={w.y2} strokeWidth={w.t} />
      ))}
      {COLUMNS.map(([x, y], i) => (
        <rect key={i} data-ref={top ? gridRef(x, y) : undefined} x={x - COL / 2} y={y - COL / 2} width={COL} height={COL} />
      ))}
    </svg>
  );
}

/** The sheet that peels out of the model: a tiny plan with a title block. */
function OutSheet() {
  return (
    <div className="hp-card hp-card--sheet">
      <svg className="hp-card__plan" viewBox={PLAN_VIEWBOX} aria-hidden="true">
        <PlanWalls className="pl-walls hp-card__walls" pathLength />
        <PlanColumns />
      </svg>
      <div className="hp-card__tb">
        <span>Floor plan</span>
        <span>A-101</span>
        <span>1:100</span>
      </div>
    </div>
  );
}

/** The estimate that peels out of the model, rows taken from the example flat. */
function OutEstimate() {
  return (
    <div className="hp-card hp-card--est">
      <p className="hp-card__title">Door and window estimate</p>
      <table className="hp-est">
        <tbody>
          {ESTIMATE_ROWS.map((r) => (
            <tr key={r.mark} className="hp-est__row">
              <td>{r.mark}</td>
              <td>{r.item}</td>
              <td>{r.nos}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function HeroPlan() {
  const root = useRef<HTMLDivElement>(null);
  const replay = useRef<() => void>(() => {});
  /** stage-relative screen points of the columns, for the cursor's snap; refreshed when the model settles */
  const snaps = useRef<Array<{ x: number; y: number; ref: string }>>([]);
  const settled = useRef(false);
  const measure = useRef<() => void>(() => {});

  // The drawing: set out, draw, extrude, turn to an axonometric, then issue a sheet and an estimate.
  useGSAP(
    () => {
      const q = gsap.utils.selector(root);
      const tilt = q('.hp-tilt');
      const rig = q('.hp-rig');
      const slices = q('.hp-slice');
      const leaves = q<SVGLineElement>('.hp-base .pl-door-leaf');
      const dimTexts = q<SVGTextElement>('.hp-base .pl-dim-text');
      const cards = q('.hp-card');
      const annotation = q('.hp-base .pl-rooms, .hp-base .pl-dims');

      const measureSnaps = () => {
        const stage = q('.hp-frame')[0]?.getBoundingClientRect();
        if (!stage) return;
        snaps.current = q<SVGRectElement>('.hp-slice--top rect').map((r) => {
          const b = r.getBoundingClientRect();
          return { x: b.left + b.width / 2 - stage.left, y: b.top + b.height / 2 - stage.top, ref: r.dataset.ref ?? '' };
        });
      };
      measure.current = measureSnaps;

      const finalState = () => {
        gsap.set(tilt, { rotationX: AXO.tilt, yPercent: AXO.lift });
        gsap.set(rig, { rotation: AXO.spin, scale: AXO.scale });
        gsap.set(slices, { z: (i: number) => (i + 1) * SLICE_GAP, autoAlpha: 1 });
        gsap.set(annotation, { autoAlpha: 0.45 });
        leaves.forEach((leaf) =>
          gsap.set(leaf, { rotation: Number(leaf.dataset.sweep), svgOrigin: leaf.dataset.hinge ?? '0 0' }),
        );
        gsap.set(cards, { autoAlpha: 1 });
      };

      const mm = gsap.matchMedia();

      mm.add(MQ.reduced, () => {
        finalState();
        dimTexts.forEach((t) => (t.textContent = t.dataset.value ?? ''));
        replay.current = () => {};
      });

      mm.add(MQ.motion, () => {
        const base = (sel: string) => q(sel.split(',').map((s) => `.hp-base ${s.trim()}`).join(','));
        const counters = dimTexts.map(() => ({ v: 0 }));

        const tl = gsap.timeline({
          delay: 0.35,
          defaults: { ease: EASE.out },
          onStart: () => {
            settled.current = false;
            gsap.set([tilt, rig], { willChange: 'transform' });
          },
          onComplete: () => {
            gsap.set([tilt, rig], { willChange: 'auto' });
            measureSnaps();
            settled.current = true;
          },
        });

        // everything back to the blank sheet, so the timeline can be replayed from any point
        tl.set(tilt, { rotationX: 0, yPercent: 0 })
          .set(rig, { rotation: 0, scale: 1 })
          .set(slices, { z: 0, autoAlpha: 0 })
          .set(base('.pl-wall, .pl-door-arc'), { strokeDasharray: 1, strokeDashoffset: 1 })
          .set(leaves, { rotation: 0, svgOrigin: (_i: number, el: Element) => el.getAttribute('data-hinge') ?? '0 0' })
          .set(base('.pl-col'), { scale: 0, transformOrigin: '50% 50%' })
          .set(base('.pl-window'), { autoAlpha: 0, scale: 0.6, transformOrigin: '50% 50%' })
          .set(base('.pl-bubble, .pl-bubble-text'), { autoAlpha: 0, scale: 0.5, transformOrigin: '50% 50%' })
          .set(base('.pl-gridline--v'), { scaleY: 0, transformOrigin: '50% 0%' })
          .set(base('.pl-gridline--h'), { scaleX: 0, transformOrigin: '0% 50%' })
          .set(base('.pl-rooms text'), { autoAlpha: 0, y: 6 })
          .set(base('.pl-dim-line'), { scaleX: 0, transformOrigin: '0% 50%' })
          .set(base('.pl-dim-tick'), { autoAlpha: 0, scale: 0, transformOrigin: '50% 50%' })
          .set(dimTexts, { autoAlpha: 0 })
          .set(annotation, { autoAlpha: 1 })
          .set(cards, { autoAlpha: 0 })
          .set(q('.hp-card__walls .pl-wall'), { strokeDasharray: 1, strokeDashoffset: 1 })
          .set(q('.hp-est__row'), { autoAlpha: 0, x: -6 });

        // 1. setting out: grid lines run out, bubbles are set on their ends
        tl.to(base('.pl-gridline'), { scaleX: 1, scaleY: 1, duration: DUR.l, stagger: STAGGER.base, ease: EASE.inOut }, 0)
          .to(base('.pl-bubble, .pl-bubble-text'), { autoAlpha: 1, scale: 1, duration: DUR.s, stagger: STAGGER.tight, ease: EASE.stamp }, 0.35)
          // 2. walls drawn with the pen, one after another
          .to(base('.pl-wall'), { strokeDashoffset: 0, duration: DUR.l, stagger: 0.04, ease: EASE.draw }, 0.6)
          // 3. columns set down, windows placed
          .to(base('.pl-col'), { scale: 1, duration: DUR.s, stagger: STAGGER.tight, ease: EASE.stamp }, 1.35)
          .to(base('.pl-window'), { autoAlpha: 1, scale: 1, duration: DUR.s, stagger: STAGGER.tight, ease: EASE.out }, 1.55)
          // 4. doors swing open, their arcs traced behind them
          .to(base('.pl-door-arc'), { strokeDashoffset: 0, duration: DUR.l, stagger: STAGGER.base, ease: EASE.inOut }, 1.8);
        leaves.forEach((leaf, i) => {
          tl.to(leaf, { rotation: Number(leaf.dataset.sweep), duration: DUR.l, ease: EASE.inOut }, 1.8 + i * STAGGER.base);
        });

        // 5. the dimension string measures out, its figures counting up as the line runs
        tl.to(base('.pl-dim-line'), { scaleX: 1, duration: DUR.xl, ease: EASE.inOut }, 2.0)
          .to(base('.pl-dim-tick'), { autoAlpha: 1, scale: 1, duration: DUR.xs, stagger: 0.32, ease: EASE.stamp }, 2.0)
          .to(dimTexts, { autoAlpha: 1, duration: DUR.xs, stagger: 0.32 }, 2.1);
        dimTexts.forEach((t, i) => {
          const end = Number(t.dataset.value);
          tl.fromTo(
            counters[i],
            { v: 0 },
            {
              v: end,
              duration: 0.55,
              ease: EASE.out,
              onUpdate: () => {
                t.textContent = String(Math.round(counters[i].v / 10) * 10);
              },
            },
            2.1 + i * 0.32,
          );
        });
        tl.to(base('.pl-rooms text'), { autoAlpha: 1, y: 0, duration: DUR.m, stagger: STAGGER.tight }, 2.35);

        // 6. the camera eases round to an axonometric while the walls rise out of the plan
        tl.to(tilt, { rotationX: AXO.tilt, yPercent: AXO.lift, duration: DUR.camera, ease: EASE.camera }, 3.3)
          .to(rig, { rotation: AXO.spin, scale: AXO.scale, duration: DUR.camera, ease: EASE.camera }, 3.3)
          .to(annotation, { autoAlpha: 0.45, duration: DUR.m, ease: EASE.inOut }, 3.5)
          .to(slices, { autoAlpha: 1, duration: 0.01, stagger: 0.03 }, 4.0)
          .to(slices, { z: (i: number) => (i + 1) * SLICE_GAP, duration: DUR.xl, ease: EASE.out, stagger: 0.03 }, 4.0);

        // 7. the outputs peel off the model: a sheet, then the estimate
        tl.fromTo(
          q('.hp-card--sheet'),
          { autoAlpha: 0, xPercent: -38, yPercent: 30, rotation: 0, rotationX: 24, scale: 0.7, transformOrigin: '0% 100%' },
          { autoAlpha: 1, xPercent: 0, yPercent: 0, rotation: -4, rotationX: 0, scale: 1, duration: DUR.xl, ease: EASE.out },
          5.25,
        )
          .to(q('.hp-card__walls .pl-wall'), { strokeDashoffset: 0, duration: DUR.m, stagger: 0.015, ease: EASE.draw }, 5.55)
          .fromTo(
            q('.hp-card--est'),
            { autoAlpha: 0, xPercent: 34, yPercent: -26, rotation: 0, rotationX: -20, scale: 0.7, transformOrigin: '100% 0%' },
            { autoAlpha: 1, xPercent: 0, yPercent: 0, rotation: 3, rotationX: 0, scale: 1, duration: DUR.xl, ease: EASE.out },
            5.55,
          )
          .to(q('.hp-est__row'), { autoAlpha: 1, x: 0, duration: DUR.s, stagger: 0.06 }, 5.95);

        replay.current = () => tl.restart(true);
        return () => {
          settled.current = false;
        };
      });

      // Depth on scroll: the model sinks slower than the page, the issued cards float a little faster.
      mm.add(MQ.motion, () => {
        const trigger = q('.hp-frame')[0];
        gsap
          .timeline({ scrollTrigger: { trigger, start: 'top 12%', end: 'bottom top', scrub: true } })
          .to(q('.hp-stage'), { y: 70, ease: 'none' }, 0)
          .to(q('.hp-out--sheet'), { y: -46, ease: 'none' }, 0)
          .to(q('.hp-out--est'), { y: -22, ease: 'none' }, 0);
      });

      // Snap points move with the layout: re-measure after a resize once the model has settled.
      const onResize = gsap.delayedCall(0.2, () => settled.current && measureSnaps()).pause();
      const resize = () => onResize.restart(true);
      window.addEventListener('resize', resize);
      return () => window.removeEventListener('resize', resize);
    },
    { scope: root },
  );

  // A drafting crosshair over the drawing (mouse and trackpad only). It snaps to the columns like an object snap.
  useGSAP(
    () => {
      const frame = root.current?.querySelector<HTMLElement>('.hp-frame');
      if (!frame) return;
      const mm = gsap.matchMedia();
      mm.add(MQ.fine, () => {
        const q = gsap.utils.selector(frame);
        const cursor = q('.hp-cursor')[0];
        const lineX = q('.hp-cursor__h')[0];
        const lineY = q('.hp-cursor__v')[0];
        const pick = q('.hp-cursor__pick')[0];
        const snap = q('.hp-cursor__snap')[0];
        const label = q<HTMLElement>('.hp-cursor__label')[0];
        frame.classList.add('has-crosshair');

        const follow = { duration: 0.16, ease: 'power3' };
        const toLineY = gsap.quickTo(lineX, 'y', follow);
        const toLineX = gsap.quickTo(lineY, 'x', follow);
        const toPickX = gsap.quickTo(pick, 'x', follow);
        const toPickY = gsap.quickTo(pick, 'y', follow);
        let snapped = '';
        let box: DOMRect | null = null;
        let measuredAt = Number.NaN;

        // Layout is read only when the page has scrolled since the last read (the parallax moves the model).
        const fresh = () => {
          if (box && measuredAt === window.scrollY) return box;
          measuredAt = window.scrollY;
          box = frame.getBoundingClientRect();
          if (settled.current) measure.current();
          return box;
        };

        const enter = (e: PointerEvent) => {
          box = null;
          const b = fresh();
          // start where the pointer came in, not from the corner
          const x = e.clientX - b.left;
          const y = e.clientY - b.top;
          gsap.set(lineX, { y });
          gsap.set(lineY, { x });
          gsap.set(pick, { x, y });
          toLineY(y);
          toLineX(x);
          toPickX(x);
          toPickY(y);
          gsap.to(cursor, { autoAlpha: 1, duration: DUR.xs, overwrite: true });
        };
        const leave = () => {
          box = null;
          snapped = '';
          gsap.to(cursor, { autoAlpha: 0, duration: DUR.s, overwrite: true });
          gsap.to(snap, { autoAlpha: 0, scale: 0.5, duration: DUR.xs, overwrite: true });
        };
        const move = (e: PointerEvent) => {
          const b = fresh();
          let x = e.clientX - b.left;
          let y = e.clientY - b.top;
          let hit: { x: number; y: number; ref: string } | null = null;
          if (settled.current) {
            let best = 26 * 26;
            for (const p of snaps.current) {
              const d = (p.x - x) ** 2 + (p.y - y) ** 2;
              if (d < best) {
                best = d;
                hit = p;
              }
            }
          }
          if (hit) {
            x = hit.x;
            y = hit.y;
            if (snapped !== hit.ref) {
              snapped = hit.ref;
              label.textContent = `Column ${hit.ref}`;
              gsap.set(snap, { x, y });
              gsap.fromTo(snap, { autoAlpha: 0, scale: 1.8 }, { autoAlpha: 1, scale: 1, duration: DUR.s, ease: EASE.stamp, overwrite: true });
            }
          } else if (snapped) {
            snapped = '';
            gsap.to(snap, { autoAlpha: 0, scale: 0.5, duration: DUR.xs, overwrite: true });
          }
          toLineY(y);
          toLineX(x);
          toPickX(x);
          toPickY(y);
        };

        frame.addEventListener('pointerenter', enter);
        frame.addEventListener('pointerleave', leave);
        frame.addEventListener('pointermove', move);
        return () => {
          frame.classList.remove('has-crosshair');
          frame.removeEventListener('pointerenter', enter);
          frame.removeEventListener('pointerleave', leave);
          frame.removeEventListener('pointermove', move);
        };
      });
    },
    { scope: root },
  );

  return (
    <figure className="hero-plan" ref={root}>
      <div className="hp-frame">
        <div
          className="hp-stage"
          role="img"
          aria-label="An example flat plan draws itself: grid lines, walls, columns, windows, doors and dimensions. It then turns into a 3D view with the walls rising, and a drawing sheet and a door and window estimate come out of the model."
        >
          <div className="hp-tilt">
            <div className="hp-rig">
              <svg className="hp-base" viewBox={PLAN_VIEWBOX} aria-hidden="true">
                <PlanGrid />
                <PlanWalls pathLength />
                <PlanColumns />
                <PlanWindows />
                <PlanDoors open={false} />
                <PlanRooms />
                <PlanDims />
              </svg>
              {Array.from({ length: SLICES }, (_, i) => (
                <Slice key={i} top={i === SLICES - 1} />
              ))}
            </div>
          </div>
        </div>
        <div className="hp-out hp-out--sheet" aria-hidden="true">
          <OutSheet />
        </div>
        <div className="hp-out hp-out--est" aria-hidden="true">
          <OutEstimate />
        </div>
        <div className="hp-cursor" aria-hidden="true">
          <span className="hp-cursor__h" />
          <span className="hp-cursor__v" />
          <span className="hp-cursor__pick" />
          <span className="hp-cursor__snap">
            <span className="hp-cursor__label" />
          </span>
        </div>
      </div>
      <figcaption className="hero-plan__caption">
        <span className="anno">Example flat, drawn for this page. 10.4 x 6.8 m, RCC frame.</span>
        <button type="button" className="link-button anno hero-plan__replay" onClick={() => replay.current()}>
          Draw it again
        </button>
      </figcaption>
    </figure>
  );
}

export function Hero() {
  const root = useRef<HTMLElement>(null);

  // The headline is set line by line, each rising out of its own mask; the rest follows on one beat.
  useGSAP(
    () => {
      const q = gsap.utils.selector(root);
      const title = q<HTMLElement>('.hero__title')[0];
      const rest = q('.hero__lede, .hero__actions, .hero__proof');
      const mm = gsap.matchMedia();
      mm.add(MQ.motion, () => {
        gsap.set(title, { autoAlpha: 0 });
        gsap.set(rest, { autoAlpha: 0, y: 16 });
        let split: SplitText | null = null;
        let cancelled = false;
        document.fonts.ready.then(() => {
          if (cancelled) return;
          split = SplitText.create(title, { type: 'lines', mask: 'lines', linesClass: 'split-line' });
          gsap
            .timeline({ delay: 0.1, onComplete: () => split?.revert() })
            .set(title, { autoAlpha: 1 })
            .from(split.lines, { yPercent: 110, duration: DUR.xl, ease: EASE.out, stagger: STAGGER.loose })
            .to(rest, { autoAlpha: 1, y: 0, duration: DUR.xl, ease: EASE.out, stagger: STAGGER.base }, 0.45);
        });
        return () => {
          cancelled = true;
          split?.revert();
        };
      });
    },
    { scope: root },
  );

  return (
    <section className="hero" aria-labelledby="hero-title" id="top" ref={root}>
      <div className="hero__copy">
        <h1 id="hero-title" className="hero__title">
          Brief or drawing set in. Checked Revit model out.
        </h1>
        <p className="hero__lede">
          Plinth builds a native Revit model from a written brief or your office's DWG set, checks it against the
          source, then issues the sheets, the door and window estimate and the furniture fit-out. After that, you can
          ask Claude to work on the model in plain words.
        </p>
        <div className="hero__actions">
          <MagneticLink className="btn" href="#early-access">
            Request early access
          </MagneticLink>
          <a className="btn btn--quiet" href="#register">
            See everything it does
          </a>
        </div>
        <p className="hero__proof anno">
          Run end to end on a real G+4 RCC drawing set: about 1,365 elements, 0 Revit warnings. Early access, Revit 2024
          to 2027.
        </p>
      </div>
      <HeroPlan />
    </section>
  );
}
