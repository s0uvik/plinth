import { useRef } from 'react';
import { gsap, useGSAP } from '../lib/gsap';
import { COL, COLUMNS, WALLS } from '../lib/plan';
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

/** number of stacked wall slices that make the extrusion in the axonometric */
const SLICES = 18;
const SLICE_GAP = 3; // px between slices
const AXO = { tilt: 54, spin: -36, scale: 0.86, lift: -6 };

/** Wall + column footprint only, used for every extrusion slice. */
function Slice({ top }: { top: boolean }) {
  return (
    <svg className={`hp-slice${top ? ' hp-slice--top' : ''}`} viewBox={PLAN_VIEWBOX} aria-hidden="true">
      {WALLS.map((w, i) => (
        <line key={i} x1={w.x1} y1={w.y1} x2={w.x2} y2={w.y2} strokeWidth={w.t} />
      ))}
      {COLUMNS.map(([x, y], i) => (
        <rect key={i} x={x - COL / 2} y={y - COL / 2} width={COL} height={COL} />
      ))}
    </svg>
  );
}

function HeroPlan() {
  const root = useRef<HTMLDivElement>(null);
  const replay = useRef<() => void>(() => {});

  useGSAP(
    () => {
      const q = gsap.utils.selector(root);
      const tilt = q('.hp-tilt');
      const rig = q('.hp-rig');
      const slices = q('.hp-slice');
      const leaves = q<SVGLineElement>('.hp-base .pl-door-leaf');

      const finalState = () => {
        gsap.set(tilt, { rotationX: AXO.tilt, yPercent: AXO.lift });
        gsap.set(rig, { rotation: AXO.spin, scale: AXO.scale });
        gsap.set(slices, { z: (i: number) => (i + 1) * SLICE_GAP, autoAlpha: 1 });
        gsap.set(q('.hp-base .pl-rooms, .hp-base .pl-dims'), { autoAlpha: 0.55 });
        leaves.forEach((leaf) =>
          gsap.set(leaf, { rotation: Number(leaf.getAttribute('data-sweep')), svgOrigin: leaf.getAttribute('data-hinge') ?? '0 0' }),
        );
      };

      const mm = gsap.matchMedia();

      mm.add('(prefers-reduced-motion: reduce)', () => {
        finalState();
        replay.current = () => {};
      });

      mm.add('(prefers-reduced-motion: no-preference)', () => {
        const tl = gsap.timeline({ defaults: { ease: 'power2.out' }, delay: 0.25 });

        tl.set(tilt, { rotationX: 0, yPercent: 0 })
          .set(rig, { rotation: 0, scale: 1 })
          .set(slices, { z: 0, autoAlpha: 0 })
          .set(q('.hp-base .pl-wall'), { strokeDasharray: 1, strokeDashoffset: 1 })
          .set(q('.hp-base .pl-door-arc'), { strokeDasharray: 1, strokeDashoffset: 1 })
          .set(leaves, { rotation: 0, svgOrigin: (_i: number, el: Element) => el.getAttribute('data-hinge') ?? '0 0' })
          .set(q('.hp-base .pl-col'), { scale: 0, transformOrigin: '50% 50%' })
          .set(q('.hp-base .pl-windows, .hp-base .pl-rooms, .hp-base .pl-dims, .hp-base .pl-bubble, .hp-base .pl-bubble-text'), {
            autoAlpha: 0,
          })
          .set(q('.hp-base .pl-gridline--v'), { scaleY: 0, transformOrigin: '50% 0%' })
          .set(q('.hp-base .pl-gridline--h'), { scaleX: 0, transformOrigin: '0% 50%' });

        // 1. setting out: grid lines and bubbles
        tl.to(q('.hp-base .pl-gridline'), { scaleX: 1, scaleY: 1, duration: 0.8, stagger: 0.06, ease: 'power3.inOut' }, 0)
          .to(q('.hp-base .pl-bubble, .hp-base .pl-bubble-text'), { autoAlpha: 1, duration: 0.4, stagger: 0.03 }, 0.3)
          // 2. walls draw themselves
          .to(q('.hp-base .pl-wall'), { strokeDashoffset: 0, duration: 0.9, stagger: 0.045, ease: 'power2.inOut' }, 0.6)
          // 3. columns, then windows
          .to(q('.hp-base .pl-col'), { scale: 1, duration: 0.35, stagger: 0.03, ease: 'back.out(2)' }, 1.5)
          .to(q('.hp-base .pl-windows'), { autoAlpha: 1, duration: 0.4 }, 1.8)
          // 4. doors swing in
          .to(q('.hp-base .pl-door-arc'), { strokeDashoffset: 0, duration: 0.7, stagger: 0.08, ease: 'power1.inOut' }, 2.0);
        leaves.forEach((leaf, i) => {
          tl.to(leaf, { rotation: Number(leaf.getAttribute('data-sweep')), duration: 0.7, ease: 'power1.inOut' }, 2.0 + i * 0.08);
        });
        // 5. annotation
        tl.to(q('.hp-base .pl-rooms, .hp-base .pl-dims'), { autoAlpha: 1, duration: 0.5 }, 2.5)
          // 6. the plan lifts into an axonometric and the walls rise
          .to(tilt, { rotationX: AXO.tilt, yPercent: AXO.lift, duration: 1.6, ease: 'power3.inOut' }, 3.3)
          .to(rig, { rotation: AXO.spin, scale: AXO.scale, duration: 1.6, ease: 'power3.inOut' }, 3.3)
          .to(q('.hp-base .pl-rooms, .hp-base .pl-dims'), { autoAlpha: 0.55, duration: 0.6 }, 3.6)
          .to(slices, { autoAlpha: 1, duration: 0.01, stagger: 0.035 }, 4.1)
          .to(slices, { z: (i: number) => (i + 1) * SLICE_GAP, duration: 1.1, ease: 'power3.out', stagger: 0.035 }, 4.1);

        replay.current = () => tl.restart(true);
      });
    },
    { scope: root },
  );

  return (
    <figure className="hero-plan">
      <div
        className="hp-stage"
        ref={root}
        role="img"
        aria-label="An example flat plan draws itself: grid lines, walls, columns, windows and swinging doors, then lifts into a 3D view with the walls rising."
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
      <figcaption className="hero-plan__caption">
        <span className="anno">Example flat, ground floor. 10.4 x 6.8 m, RCC frame.</span>
        <button type="button" className="link-button anno hero-plan__replay" onClick={() => replay.current()}>
          Draw it again
        </button>
      </figcaption>
    </figure>
  );
}

export function Hero() {
  return (
    <section className="hero" aria-labelledby="hero-title" id="top">
      <div className="hero__copy">
        <h1 id="hero-title" className="hero__title">
          Your DWG drawing set, built as a native Revit model.
        </h1>
        <p className="hero__lede">
          Plinth reads your office's plans, sections, elevations, foundation and beam layouts, or a written brief. It asks
          about what the drawings leave open, waits for your approval, then builds the model, the drawing set and the
          door/window estimate.
        </p>
        <div className="hero__actions">
          <a className="btn" href="#early-access">
            Request early access
          </a>
          <a className="btn btn--quiet" href="#run">
            See how a run goes
          </a>
        </div>
        <p className="hero__note anno">Early access trial. Revit 2024 to 2027.</p>
      </div>
      <HeroPlan />
    </section>
  );
}
