const CELLS = [
  { label: 'Project', value: 'Plinth' },
  { label: 'Drawing', value: 'Product overview' },
  { label: 'Sheet no.', value: 'P-001' },
  { label: 'Scale', value: 'Not to scale' },
  { label: 'Revision', value: 'Early access' },
  { label: 'Status', value: 'Trial' },
];

export function TitleBlock() {
  return (
    <footer className="titleblock">
      <div className="titleblock__grid">
        <div className="titleblock__brand">
          <p className="titleblock__name">Plinth</p>
          <p className="titleblock__line">DWG drawing sets and briefs to native Revit models.</p>
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
          <p>Revit is a trademark of Autodesk, Inc.</p>
        </div>
      </div>
    </footer>
  );
}
