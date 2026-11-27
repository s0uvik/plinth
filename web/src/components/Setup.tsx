import { useRef } from 'react';
import { DUR, EASE, gsap, MQ, STAGGER, useGSAP } from '../lib/gsap';
import { SectionHead } from './SectionHead';

const PARTS = [
  {
    name: 'Revit add-in',
    role: 'Runs inside Revit 2024, 2025, 2026 or 2027 and makes and reads the elements. Live-tested on 2026.',
  },
  {
    name: 'Plinth service',
    role: 'Runs on your PC. Its review page opens in your browser: import, answer, approve and build there.',
  },
  {
    name: 'Claude Desktop',
    role: 'Connects to Plinth through MCP, under your own Claude account, so you can talk to Plinth and your model.',
  },
];

export function Setup() {
  const root = useRef<HTMLElement>(null);

  useGSAP(
    () => {
      const mm = gsap.matchMedia();
      mm.add(MQ.motion, () => {
        const q = gsap.utils.selector(root);
        gsap
          .timeline({ scrollTrigger: { trigger: q('.pc')[0], start: 'top 75%', once: true } })
          .from(q('.pc__label'), { autoAlpha: 0, y: 8, duration: DUR.m })
          .from(q('.pc__part'), { autoAlpha: 0, y: 14, duration: DUR.l, stagger: STAGGER.loose * 1.5 }, 0.1)
          .from(q('.pc__link'), { scaleX: 0, scaleY: 0, duration: DUR.m, ease: EASE.inOut, stagger: STAGGER.loose * 1.5 }, 0.45)
          .from(q('.pc__foot, .pc__needs'), { autoAlpha: 0, y: 8, duration: DUR.m, stagger: STAGGER.base }, 0.8);
      });
    },
    { scope: root },
  );

  return (
    <section className="section setup" ref={root} aria-labelledby="setup">
      <SectionHead
        grid="G"
        id="setup"
        title="Runs on the PC you model on"
        lede="Three pieces, installed per user, no admin rights needed."
      />
      <div className="pc">
        <p className="pc__label">Your PC</p>
        <ol className="pc__parts">
          {PARTS.map((p, i) => (
            <li key={p.name} className="pc__item">
              <div className="pc__part">
                <h3 className="pc__name">{p.name}</h3>
                <p className="pc__role">{p.role}</p>
              </div>
              {i < PARTS.length - 1 ? <span className="pc__link" aria-hidden="true" /> : null}
            </li>
          ))}
        </ol>
        <p className="pc__foot">Your model and drawings stay on your PC.</p>
        <p className="pc__needs anno">
          Needs Windows, Python 3.12 and the free ODA File Converter, which Plinth uses to read DWG files.
        </p>
      </div>
    </section>
  );
}
