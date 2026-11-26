import { useRef } from 'react';
import { gsap, ScrollTrigger, useGSAP } from '../lib/gsap';
import { DOORS, ESTIMATE_ROWS, WINDOWS, openingCentre, revisionCloud } from '../lib/plan';
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
import { SectionHead } from './SectionHead';

const STEPS = [
  {
    title: 'Import the drawing folder',
    body: 'Point Plinth at the folder of DWG sheets. Every sheet is classified as a plan, section, elevation, foundation or beam layout, and what Plinth recognised is drawn over the sheet so you can check it.',
  },
  {
    title: 'Answer what the drawings leave open',
    body: 'Plinth asks for the project location before it assumes anything. Every assumption it still has to make is listed with its reason, for you to confirm or change.',
  },
  {
    title: 'Approve',
    body: 'Nothing is built until you approve. Until then, your Revit model is untouched.',
  },
  {
    title: 'Build',
    body: 'Native walls, columns, beams, slabs, doors, windows, stairs, railings, rooms, shafts and levels. Revit elements, not imported geometry.',
  },
  {
    title: 'Self-check',
    body: 'Plinth reads the model back and compares it with the plan and the drawings. It explains every difference it finds.',
  },
  {
    title: 'Drawing set and estimate',
    body: 'Sheets fitted to your office’s title block, with PDF, and a door/window estimate in your office’s own Excel format. Remove deletes exactly what the run created, and nothing else.',
  },
];

const SHEET_KINDS = ['Plan', 'Section', 'Elevation', 'Foundation', 'Beam layout'];
const ELEMENTS = ['Walls', 'Columns', 'Beams', 'Slabs', 'Doors', 'Windows', 'Stairs', 'Railings', 'Rooms', 'Shafts', 'Levels'];
const SHEETS = [
  { no: 'A-101', name: 'Ground floor plan' },
  { no: 'A-201', name: 'Section A-A' },
  { no: 'A-301', name: 'Front elevation' },
];

// The W2 window on the top wall is the one with the open question.
const CLOUD = revisionCloud(358, 20, 103, 40, 8);

function Tick({ x, y }: { x: number; y: number }) {
  return <path className="sf-tick" d={`M${x - 5} ${y - 13} l3.5 3.5 l6.5 -7`} />;
}

function StoryFigure() {
  const tags = [
    ...WINDOWS.map((w) => ({ mark: w.mark, ...openingCentre(w) })),
    ...DOORS.map((d) => {
      // halfway round the swing, a little inside the arc
      const a = ((d.a0 + d.sweep / 2) * Math.PI) / 180;
      return { mark: d.mark, x: d.hx + d.len * 0.55 * Math.cos(a), y: d.hy + d.len * 0.55 * Math.sin(a) };
    }),
  ];
  return (
    <div className="sf" aria-hidden="true">
      <div className="sf-sheets">
        {SHEET_KINDS.map((k) => (
          <span key={k} className="sf-kind">
            <span className="sf-kind__box" />
            {k}
          </span>
        ))}
      </div>

      <div className="sf-frame">
        <svg className="sf-svg" viewBox={PLAN_VIEWBOX}>
          <g className="sf-dwg">
            <PlanGrid />
            <PlanWalls className="pl-walls sf-dwg-outer" />
            <PlanWalls className="pl-walls sf-dwg-inner" inset={1.6} />
            <PlanWindows />
            <PlanDoors open />
            <PlanRooms />
            <PlanDims />
          </g>
          <g className="sf-model">
            <PlanWalls className="pl-walls sf-model-walls" />
            <PlanColumns className="pl-columns sf-model-cols" />
          </g>
          <g className="sf-recog">
            <PlanWalls className="pl-walls sf-recog-walls" pathLength />
            <PlanColumns className="pl-columns sf-recog-cols" />
            {tags.map((t, i) => (
              <g key={i} className="sf-tag" transform={`translate(${t.x} ${t.y})`}>
                <rect x={-11} y={-7} width={22} height={14} rx={1} />
                <text y={3.5} textAnchor="middle">
                  {t.mark}
                </text>
              </g>
            ))}
          </g>
          <path className="sf-cloud" d={CLOUD} pathLength={1} />
          <g className="sf-checks">
            {tags.map((t, i) => (
              <Tick key={i} x={t.x + 8} y={t.y} />
            ))}
          </g>
        </svg>

        <div className="sf-card sf-question">
          <p className="sf-card__kicker">Question</p>
          <p className="sf-card__main">Window W2: sill height not drawn.</p>
          <p className="sf-card__sub">900 mm assumed for West Bengal. Confirm?</p>
          <div className="sf-card__actions">
            <span className="sf-fakebtn sf-fakebtn--primary">Confirm 900 mm</span>
            <span className="sf-fakebtn">Change</span>
          </div>
        </div>

        <div className="sf-stamp">
          <span className="sf-stamp__big">Approved</span>
          <span className="sf-stamp__small">build may start</span>
        </div>

        <ul className="sf-elements">
          {ELEMENTS.map((e) => (
            <li key={e} className="sf-el">
              {e}
            </li>
          ))}
        </ul>

        <div className="sf-card sf-result">
          <p className="sf-card__kicker">Self-check</p>
          <p className="sf-result__zero">
            <span className="sf-result__num">0</span> unexplained differences
          </p>
          <p className="sf-card__sub">Explained: W2 sill at 900 mm, assumed and confirmed by you.</p>
        </div>

        <div className="sf-out">
          <div className="sf-fan">
            {SHEETS.map((s) => (
              <div key={s.no} className="sf-sheet">
                <div className="sf-sheet__draw" />
                <div className="sf-sheet__tb">
                  <span>{s.name}</span>
                  <span>{s.no}</span>
                </div>
              </div>
            ))}
          </div>
          <table className="sf-est">
            <caption>Door/window estimate</caption>
            <thead>
              <tr>
                <th>Mark</th>
                <th>Item</th>
                <th>Nos.</th>
              </tr>
            </thead>
            <tbody>
              {ESTIMATE_ROWS.map((r) => (
                <tr key={r.mark} className="sf-est__row">
                  <td>{r.mark}</td>
                  <td>{r.item}</td>
                  <td>{r.nos}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}

export function RunStory() {
  const root = useRef<HTMLElement>(null);

  useGSAP(
    () => {
      const q = gsap.utils.selector(root);
      const steps = q<HTMLElement>('.story-step');
      const stepsList = q('.story-steps')[0];

      // Mark the step being read. Plain class toggles, the same with or without motion.
      steps.forEach((step) => {
        ScrollTrigger.create({
          trigger: step,
          start: 'top 62%',
          end: 'bottom 62%',
          toggleClass: { targets: step, className: 'is-active' },
        });
      });

      // One timeline, one unit per step. Labels mark the settled state of each step.
      const tl = gsap.timeline({ paused: true, defaults: { ease: 'power2.inOut', duration: 0.5 } });

      tl.set(q('.sf-recog-walls .pl-wall'), { strokeDasharray: 1, strokeDashoffset: 1 })
        .set(q('.sf-recog-cols, .sf-tag, .sf-model, .sf-checks, .sf-stamp, .sf-result, .sf-out, .sf-question, .sf-elements'), { autoAlpha: 0 })
        .set(q('.sf-cloud'), { strokeDasharray: 1, strokeDashoffset: 1 })
        .set(q('.sf-kind__box'), { scale: 0 })
        .set(q('.sf-el'), { '--on': 0 })
        .set(q('.sf-question'), { y: 16 })
        .set(q('.sf-stamp'), { scale: 1.6, rotation: -14 })
        .set(q('.sf-result'), { y: 16 })
        .set(q('.sf-sheet'), { rotation: 0, x: 0, y: 0 })
        .set(q('.sf-est__row'), { autoAlpha: 0, x: -8 });

      // 1. import: sheets classified, recognised elements drawn over the sheet
      tl.addLabel('s1', 0)
        .to(q('.sf-kind__box'), { scale: 1, stagger: 0.06, duration: 0.2, ease: 'back.out(2)' }, 0.05)
        .to(q('.sf-recog-walls .pl-wall'), { strokeDashoffset: 0, stagger: 0.015 }, 0.15)
        .to(q('.sf-recog-cols'), { autoAlpha: 1 }, 0.4)
        .to(q('.sf-tag'), { autoAlpha: 1, stagger: 0.02, duration: 0.2 }, 0.5)
        .addLabel('s1-end', 1)
        // 2. questions: cloud the open item, ask
        .to(q('.sf-cloud'), { strokeDashoffset: 0, duration: 0.4 }, 1.05)
        .to(q('.sf-question'), { autoAlpha: 1, y: 0, duration: 0.35 }, 1.3)
        .addLabel('s2-end', 2)
        // 3. approve: stamp lands
        .to(q('.sf-stamp'), { autoAlpha: 1, scale: 1, rotation: -8, duration: 0.25, ease: 'power4.in' }, 2.1)
        .to(q('.sf-question'), { autoAlpha: 0.35, duration: 0.3 }, 2.2)
        .addLabel('s3-end', 3)
        // 4. build: overlay gives way to the model
        .to(q('.sf-stamp, .sf-question, .sf-cloud'), { autoAlpha: 0, duration: 0.3 }, 3.05)
        .to(q('.sf-recog'), { autoAlpha: 0, duration: 0.3 }, 3.1)
        .to(q('.sf-model'), { autoAlpha: 1, duration: 0.4 }, 3.15)
        .to(q('.sf-elements'), { autoAlpha: 1, duration: 0.2 }, 3.15)
        .to(q('.sf-el'), { '--on': 1, stagger: 0.05, duration: 0.15 }, 3.2)
        .addLabel('s4-end', 4)
        // 5. self-check
        .to(q('.sf-elements'), { autoAlpha: 0, duration: 0.3 }, 4.05)
        .to(q('.sf-checks'), { autoAlpha: 1, duration: 0.3 }, 4.1)
        .to(q('.sf-result'), { autoAlpha: 1, y: 0, duration: 0.35 }, 4.3)
        .addLabel('s5-end', 5)
        // 6. output: sheets fan out, estimate rows tick in
        .to(q('.sf-result, .sf-checks'), { autoAlpha: 0, duration: 0.25 }, 5.05)
        .to(q('.sf-svg'), { autoAlpha: 0.12, duration: 0.3 }, 5.05)
        .to(q('.sf-out'), { autoAlpha: 1, duration: 0.25 }, 5.15)
        .to(
          q('.sf-sheet'),
          {
            rotation: (i: number) => [-9, -2, 5][i],
            x: (i: number) => [-6, 4, 14][i] + '%',
            y: (i: number) => [6, 0, 4][i] + '%',
            duration: 0.4,
            ease: 'power3.out',
          },
          5.2,
        )
        .to(q('.sf-est__row'), { autoAlpha: 1, x: 0, stagger: 0.07, duration: 0.15 }, 5.35)
        .addLabel('s6-end', 6);

      const mm = gsap.matchMedia();

      mm.add('(prefers-reduced-motion: no-preference)', () => {
        ScrollTrigger.create({
          trigger: stepsList,
          start: 'top 62%',
          end: 'bottom 62%',
          scrub: 0.6,
          animation: tl,
        });
      });

      mm.add('(prefers-reduced-motion: reduce)', () => {
        // No motion: jump straight to each step's settled state as it is reached.
        tl.seek('s1-end', false);
        steps.forEach((step, i) => {
          ScrollTrigger.create({
            trigger: step,
            start: 'top 62%',
            end: 'bottom 62%',
            onToggle: (self) => {
              if (self.isActive) tl.seek(`s${i + 1}-end`, false);
            },
          });
        });
      });
    },
    { scope: root },
  );

  return (
    <section className="section story" ref={root} aria-labelledby="run">
      <SectionHead
        grid="B"
        id="run"
        title="How a run goes"
        lede="From a folder of DWG sheets to a model, a drawing set and an estimate. You stay in charge of the two steps that matter: answering and approving."
      />
      <div className="story__body">
        <div className="story__figure">
          <StoryFigure />
        </div>
        <ol className="story-steps">
          {STEPS.map((s, i) => (
            <li key={s.title} className="story-step">
              <span className="story-step__no" aria-hidden="true">
                {i + 1}
              </span>
              <h3 className="story-step__title">
                <span className="visually-hidden">Step {i + 1}: </span>
                {s.title}
              </h3>
              <p className="story-step__body">{s.body}</p>
            </li>
          ))}
        </ol>
      </div>
    </section>
  );
}
