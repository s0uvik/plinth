const NAV = [
  { href: '#run', label: 'How a run goes' },
  { href: '#output', label: 'What you get' },
  { href: '#fit-out', label: 'Fit-out' },
  { href: '#setup', label: 'Setup' },
  { href: '#status', label: 'Status' },
];

export function Header() {
  return (
    <header className="site-header">
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
        <a className="btn btn--small" href="#early-access">
          Request early access
        </a>
      </div>
    </header>
  );
}
