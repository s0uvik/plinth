import { useRef } from 'react';
import { DUR, EASE, gsap, MQ, STAGGER, useGSAP } from '../lib/gsap';
import { SectionHead } from './SectionHead';

const VERSIONS = [
  { year: '2024', tested: false },
  { year: '2025', tested: false },
  { year: '2026', tested: true },
  { year: '2027', tested: false },
];

export function Status() {
  const root = useRef<HTMLElement>(null);

  useGSAP(
    () => {
      const q = gsap.utils.selector(root);
      const mm = gsap.matchMedia();
      mm.add(MQ.motion, () => {
        // The dimension string measures out from its centre, its ticks are set, then the versions are written in.
        gsap
          .timeline({ scrollTrigger: { trigger: q('.versions')[0], start: 'top 80%', once: true } })
          .from(q('.dimstring__line'), { scaleX: 0, duration: DUR.xl, ease: EASE.inOut })
          .from(q('.dimstring__tick'), { scale: 0, autoAlpha: 0, duration: DUR.s, ease: EASE.stamp, stagger: 0.05 }, 0.9)
          .from(q('.dimstring__text'), { autoAlpha: 0, y: 4, duration: DUR.m }, 0.8)
          .from(q('.version'), { autoAlpha: 0, y: 12, duration: DUR.l, stagger: STAGGER.base }, 0.3);
      });
    },
    { scope: root },
  );

  return (
    <section className="section status" ref={root} aria-labelledby="status">
      <SectionHead grid="H" id="status" title="Where it stands" />
      <div className="status__body">
        <div className="status__text">
          <p className="status__stage">Early access, as a trial.</p>
          <p>
            Plinth has been run end to end on one real drawing set and live-tested on Revit 2026. It builds for Revit
            2024 to 2027.
          </p>
          <p>
            MEP fit-out is in testing: it has not yet been built on a live project. Compliance checks against NBC 2016,
            KMC and West Bengal rules are advisory until their values are verified.
          </p>
          <p>
            Next is a portal where you sign up, download the installer and sign the add-in in from your browser. It isn’t
            built yet.
          </p>
        </div>

        <figure className="versions">
          <figcaption className="versions__cap">Revit versions</figcaption>
          {/* a dimension string across the supported range */}
          <div className="dimstring" aria-hidden="true">
            <span className="dimstring__line" />
            <span className="dimstring__tick dimstring__tick--a" />
            <span className="dimstring__tick dimstring__tick--b" />
            <span className="dimstring__text">supported</span>
          </div>
          <ul className="versions__list">
            {VERSIONS.map((v) => (
              <li key={v.year} className={`version${v.tested ? ' version--tested' : ''}`}>
                <span className="version__year">{v.year}</span>
                <span className="version__note">{v.tested ? 'Live-tested' : 'Supported'}</span>
              </li>
            ))}
          </ul>
        </figure>
      </div>
    </section>
  );
}
