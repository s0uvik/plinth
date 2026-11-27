import { useRef } from 'react';
import { gsap, useGSAP } from '../lib/gsap';
import { MagneticLink } from './Magnetic';

const NAV = [
  { href: '#register', label: 'What it does' },
  { href: '#run', label: 'A run' },
  { href: '#talk', label: 'Talk to Revit' },
  { href: '#fit-out', label: 'Fit-out' },
  { href: '#principles', label: 'Rules' },
  { href: '#status', label: 'Status' },
];

export function Header() {
  const root = useRef<HTMLElement>(null);

  // A redline along the header's bottom edge shows how far down the sheet you are. It follows the scroll position
  // exactly (no easing of its own), so it reads as state rather than animation.
  useGSAP(
    () => {
      gsap.fromTo(
        '.site-progress__fill',
        { scaleX: 0 },
        {
          scaleX: 1,
          ease: 'none',
          scrollTrigger: { trigger: document.documentElement, start: 0, end: 'max', scrub: true },
        },
      );
    },
    { scope: root },
  );

  return (
    <header className="site-header" ref={root}>
      <div className="site-header__inner">
        <a className="wordmark" href="#top" aria-label="Plinth, back to top">
          <svg className="wordmark__mark" viewBox="0 0 24 24" aria-hidden="true">
            <rect x="5" y="4" width="14" height="11" fill="none" stroke="currentColor" strokeWidth="1.6" />
            <rect x="2" y="16.5" width="20" height="4" fill="var(--red)" />
          </svg>
          <span>Plinth</span>
        </a>
        <nav aria-label="Sections" className="site-nav">
          <ul>
            {NAV.map((n) => (
              <li key={n.href}>
                <a href={n.href}>{n.label}</a>
              </li>
            ))}
          </ul>
        </nav>
        <MagneticLink className="btn btn--small" href="#early-access">
          Request early access
        </MagneticLink>
      </div>
      <span className="site-progress" aria-hidden="true">
        <span className="site-progress__fill" />
      </span>
    </header>
  );
}
