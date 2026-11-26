import { useRef } from 'react';
import { gsap, useGSAP } from '../lib/gsap';
import { SectionHead } from './SectionHead';

const PARTS = [
  {
    name: 'Revit add-in',
    role: 'Runs inside Revit 2024 to 2027 and makes the elements.',
  },
  {
    name: 'Local service',
    role: 'Runs on your PC. You review and approve in its page in your browser.',
  },
  {
    name: 'Claude Desktop',
    role: 'Connects through MCP, so you can talk to Plinth.',
  },
];

export function Setup() {
  const root = useRef<HTMLElement>(null);

  useGSAP(
    () => {
      const mm = gsap.matchMedia();
      mm.add('(prefers-reduced-motion: no-preference)', () => {
        const q = gsap.utils.selector(root);
        gsap
          .timeline({ scrollTrigger: { trigger: q('.pc')[0], start: 'top 75%', once: true } })
          .from(q('.pc__part'), { autoAlpha: 0, y: 12, duration: 0.4, stagger: 0.12, ease: 'power2.out' })
          .from(q('.pc__link'), { scaleX: 0, scaleY: 0, duration: 0.35, stagger: 0.12, ease: 'power2.inOut' }, 0.25);
      });
    },
    { scope: root },
  );

  return (
    <section className="section setup" ref={root} aria-labelledby="setup">
      <SectionHead
        grid="F"
        id="setup"
        title="How it fits your setup"
        lede="Three pieces, all on the PC you already model on."
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
      </div>
    </section>
  );
}
