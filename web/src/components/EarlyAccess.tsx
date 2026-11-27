import { useState, type FormEvent } from 'react';
import { MagneticButton } from './Magnetic';
import { SectionHead } from './SectionHead';

const NOT_OPEN = 'Sign-up opens soon. Nothing was sent: this form is not connected yet.';

export function EarlyAccess() {
  const [message, setMessage] = useState('');

  // TODO: connect to the sign-up portal. There is no backend yet, so this handler sends nothing
  // and only tells the visitor so. Replace it with a real request once the portal exists.
  const handleSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setMessage(NOT_OPEN);
  };

  return (
    <section className="section access" aria-labelledby="early-access">
      <SectionHead
        grid="J"
        id="early-access"
        title="Request early access"
        lede="For BIM leads, architects and small-to-mid offices working in Revit. India first. Bring a drawing set you know well: a trial run on it is the real test."
      />
      <form className="access__form" onSubmit={handleSubmit}>
        <div className="field">
          <label htmlFor="ea-email">Work email</label>
          <input id="ea-email" name="email" type="email" autoComplete="email" required />
        </div>
        <div className="field">
          <label htmlFor="ea-office">Office or firm</label>
          <input id="ea-office" name="office" type="text" autoComplete="organization" />
        </div>
        <div className="field">
          <label htmlFor="ea-revit">Revit version you use</label>
          <select id="ea-revit" name="revit" defaultValue="2026">
            <option>2024</option>
            <option>2025</option>
            <option>2026</option>
            <option>2027</option>
          </select>
        </div>
        <div className="access__submit">
          <MagneticButton type="submit" className="btn">
            Request early access
          </MagneticButton>
          <p className="access__msg" role="status" aria-live="polite">
            {message}
          </p>
        </div>
      </form>
    </section>
  );
}
