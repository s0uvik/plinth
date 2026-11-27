import type { ReactNode } from 'react';

type Props = {
  /** grid bubble letter, the section's position on the sheet */
  grid: string;
  id: string;
  title: ReactNode;
  lede?: ReactNode;
};

/**
 * A section opens like a grid line on a drawing: a bubble, a dash-dot line across the sheet,
 * then the heading.
 */
export function SectionHead({ grid, id, title, lede }: Props) {
  return (
    <div className="sec-head">
      <div className="sec-head__grid" aria-hidden="true">
        <span className="bubble">{grid}</span>
        <span className="gridline" />
      </div>
      <h2 id={id} className="sec-head__title">
        {title}
      </h2>
      {lede ? <p className="sec-head__lede">{lede}</p> : null}
    </div>
  );
}
