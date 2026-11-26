import { SectionHead } from './SectionHead';

const VERSIONS = [
  { year: '2024', tested: false },
  { year: '2025', tested: false },
  { year: '2026', tested: true },
  { year: '2027', tested: false },
];

export function Status() {
  return (
    <section className="section status" aria-labelledby="status">
      <SectionHead grid="G" id="status" title="Where it stands" />
      <div className="status__body">
        <div className="status__text">
          <p className="status__stage">Early access, as a trial.</p>
          <p>
            Plinth has been run end to end on one real drawing set and live-tested on Revit 2026. It supports Revit 2024
            to 2027.
          </p>
          <p>
            Next is a portal where you sign up, download the installer and sign the add-in in through your browser. It
            isn’t built yet.
          </p>
        </div>

        <figure className="versions">
          <figcaption className="versions__cap">Revit versions</figcaption>
          {/* a dimension string across the supported range */}
          <div className="dimstring" aria-hidden="true">
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
