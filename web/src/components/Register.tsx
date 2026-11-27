import { useRef } from 'react';
import { DUR, EASE, gsap, MQ, ScrollTrigger, STAGGER, useGSAP } from '../lib/gsap';
import { PREVIEWS } from './RegisterPreviews';
import { SectionHead } from './SectionHead';

type Row = {
  no: string;
  title: string;
  href: string;
  desc: string;
  /** a fact from the real test run, or a caveat */
  note?: string;
  /** shown in redline: not proven live yet */
  caution?: string;
};

type Group = { code: string; name: string; rows: Row[] };

const GROUPS: Group[] = [
  {
    code: 'A',
    name: 'Model and documents',
    rows: [
      {
        no: 'A-101',
        title: 'Brief to model',
        href: '#run',
        desc: 'Describe the building in words. Plinth plans it, checks it against the location’s rules, shows you a preview and builds it once you approve.',
        note: 'Rule sets: NBC 2016, KMC, West Bengal. Advisory: their values are not yet verified.',
      },
      {
        no: 'A-102',
        title: 'Drawings to model',
        href: '#run',
        desc: 'Give it a folder of DWG sheets. It sorts plans, sections, elevations, foundation and beam layouts and site plans, asks the location first, then everything the drawings leave open, and builds native Revit elements after you approve.',
        note: 'Real G+4 set: about 1,365 elements, 0 Revit warnings.',
      },
      {
        no: 'A-201',
        title: 'Self-check',
        href: '#run',
        desc: 'Reads the model back and compares it with the plan and with the drawings. Every difference is explained, or flagged for you to check.',
        note: 'Real G+4 set: 0 unexplained differences.',
      },
      {
        no: 'A-301',
        title: 'Drawing set',
        href: '#run',
        desc: 'Sheets and views on your office’s title block at standard scales, plus a PDF. It asks for the title-block fields instead of guessing them, and can remake the set.',
      },
      {
        no: 'A-401',
        title: 'Estimate',
        href: '#run',
        desc: 'A door and window estimate in your office’s own Excel format. Give it your own estimate as well and it compares the two, cell by cell.',
      },
    ],
  },
  {
    code: 'M',
    name: 'Fit-out',
    rows: [
      {
        no: 'M-501',
        title: 'Furniture',
        href: '#fit-out',
        desc: 'Room by room, from your office’s loaded families. It measures each family’s real size, keeps clearances and avoids clashes. Ask for placeholders and it makes a plain box family.',
        note: 'Real G+4 set: 338 items placed, no clashes.',
      },
      {
        no: 'M-502',
        title: 'MEP',
        href: '#fit-out',
        desc: 'Plumbing and drainage, electrical, HVAC and fire, laid out by rules and built as native pipes, conduits, fittings, devices and circuits.',
        caution: 'In testing. An indicative coordination model that an MEP engineer must verify.',
      },
    ],
  },
  {
    code: 'R',
    name: 'Working in Revit',
    rows: [
      {
        no: 'R-601',
        title: 'Talk to your model',
        href: '#talk',
        desc: 'Through Claude Desktop, Claude uses the add-in’s 267 Revit commands for you: schedules, sheets, exports, warnings, families, rooms and more.',
      },
      {
        no: 'R-602',
        title: 'Families, done safely',
        href: '#principles',
        desc: 'Finds a family in the project or in Revit’s library and adapts a copy for a new size. It asks before creating one, and never edits an existing type or family in place.',
      },
    ],
  },
];

export function Register() {
  const root = useRef<HTMLElement>(null);

  useGSAP(
    () => {
      const q = gsap.utils.selector(root);
      const rows = q<HTMLElement>('.reg-row');
      const mm = gsap.matchMedia();

      mm.add(MQ.motion, () => {
        // The register is ruled as it comes into view: each rule runs across, then its row is written in.
        gsap.set(q('.reg-rule'), { scaleX: 0, transformOrigin: '0% 50%' });
        gsap.set(q('.reg-row__no, .reg-row__body, .reg-group__head'), { autoAlpha: 0, x: -10 });

        const ruling = ScrollTrigger.batch(q('.reg-group'), {
          start: 'top 82%',
          once: true,
          onEnter: (groups) =>
            groups.forEach((g, gi) => {
              const gq = gsap.utils.selector(g);
              gsap
                .timeline({ delay: gi * 0.15 })
                .to(gq('.reg-group__head'), { autoAlpha: 1, x: 0, duration: DUR.m })
                .to(gq('.reg-rule'), { scaleX: 1, duration: DUR.l, ease: EASE.inOut, stagger: STAGGER.base }, 0)
                .to(gq('.reg-row__no, .reg-row__body'), { autoAlpha: 1, x: 0, duration: DUR.m, stagger: STAGGER.tight }, 0.2);
            }),
        });

        // Each preview is drawn the first time its row comes into view, and again on hover or keyboard focus.
        const cleanups = rows.map((row) => {
          const rq = gsap.utils.selector(row);
          let tl: gsap.core.Timeline | null = null;
          const build = () => {
            const strokes = rq('.pv-s, .pv-r');
            const pops = rq('.pv-f');
            const marks = rq('.pv-d, .pv-text');
            const t = gsap.timeline({ paused: true });
            if (strokes.length)
              t.fromTo(strokes, { strokeDashoffset: 1, strokeDasharray: 1 }, { strokeDashoffset: 0, duration: DUR.m, ease: EASE.draw, stagger: 0.05 }, 0);
            if (pops.length)
              t.fromTo(pops, { autoAlpha: 0, scale: 0.7, transformOrigin: '50% 50%' }, { autoAlpha: 1, scale: 1, duration: DUR.s, ease: EASE.stamp, stagger: 0.06 }, 0.15);
            if (marks.length)
              t.fromTo(marks, { autoAlpha: 0 }, { autoAlpha: 1, duration: DUR.s, ease: EASE.out, stagger: 0.05 }, 0.35);
            tl = t;
            return tl;
          };
          // hidden until its first draw, so nothing pops in already finished
          gsap.set(rq('.pv'), { autoAlpha: 0 });
          ScrollTrigger.create({
            trigger: row,
            start: 'top 85%',
            once: true,
            onEnter: () => {
              gsap.set(rq('.pv'), { autoAlpha: 1 });
              (tl ?? build()).play(0);
            },
          });
          const replay = () => {
            if (!tl) return; // not reached yet: the scroll draw will do it
            gsap.set(rq('.pv'), { autoAlpha: 1 });
            (tl ?? build()).restart();
          };
          row.addEventListener('pointerenter', replay);
          row.addEventListener('focusin', replay);
          return () => {
            row.removeEventListener('pointerenter', replay);
            row.removeEventListener('focusin', replay);
            tl?.kill();
          };
        });

        return () => {
          ruling.forEach((t) => t.kill());
          cleanups.forEach((c) => c());
        };
      });
    },
    { scope: root },
  );

  return (
    <section className="section register-sec" ref={root} aria-labelledby="register">
      <SectionHead
        grid="B"
        id="register"
        title="What Plinth does"
        lede="Read it like the sheet index on a drawing set. Every entry works on the same Revit model, so the drawings, the estimate and the fit-out agree with it and with each other."
      />
      <div className="reg">
        <div className="reg-cols anno" aria-hidden="true">
          <span>Sheet</span>
          <span>Title and scope</span>
          <span>Preview</span>
        </div>
        {GROUPS.map((g) => (
          <section key={g.code} className="reg-group" aria-labelledby={`reg-${g.code}`}>
            <h3 id={`reg-${g.code}`} className="reg-group__head">
              <span className="reg-group__code" aria-hidden="true">
                {g.code}
              </span>
              {g.name}
            </h3>
            <ol className="reg-list">
              {g.rows.map((r) => {
                const Preview = PREVIEWS[r.no];
                return (
                  <li key={r.no} className="reg-row">
                    <span className="reg-rule" aria-hidden="true" />
                    <span className="reg-row__no" aria-hidden="true">
                      {r.no}
                    </span>
                    <div className="reg-row__body">
                      <h4 className="reg-row__title">
                        <a href={r.href}>
                          <span className="visually-hidden">{r.no}: </span>
                          {r.title}
                        </a>
                      </h4>
                      <p className="reg-row__desc">{r.desc}</p>
                      {r.note ? <p className="reg-row__note anno">{r.note}</p> : null}
                      {r.caution ? <p className="reg-row__note reg-row__note--caution anno">{r.caution}</p> : null}
                    </div>
                    <div className="reg-row__pv">{Preview ? <Preview /> : null}</div>
                  </li>
                );
              })}
            </ol>
          </section>
        ))}
      </div>
    </section>
  );
}
