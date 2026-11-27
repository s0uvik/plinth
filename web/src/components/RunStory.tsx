import { useRef } from 'react';
import { EASE, gsap, MQ, ScrollTrigger, useGSAP } from '../lib/gsap';
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
    title: 'Read the set',
    body: 'Point Plinth at the folder of DWG sheets. It sorts every sheet (plan, section, elevation, foundation or beam layout, site) and draws what it recognised over the sheet, for you to check.',
  },
  {
    title: 'Ask',
    body: 'It asks for the location first, then for everything the drawings leave open. Each assumption is listed with its reason, for you to confirm or change.',
  },
  {
    title: 'Approve',
    body: 'Nothing is built until you approve. Until then, your Revit model is untouched.',
  },
  {
    title: 'Build',
    body: 'Native levels, grids, columns, walls, floors, foundation slabs, shafts, beams, stairs, railings, doors, windows, room separation lines and rooms. Revit elements, not imported geometry.',
  },
  {
    title: 'Check',
    body: 'Plinth reads the model back and compares it with the plan and with the drawings. Every difference is explained or flagged.',
  },
  {
    title: 'Issue',
    body: 'Sheets on your office’s title block with a PDF, and the door and window estimate in your own Excel format. Remove takes out exactly what the run made, and nothing else.',
  },
];

const SHEET_KINDS = ['Plan', 'Section', 'Elevation', 'Foundation', 'Beam layout', 'Site'];
const ELEMENTS = ['Levels', 'Grids', 'Columns', 'Walls', 'Floors', 'Slabs', 'Beams', 'Stairs', 'Railings', 'Doors', 'Windows', 'Rooms'];
const RECORD = [
  { label: 'Elements built', value: 'about 1,365' },
  { label: 'Revit warnings', value: '0' },
  { label: 'Unexplained differences in the self-check', value: '0' },
  { label: 'Furniture items placed, no clashes', value: '338' },
];
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
              <g key={i} transform={`translate(${t.x} ${t.y})`}>
                <g className="sf-tag">
                  <rect x={-11} y={-7} width={22} height={14} rx={1} />
                  <text y={3.5} textAnchor="middle">
                    {t.mark}
                  </text>
                </g>
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
              <span className="sf-el__box">
                <span className="sf-el__fill" />
              </span>
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
            <caption>Door and window estimate</caption>
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

      // One timeline, one unit per step. Each change happens in the first part of its unit; the rest is a settled
      // state to read against. Labels mark those settled states.
      const tl = gsap.timeline({ paused: true, defaults: { ease: EASE.inOut, duration: 0.5 } });

      tl.set(q('.sf-recog-walls .pl-wall'), { strokeDasharray: 1, strokeDashoffset: 1 })
        .set(q('.sf-recog-cols, .sf-tag, .sf-model, .sf-checks, .sf-stamp, .sf-result, .sf-out, .sf-question, .sf-elements'), { autoAlpha: 0 })
        .set(q('.sf-cloud'), { strokeDasharray: 1, strokeDashoffset: 1 })
        .set(q('.sf-kind__box'), { scale: 0 })
        .set(q('.sf-el__fill'), { scale: 0 })
        .set(q('.sf-el'), { autoAlpha: 0.35 })
        .set(q('.sf-question'), { y: 18, rotation: 1.5 })
        .set(q('.sf-stamp'), { scale: 1.7, rotation: -16 })
        .set(q('.sf-result'), { y: 18 })
        .set(q('.sf-tag'), { scale: 0.6, transformOrigin: '50% 50%' })
        .set(q('.sf-sheet'), { rotation: 0, x: 0, y: 0 })
        .set(q('.sf-est__row'), { autoAlpha: 0, x: -8 });

      // 1. read: sheets sorted, recognised elements drawn over the sheet in redline
      tl.addLabel('s1', 0)
        .to(q('.sf-kind__box'), { scale: 1, stagger: 0.05, duration: 0.2, ease: EASE.stamp }, 0.05)
        .to(q('.sf-recog-walls .pl-wall'), { strokeDashoffset: 0, stagger: 0.015, ease: EASE.draw }, 0.12)
        .to(q('.sf-recog-cols'), { autoAlpha: 1, duration: 0.3 }, 0.4)
        .to(q('.sf-tag'), { autoAlpha: 1, scale: 1, stagger: 0.02, duration: 0.25, ease: EASE.stamp }, 0.45)
        .addLabel('s1-end', 1)
        // 2. ask: cloud the open item, the question card slides up
        .to(q('.sf-cloud'), { strokeDashoffset: 0, duration: 0.45, ease: EASE.draw }, 1.05)
        .to(q('.sf-question'), { autoAlpha: 1, y: 0, rotation: 0, duration: 0.4, ease: EASE.out }, 1.25)
        .addLabel('s2-end', 2)
        // 3. approve: the stamp is set down on the question
        .to(q('.sf-stamp'), { autoAlpha: 1, duration: 0.08, ease: 'none' }, 2.1)
        .to(q('.sf-stamp'), { scale: 1, rotation: -8, duration: 0.3, ease: EASE.stamp }, 2.1)
        .to(q('.sf-question'), { autoAlpha: 0.35, duration: 0.3 }, 2.25)
        .addLabel('s3-end', 3)
        // 4. build: the overlay gives way to the model, the element list ticks off
        .to(q('.sf-stamp, .sf-question, .sf-cloud'), { autoAlpha: 0, duration: 0.3 }, 3.05)
        .to(q('.sf-recog'), { autoAlpha: 0, duration: 0.35 }, 3.1)
        .to(q('.sf-model'), { autoAlpha: 1, duration: 0.45 }, 3.15)
        .to(q('.sf-elements'), { autoAlpha: 1, duration: 0.2 }, 3.15)
        .to(q('.sf-el'), { autoAlpha: 1, stagger: 0.04, duration: 0.12 }, 3.2)
        .to(q('.sf-el__fill'), { scale: 1, stagger: 0.04, duration: 0.14, ease: EASE.stamp }, 3.22)
        .addLabel('s4-end', 4)
        // 5. check: ticks against every recognised element, then the result
        .to(q('.sf-elements'), { autoAlpha: 0, duration: 0.3 }, 4.05)
        .to(q('.sf-checks'), { autoAlpha: 1, duration: 0.3 }, 4.1)
        .to(q('.sf-result'), { autoAlpha: 1, y: 0, duration: 0.4, ease: EASE.out }, 4.25)
        .addLabel('s5-end', 5)
        // 6. issue: sheets fan out, estimate rows tick in
        .to(q('.sf-result, .sf-checks'), { autoAlpha: 0, duration: 0.25 }, 5.05)
        .to(q('.sf-svg'), { autoAlpha: 0.12, duration: 0.3 }, 5.05)
        .to(q('.sf-out'), { autoAlpha: 1, duration: 0.25 }, 5.15)
        .to(
          q('.sf-sheet'),
          {
            rotation: (i: number) => [-9, -2, 5][i],
            x: (i: number) => [-6, 4, 14][i] + '%',
            y: (i: number) => [6, 0, 4][i] + '%',
            duration: 0.45,
            ease: EASE.out,
            stagger: 0.04,
          },
          5.2,
        )
        .to(q('.sf-est__row'), { autoAlpha: 1, x: 0, stagger: 0.06, duration: 0.16, ease: EASE.out }, 5.35)
        .addLabel('s6-end', 6);

      const mm = gsap.matchMedia();

      mm.add(MQ.motion, () => {
        // Scroll drives the drawing. Lenis already smooths the wheel, so the catch-up here stays short.
        ScrollTrigger.create({
          trigger: stepsList,
          start: 'top 62%',
          end: 'bottom 62%',
          scrub: 0.4,
          animation: tl,
        });
        // the progress rail beside the steps, drawn down the sheet as you read
        gsap.fromTo(
          q('.story-rail__fill'),
          { scaleY: 0 },
          { scaleY: 1, ease: 'none', scrollTrigger: { trigger: stepsList, start: 'top 62%', end: 'bottom 62%', scrub: true } },
        );
      });

      mm.add(MQ.reduced, () => {
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
        grid="C"
        id="run"
        title="A run, from drawings to issued sheets"
        lede="Start from a folder of DWG sheets, or from a written brief that Plinth plans for you. Either way, you stay in charge of the two steps that matter: answering and approving."
      />
      <div className="story__body">
        <div className="story__figure">
          <StoryFigure />
        </div>
        <div className="story__steps">
          <span className="story-rail" aria-hidden="true">
            <span className="story-rail__fill" />
          </span>
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
      </div>

      <aside className="record" aria-labelledby="record-title">
        <div className="record__head">
          <h3 id="record-title" className="record__title">
            Test record
          </h3>
          <p className="record__lede">
            One real G+4 RCC frame residential drawing set from West Bengal, India, run end to end and live-tested on
            Revit 2026.
          </p>
        </div>
        <dl className="record__list">
          {RECORD.map((r) => (
            <div key={r.label} className="record__row">
              <dt>{r.label}</dt>
              <dd>{r.value}</dd>
            </div>
          ))}
        </dl>
      </aside>
    </section>
  );
}
