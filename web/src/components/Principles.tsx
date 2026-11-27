import { useRef } from 'react';
import { DUR, EASE, gsap, MQ, ScrollTrigger, STAGGER, useGSAP } from '../lib/gsap';
import { SectionHead } from './SectionHead';

const NOTES = [
  {
    rule: 'Nothing is built until you approve.',
    why: 'You see every assumption, with its reason, before anything is made in your model.',
  },
  {
    rule: 'Nothing is guessed.',
    why: 'If the drawings or the brief leave something open, Plinth asks. Title-block fields too.',
  },
  {
    rule: 'Your types and families are never edited in place.',
    why: 'Plinth duplicates first and changes the copy. It asks before it creates a family.',
  },
  {
    rule: 'Remove takes out exactly what a run created.',
    why: 'Nothing else in the model is touched.',
  },
  {
    rule: 'Every Revit call is logged, run by run.',
    why: 'Each call and Revit’s answer are kept with the run, as an audit trail.',
  },
  {
    rule: 'Your model and drawings stay on your PC.',
    why: 'The add-in and the Plinth service run locally. Claude Desktop runs under your own Claude account.',
  },
  {
    rule: 'Running code inside Revit is off by default.',
    why: 'Commands that run code need developer mode switched on, and each use is logged.',
  },
];

export function Principles() {
  const root = useRef<HTMLElement>(null);

  useGSAP(
    () => {
      const q = gsap.utils.selector(root);
      const mm = gsap.matchMedia();
      mm.add(MQ.motion, () => {
        // Each note is written in from left to right: a strip of paper slides off it, like a wipe on a sheet.
        gsap.set(q('.note__wipe'), { scaleX: 1, transformOrigin: '100% 50%' });
        gsap.set(q('.note__no'), { autoAlpha: 0, scale: 0.5 });
        const batch = ScrollTrigger.batch(q('.note'), {
          start: 'top 88%',
          once: true,
          onEnter: (els) =>
            gsap
              .timeline()
              .to(
                els.map((el) => el.querySelector('.note__wipe')),
                { scaleX: 0, duration: DUR.l, ease: EASE.inOut, stagger: STAGGER.loose },
                0,
              )
              .to(
                els.map((el) => el.querySelector('.note__no')),
                { autoAlpha: 1, scale: 1, duration: DUR.s, ease: EASE.stamp, stagger: STAGGER.loose },
                0.1,
              ),
        });
        return () => batch.forEach((t) => t.kill());
      });
    },
    { scope: root },
  );

  return (
    <section className="section principles" ref={root} aria-labelledby="principles">
      <SectionHead
        grid="F"
        id="principles"
        title="Rules it keeps"
        lede="Written the way rules go on a drawing set: as general notes that apply to every sheet."
      />
      <div className="notes">
        <p className="notes__head" aria-hidden="true">
          General notes
        </p>
        <ol className="notes__list">
          {NOTES.map((n, i) => (
            <li key={n.rule} className="note">
              <span className="note__no" aria-hidden="true">
                {i + 1}.
              </span>
              <p className="note__rule">{n.rule}</p>
              <p className="note__why">{n.why}</p>
              <span className="note__wipe" aria-hidden="true" />
            </li>
          ))}
        </ol>
      </div>
    </section>
  );
}
