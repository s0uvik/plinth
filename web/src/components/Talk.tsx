import { useRef } from 'react';
import { DUR, EASE, gsap, MQ, ScrollTrigger, SplitText, STAGGER, useGSAP } from '../lib/gsap';
import { DOORS, DOOR_SCHEDULE } from '../lib/plan';
import { PLAN_VIEWBOX, PlanColumns, PlanDoors, PlanGrid, PlanRooms, PlanWalls, PlanWindows } from './PlanParts';
import { SectionHead } from './SectionHead';

/** Illustrative prompts. The tool names are real revit_ tools; the session itself is drawn for this page. */
const EXCHANGES = [
  {
    key: 'schedule',
    label: 'Schedule',
    you: 'Make a door schedule for Floor 2.',
    tool: 'revit_create_schedule',
    reply: 'Done: Door Schedule, Floor 2. Four doors, sorted by mark.',
  },
  {
    key: 'warnings',
    label: 'Warnings',
    you: 'Which walls have warnings?',
    tool: 'revit_get_model_warnings_summary',
    reply: 'One. The partition on grid 3, by Bedroom 2, is slightly off axis. It is marked on the plan.',
  },
  {
    key: 'export',
    label: 'Export',
    you: 'Export these sheets to PDF.',
    tool: 'revit_export_pdf',
    reply: 'Exported 3 sheets to one PDF, in sheet-number order.',
  },
];

/** The add-in's commands by folder (addin/src/Revit/Commands), grouped for reading. 267 in all. */
const TOOLSETS = [
  { name: 'Query, create, delete', what: 'Find, read and place elements, levels and grids', n: 33 },
  { name: 'Views, sheets, schedules', what: 'Create views and sheets; create, filter, sort and read schedules', n: 37 },
  { name: 'Families, materials, parameters', what: 'Load, duplicate, swap and audit; shared and project parameters', n: 37 },
  { name: 'Structural and MEP', what: 'Beams, columns, foundations, rebar; pipes, ducts, conduits, circuits', n: 30 },
  { name: 'Rooms and annotation', what: 'Rooms, areas, finishes, tags, dimensions, keynotes', n: 28 },
  { name: 'Documents, batches, workflows', what: 'Save and checkpoints, one-undo batches, audit and cleanup runs', n: 24 },
  { name: 'Graphics, filters, phases', what: 'Overrides, view filters, phases, view templates, selections', n: 22 },
  { name: 'Model health and geometry', what: 'Warnings summary, naming lint, clashes, areas and volumes', n: 17 },
  { name: 'Export', what: 'PDF, DWG, IFC, NWC, FBX, gbXML, DGN, DWF, images, CSV', n: 16 },
  { name: 'Links and coordinates', what: 'Revit and CAD links, shared coordinates, base point', n: 12 },
  { name: 'Modify', what: 'Move, copy, mirror, rotate, change type, set parameters', n: 11 },
];
const MOST = Math.max(...TOOLSETS.map((t) => t.n));

const EXPORT_SHEETS = [
  { no: 'A-101', name: 'Floor 2 plan' },
  { no: 'A-201', name: 'Section A-A' },
  { no: 'A-301', name: 'Front elevation' },
];

/** door rings sit halfway round each swing, as in the run story */
const DOOR_MARKS = DOORS.map((d) => {
  const a = ((d.a0 + d.sweep / 2) * Math.PI) / 180;
  return { mark: d.mark, x: d.hx + d.len * 0.55 * Math.cos(a), y: d.hy + d.len * 0.55 * Math.sin(a) };
});

function TalkDrawing() {
  return (
    <div className="ts-frame" aria-hidden="true">
      <div className="ts-plan">
        <svg className="ts-svg" viewBox={PLAN_VIEWBOX}>
          <PlanGrid />
          <PlanWalls />
          <PlanColumns />
          <PlanWindows />
          <PlanDoors open />
          <PlanRooms />
          <g className="ts-rings">
            {DOOR_MARKS.map((m, i) => (
              <circle key={i} className="ts-ring" cx={m.x} cy={m.y} r={15} pathLength={1} />
            ))}
          </g>
          {/* the wall with the warning: the partition on grid 3 */}
          <path className="ts-wall" d="M420 223 V376" pathLength={1} />
          {/* leader from the warning to the wall */}
          <path className="ts-leader" d="M282 120 L414 312" pathLength={1} />
          <circle className="ts-pulse" cx={420} cy={325} r={20} />
        </svg>
        <p className="ts-title">Floor 2 plan</p>
      </div>

      <div className="ts-card ts-schedule">
        <p className="ts-card__kicker">Door Schedule, Floor 2</p>
        <table className="ts-table">
          <thead>
            <tr>
              <th>Mark</th>
              <th>Width</th>
              <th>Room</th>
            </tr>
          </thead>
          <tbody>
            {DOOR_SCHEDULE.map((r, i) => (
              <tr key={i} className="ts-row">
                <td>{r.mark}</td>
                <td>{r.width}</td>
                <td>{r.room}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="ts-card ts-warning">
        <p className="ts-card__kicker">Warning, 1 wall</p>
        <p className="ts-card__main">Wall is slightly off axis and may cause inaccuracies.</p>
      </div>

      <div className="ts-export">
        <div className="ts-stack">
          {EXPORT_SHEETS.map((s) => (
            <div key={s.no} className="ts-sheet">
              <span className="ts-sheet__draw" />
              <span className="ts-sheet__tb">
                <span>{s.name}</span>
                <span>{s.no}</span>
              </span>
            </div>
          ))}
        </div>
        <div className="ts-pdf">
          <span className="ts-pdf__icon">PDF</span>
          <span className="ts-pdf__name">Floor 2 set.pdf</span>
          <span className="ts-pdf__meta">3 pages, in sheet-number order</span>
        </div>
      </div>
    </div>
  );
}

export function Talk() {
  const root = useRef<HTMLElement>(null);

  useGSAP(
    () => {
      const q = gsap.utils.selector(root);
      const buttons = q<HTMLButtonElement>('.chat__prompt');
      const playAll = q<HTMLButtonElement>('.chat__play')[0];
      let tl: gsap.core.Timeline | null = null;
      let splits: SplitText[] = [];
      let seeker: gsap.core.Tween | null = null;
      let autoplay = true;
      let inView = false;

      const mark = (i: number) =>
        buttons.forEach((b, j) => b.setAttribute('aria-pressed', String(i === j)));

      /** The whole loop as one timeline. Labels: ex0-start, ex0 (settled), ... ex2. */
      const build = (typing: boolean) => {
        const msgs = q<HTMLElement>('.msg');
        const youText = q<HTMLElement>('.msg--you .msg__text');
        const replyText = q<HTMLElement>('.msg--claude .msg__text');
        if (typing) {
          splits = [
            ...youText.map((el) => SplitText.create(el, { type: 'words,chars' })),
            ...replyText.map((el) => SplitText.create(el, { type: 'words' })),
          ];
        }
        const chars = (i: number) => (typing ? splits[i].chars : [youText[i]]);
        const words = (i: number) => (typing ? splits[EXCHANGES.length + i].words : [replyText[i]]);

        const t = gsap.timeline({ paused: true, repeat: -1, defaults: { ease: EASE.out } });

        // blank state: no messages, the bare plan
        t.set(msgs, { autoAlpha: 0, y: 12 })
          .set(q('.msg__dots'), { autoAlpha: 0 })
          .set(q('.msg__tool, .msg--claude .msg__text'), { autoAlpha: 0 })
          .set(q('.ts-ring'), { strokeDasharray: 1, strokeDashoffset: 1, autoAlpha: 1 })
          .set(q('.ts-wall'), { strokeDasharray: 1, strokeDashoffset: 1, autoAlpha: 1 })
          .set(q('.ts-leader'), { strokeDasharray: 1, strokeDashoffset: 1, autoAlpha: 1 })
          .set(q('.ts-pulse'), { autoAlpha: 0, scale: 0.5, transformOrigin: '50% 50%' })
          .set(q('.ts-schedule, .ts-warning, .ts-export, .ts-pdf'), { autoAlpha: 0 })
          .set(q('.ts-schedule'), { x: 24 })
          .set(q('.ts-warning'), { y: 14 })
          .set(q('.ts-row'), { autoAlpha: 0, x: -6 })
          .set(q('.ts-plan'), { autoAlpha: 1, scale: 1, transformOrigin: '50% 50%' })
          .set(q('.ts-sheet'), { autoAlpha: 0, x: 0, y: 18, scale: 1, rotation: 0 })
          .set(q('.ts-pdf'), { scale: 0.7 });
        if (typing) {
          t.set(splits.flatMap((s) => s.chars), { autoAlpha: 0 }).set(
            splits.slice(EXCHANGES.length).flatMap((s) => s.words),
            { autoAlpha: 0, y: 4 },
          );
        }

        let at = 0.6;
        EXCHANGES.forEach((_, i) => {
          const you = msgs[i * 2];
          const reply = msgs[i * 2 + 1];
          const rq = gsap.utils.selector(reply);
          t.addLabel(`ex${i}-start`, at);
          t.call(mark, [i], at);
          // the prompt is typed
          t.to(you, { autoAlpha: 1, y: 0, duration: DUR.m }, at);
          const typeTime = typing ? chars(i).length * STAGGER.chars : 0;
          t.to(chars(i), { autoAlpha: 1, duration: 0.01, stagger: STAGGER.chars, ease: 'none' }, at + 0.15);
          at += 0.15 + typeTime + 0.35;
          // Claude thinks, calls the tool, answers
          t.to(reply, { autoAlpha: 1, y: 0, duration: DUR.m }, at);
          t.to(rq('.msg__dots'), { autoAlpha: 1, duration: DUR.xs }, at);
          t.fromTo(
            rq('.msg__dots i'),
            { y: 0 },
            { y: -3, duration: 0.24, ease: 'sine.inOut', stagger: 0.08, repeat: 3, yoyo: true, immediateRender: false },
            at + 0.05,
          );
          at += 1.05;
          t.to(rq('.msg__dots'), { autoAlpha: 0, duration: DUR.xs }, at);
          t.fromTo(rq('.msg__tool'), { autoAlpha: 0, scale: 0.85 }, { autoAlpha: 1, scale: 1, duration: DUR.s, ease: EASE.stamp, immediateRender: false }, at);
          t.set(rq('.msg__text'), { autoAlpha: 1 }, at + 0.25);
          t.to(words(i), { autoAlpha: 1, y: 0, duration: DUR.s, stagger: 0.03 }, at + 0.25);

          // the drawing answers the prompt
          const d = at + 0.1;
          if (i === 0) {
            t.to(q('.ts-ring'), { strokeDashoffset: 0, duration: DUR.l, ease: EASE.draw, stagger: STAGGER.base }, d)
              .to(q('.ts-schedule'), { autoAlpha: 1, x: 0, duration: DUR.l }, d + 0.2)
              .to(q('.ts-row'), { autoAlpha: 1, x: 0, duration: DUR.s, stagger: 0.08 }, d + 0.45);
          } else if (i === 1) {
            t.to(q('.ts-schedule'), { autoAlpha: 0, x: 24, duration: DUR.m, ease: EASE.in }, d - 0.9)
              .to(q('.ts-ring'), { strokeDashoffset: -1, duration: DUR.m, ease: EASE.inOut }, d - 0.9)
              .to(q('.ts-wall'), { strokeDashoffset: 0, duration: DUR.l, ease: EASE.draw }, d)
              .fromTo(
                q('.ts-pulse'),
                { autoAlpha: 0.9, scale: 0.5 },
                { autoAlpha: 0, scale: 1.8, duration: 1.1, ease: EASE.out, repeat: 2, immediateRender: false },
                d + 0.3,
              )
              .to(q('.ts-warning'), { autoAlpha: 1, y: 0, duration: DUR.l }, d + 0.35)
              .to(q('.ts-leader'), { strokeDashoffset: 0, duration: DUR.m, ease: EASE.draw }, d + 0.6);
          } else {
            t.to(q('.ts-warning'), { autoAlpha: 0, y: 14, duration: DUR.m, ease: EASE.in }, d - 0.9)
              .to(q('.ts-wall, .ts-leader'), { autoAlpha: 0, duration: DUR.m }, d - 0.9)
              .to(q('.ts-plan'), { autoAlpha: 0.18, scale: 0.94, duration: DUR.l, ease: EASE.inOut }, d - 0.2)
              .set(q('.ts-export'), { autoAlpha: 1 }, d)
              .to(q('.ts-sheet'), { autoAlpha: 1, y: 0, rotation: (k: number) => [-3, 1, 4][k], duration: DUR.m, stagger: STAGGER.loose }, d)
              .to(
                q('.ts-sheet'),
                { x: '62%', y: -6, scale: 0.42, autoAlpha: 0, duration: DUR.l, ease: EASE.inOut, stagger: STAGGER.loose },
                d + 0.9,
              )
              .to(q('.ts-pdf'), { autoAlpha: 1, scale: 1, duration: DUR.m, ease: EASE.stamp }, d + 1.25);
          }
          // the export has the longest drawing change; its settled state waits for the PDF to land
          at += i === 2 ? 2.5 : 1.9;
          t.addLabel(`ex${i}`, at);
          at += 1.3;
        });

        // hold, then clear the conversation so the loop starts again on a blank sheet
        at += 1.2;
        t.to(msgs, { autoAlpha: 0, y: -8, duration: DUR.m, ease: EASE.in, stagger: 0.03 }, at)
          .to(q('.ts-export'), { autoAlpha: 0, duration: DUR.m }, at)
          .to(q('.ts-plan'), { autoAlpha: 1, scale: 1, duration: DUR.l, ease: EASE.inOut }, at + 0.2)
          .to({}, { duration: 0.6 });
        return t;
      };

      const mm = gsap.matchMedia();

      mm.add(MQ.motion, () => {
        // Built once, the first time it is needed. (A timeline is a thenable, so it is handed to a callback rather
        // than returned from a promise, which would wait for it to finish.)
        const withTimeline = (fn: (t: gsap.core.Timeline) => void) =>
          document.fonts.ready.then(() => {
            if (!root.current) return;
            if (!tl) tl = build(true);
            fn(tl);
          });
        // hide the conversation until it is first played, so it never shows finished and then restarts
        gsap.set(q('.msg'), { autoAlpha: 0 });
        gsap.set(q('.ts-schedule, .ts-warning, .ts-export'), { autoAlpha: 0 });

        ScrollTrigger.create({
          trigger: q('.talk__demo')[0],
          start: 'top 72%',
          end: 'bottom 18%',
          onToggle: (self) => {
            inView = self.isActive;
            if (inView && autoplay) withTimeline((t) => inView && autoplay && t.play());
            else tl?.pause();
          },
        });

        const show = (i: number) => {
          autoplay = false;
          playAll.hidden = false;
          withTimeline((t) => {
            seeker?.kill();
            t.pause();
            t.seek(`ex${i}-start`, false);
            seeker = t.tweenTo(`ex${i}`);
            mark(i);
          });
        };
        const resume = () => {
          autoplay = true;
          playAll.hidden = true;
          withTimeline((t) => {
            seeker?.kill();
            t.play(0);
            if (!inView) t.pause();
          });
        };
        const handlers = buttons.map((b, i) => {
          const h = () => show(i);
          b.addEventListener('click', h);
          return h;
        });
        playAll.addEventListener('click', resume);

        // the command counts measure out like a dimension string
        gsap.set(q('.toolset__fill'), { scaleX: 0 });
        ScrollTrigger.create({
          trigger: q('.toolsets__list')[0],
          start: 'top 80%',
          once: true,
          onEnter: () =>
            gsap.to(q('.toolset__fill'), {
              scaleX: (k: number) => TOOLSETS[k].n / MOST,
              duration: DUR.xl,
              ease: EASE.inOut,
              stagger: STAGGER.tight,
            }),
        });

        return () => {
          seeker?.kill();
          buttons.forEach((b, i) => b.removeEventListener('click', handlers[i]));
          playAll.removeEventListener('click', resume);
          tl?.kill();
          tl = null;
          splits.forEach((s) => s.revert());
          splits = [];
        };
      });

      mm.add(MQ.reduced, () => {
        // No motion: the whole conversation is shown; the buttons switch the drawing between its three states.
        const t = build(false);
        t.seek('ex2', false);
        mark(2);
        const handlers = buttons.map((b, i) => {
          const h = () => {
            t.seek(`ex${i}`, false);
            mark(i);
          };
          b.addEventListener('click', h);
          return h;
        });
        return () => {
          buttons.forEach((b, i) => b.removeEventListener('click', handlers[i]));
          t.kill();
        };
      });
    },
    { scope: root },
  );

  return (
    <section className="section talk" ref={root} aria-labelledby="talk">
      <SectionHead
        grid="D"
        id="talk"
        title="Talk to your Revit model"
        lede="Plinth connects Claude Desktop to Revit. Ask in plain words; Claude picks from the add-in’s 267 Revit commands, calls them on your open model and tells you what it did."
      />
      <div className="talk__body">
        <figure className="talk__demo">
          <div className="chat">
            <div className="chat__bar">
              <span className="chat__app">Claude Desktop, with Plinth</span>
              <span className="chat__tag">Illustration</span>
            </div>
            <ol className="chat__log">
              {EXCHANGES.flatMap((e) => [
                <li key={`${e.key}-you`} className="msg msg--you">
                  <span className="msg__who">You</span>
                  <p className="msg__text">{e.you}</p>
                </li>,
                <li key={`${e.key}-claude`} className="msg msg--claude">
                  <span className="msg__who">Claude</span>
                  <span className="msg__dots" aria-hidden="true">
                    <i />
                    <i />
                    <i />
                  </span>
                  <code className="msg__tool">{e.tool}</code>
                  <p className="msg__text">{e.reply}</p>
                </li>,
              ])}
            </ol>
            <div className="chat__prompts" role="group" aria-label="Show one prompt in the drawing">
              {EXCHANGES.map((e, i) => (
                <button key={e.key} type="button" className="chat__prompt">
                  <span className="chat__prompt-no" aria-hidden="true">
                    {i + 1}
                  </span>
                  {e.label}
                </button>
              ))}
              <button type="button" className="chat__play link-button anno" hidden>
                Play all three
              </button>
            </div>
          </div>
          <TalkDrawing />
          <figcaption className="talk__caption anno">
            An illustration of prompts you could give, drawn for this page on the example flat. Not a recorded session.
            The tool names are the real ones Claude would call.
          </figcaption>
        </figure>

        <div className="toolsets">
          <h3 className="toolsets__title">267 Revit commands, by what they work on</h3>
          <ul className="toolsets__list">
            {TOOLSETS.map((t) => (
              <li key={t.name} className="toolset">
                <span className="toolset__name">{t.name}</span>
                <span className="toolset__what">{t.what}</span>
                <span className="toolset__n">{t.n}</span>
                <span className="toolset__bar" aria-hidden="true">
                  <span className="toolset__fill" style={{ transform: `scaleX(${t.n / MOST})` }} />
                </span>
              </li>
            ))}
          </ul>
          <p className="toolsets__note">
            A small default set is switched on. Turn on the rest, or a read-only mode, in one settings file. Commands
            that run code inside Revit stay off unless you switch on developer mode.
          </p>
        </div>
      </div>
    </section>
  );
}
