import { SectionHead } from './SectionHead';

const NOTES = [
  { rule: 'Do not edit existing types or families in place.', why: 'Plinth duplicates first and changes the copy.' },
  { rule: 'Do not guess an input.', why: 'If the drawings leave something open, Plinth asks.' },
  { rule: 'Do not build without approval.', why: 'You see every assumption, with its reason, before anything is made.' },
  { rule: 'Remove only what the run made.', why: 'Remove deletes exactly what a run created, nothing else.' },
  { rule: 'Do not move the model or drawings off the user’s PC.', why: 'Your model and drawings stay on your PC.' },
];

export function Principles() {
  return (
    <section className="section principles" aria-labelledby="principles">
      <SectionHead
        grid="E"
        id="principles"
        title="What it will never do"
        lede="Written the way you would write them on a sheet: as general notes that apply everywhere."
      />
      <div className="notes">
        <p className="notes__head" aria-hidden="true">
          General notes
        </p>
        <ol className="notes__list">
          {NOTES.map((n) => (
            <li key={n.rule} className="note">
              <p className="note__rule">{n.rule}</p>
              <p className="note__why">{n.why}</p>
            </li>
          ))}
        </ol>
      </div>
    </section>
  );
}
