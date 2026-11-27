import { useRef, type ReactNode } from 'react';
import { DUR, EASE, gsap, MQ, ScrollTrigger, SplitText, STAGGER, useGSAP } from '../lib/gsap';

type Props = {
  /** grid bubble letter, the section's position on the sheet (I and O are skipped, as on drawings) */
  grid: string;
  id: string;
  title: ReactNode;
  lede?: ReactNode;
};

/**
 * A section opens like a grid line being set out: the bubble is drawn, its letter is written in, the dash-dot line
 * runs across the sheet, then the heading rises line by line out of its own mask.
 */
export function SectionHead({ grid, id, title, lede }: Props) {
  const root = useRef<HTMLDivElement>(null);

  useGSAP(
    () => {
      const el = root.current;
      if (!el) return;
      const q = gsap.utils.selector(el);
      const circle = q('.bubble circle');
      const letter = q('.bubble text');
      const line = q('.gridline');
      const titleEl = q<HTMLElement>('.sec-head__title')[0];
      const ledeEl = q('.sec-head__lede');

      const mm = gsap.matchMedia();
      mm.add(MQ.motion, () => {
        gsap.set(circle, { strokeDasharray: 1, strokeDashoffset: 1, rotation: -90, svgOrigin: '20 20', autoAlpha: 0, fillOpacity: 0 });
        gsap.set(letter, { autoAlpha: 0, scale: 0.4, svgOrigin: '20 24' });
        gsap.set(line, { scaleX: 0, transformOrigin: '0% 50%' });
        gsap.set([titleEl, ...ledeEl], { autoAlpha: 0 });

        let split: SplitText | null = null;
        let tl: gsap.core.Timeline | null = null;

        // Built only when the section comes into view: nothing offscreen costs anything.
        const st = ScrollTrigger.create({
          trigger: el,
          start: 'top 86%',
          once: true,
          onEnter: () => {
            document.fonts.ready.then(() => {
              if (!root.current) return;
              split = SplitText.create(titleEl, { type: 'lines', mask: 'lines', linesClass: 'split-line' });
              tl = gsap
                .timeline({
                  onComplete: () => {
                    // hand the heading back to the browser so it reflows naturally on resize
                    split?.revert();
                    split = null;
                  },
                })
                .set(circle, { autoAlpha: 1 }, 0)
                .to(circle, { strokeDashoffset: 0, duration: DUR.l, ease: EASE.draw }, 0)
                .to(circle, { fillOpacity: 1, duration: DUR.m, ease: EASE.out }, 0.35)
                .to(letter, { autoAlpha: 1, scale: 1, duration: DUR.s, ease: EASE.stamp }, 0.32)
                .to(line, { scaleX: 1, duration: DUR.xl, ease: EASE.inOut }, 0.12)
                .set(titleEl, { autoAlpha: 1 }, 0.2)
                .from(split.lines, { yPercent: 108, duration: DUR.l, ease: EASE.out, stagger: STAGGER.base }, 0.2)
                .to(ledeEl, { autoAlpha: 1, duration: DUR.l, ease: EASE.out }, 0.5)
                .from(ledeEl, { y: 14, duration: DUR.l, ease: EASE.out }, 0.5);
            });
          },
        });

        return () => {
          st.kill();
          tl?.kill();
          split?.revert();
        };
      });
    },
    { scope: root },
  );

  return (
    <div className="sec-head" ref={root}>
      <div className="sec-head__grid" aria-hidden="true">
        <svg className="bubble" viewBox="0 0 40 40">
          <circle cx="20" cy="20" r="18.75" pathLength={1} />
          <text x="20" y="24.5" textAnchor="middle">
            {grid}
          </text>
        </svg>
        <span className="gridline" />
      </div>
      <h2 id={id} className="sec-head__title">
        {title}
      </h2>
      {lede ? <p className="sec-head__lede">{lede}</p> : null}
    </div>
  );
}
