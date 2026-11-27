import { SectionHead } from './SectionHead';

const OUTPUTS = [
  {
    name: 'Revit model',
    what: 'Native walls, columns, beams, slabs, doors, windows, stairs, railings, rooms, shafts and levels.',
    form: 'In your Revit project',
  },
  {
    name: 'Drawing set',
    what: 'Sheets made from the model and fitted to your office’s title block.',
    form: 'Revit sheets and PDF',
  },
  {
    name: 'Estimate',
    what: 'Door and window estimate taken from the model.',
    form: 'Your office’s own Excel format',
  },
  {
    name: 'Self-check',
    what: 'The model read back and compared with the plan and the drawings, every difference explained.',
    form: 'Differences listed with reasons',
  },
];

const RECORD = [
  { label: 'Elements in the model', value: 'about 1,365' },
  { label: 'Revit warnings', value: '0' },
  { label: 'Unexplained differences in the self-check', value: '0' },
  { label: 'Furniture items placed, every flat', value: '338, no clashes' },
];

export function Deliverables() {
  return (
    <section className="section output" aria-labelledby="output">
      <SectionHead
        grid="C"
        id="output"
        title="What you get from one run"
        lede="Four things, all made from the same model, so they agree with each other."
      />
      <div className="output__body">
        <table className="register">
          <caption className="visually-hidden">What a run produces</caption>
          <thead>
            <tr>
              <th scope="col">Output</th>
              <th scope="col">What it is</th>
              <th scope="col">Form</th>
            </tr>
          </thead>
          <tbody>
            {OUTPUTS.map((o) => (
              <tr key={o.name}>
                <th scope="row">{o.name}</th>
                <td>{o.what}</td>
                <td className="register__form">{o.form}</td>
              </tr>
            ))}
          </tbody>
        </table>

        <aside className="record" aria-labelledby="record-title">
          <h3 id="record-title" className="record__title">
            Tested on a real drawing set
          </h3>
          <p className="record__lede">
            One G+4 RCC frame residential drawing set from West Bengal, India, run end to end.
          </p>
          <dl className="record__list">
            {RECORD.map((r) => (
              <div key={r.label} className="record__row">
                <dt>{r.label}</dt>
                <dd>{r.value}</dd>
              </div>
            ))}
          </dl>
        </aside>
      </div>
    </section>
  );
}
