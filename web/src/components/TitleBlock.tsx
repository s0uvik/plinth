import { useRef } from 'react';
import { DUR, EASE, gsap, MQ, STAGGER, useGSAP } from '../lib/gsap';

const CELLS = [
  { label: 'Project', value: 'Plinth' },
  { label: 'Drawing', value: 'Product overview' },
  { label: 'Sheet no.', value: 'P-001' },
  { label: 'Scale', value: 'Not to scale' },
  { label: 'Revision', value: 'Early access' },
  { label: 'Status', value: 'Trial' },
];

export function TitleBlock() {
  const root = useRef<HTMLElement>(null);

  useGSAP(
    () => {
      const q = gsap.utils.selector(root);
      const mm = gsap.matchMedia();
      mm.add(MQ.motion, () => {
        // The sheet is signed off: its cells are filled in, then the stamp is set down on the corner.
        gsap
          .timeline({ scrollTrigger: { trigger: root.current, start: 'top 92%', once: true } })
          .from(q('.titleblock__brand, .tb-cell, .titleblock__notes p'), {
            autoAlpha: 0,
            y: 8,
            duration: DUR.m,
            stagger: STAGGER.tight,
          })
          .fromTo(
            q('.tb-stamp'),
            { autoAlpha: 0, scale: 1.8, rotation: -18 },
            { autoAlpha: 1, scale: 1, rotation: -7, duration: DUR.m, ease: EASE.stamp },
            0.45,
          );
      });
    },
    { scope: root },
  );

  return (
    <footer className="titleblock" ref={root}>
      <div className="titleblock__grid">
        <div className="titleblock__brand">
          <p className="titleblock__name">Plinth</p>
          <p className="titleblock__line">Revit models from briefs and drawing sets: built, checked and issued.</p>
        </div>
        <dl className="titleblock__cells">
          {CELLS.map((c) => (
            <div key={c.label} className="tb-cell">
              <dt>{c.label}</dt>
              <dd>{c.value}</dd>
            </div>
          ))}
        </dl>
        <div className="titleblock__notes">
          <p>For Revit 2024 to 2027. Live-tested on Revit 2026.</p>
          <p>MEP fit-out: indicative, in testing.</p>
          <p>Revit is a trademark of Autodesk, Inc.</p>
        </div>
      </div>
      <span className="tb-stamp" aria-hidden="true">
        Early access
      </span>
    </footer>
  );
}
