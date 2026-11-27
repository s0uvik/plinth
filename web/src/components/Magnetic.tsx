import type { AnchorHTMLAttributes, ButtonHTMLAttributes } from 'react';
import { useMagnetic } from '../lib/useMagnetic';

/** A link styled as a button that leans toward the pointer (see useMagnetic). */
export function MagneticLink(props: AnchorHTMLAttributes<HTMLAnchorElement>) {
  const ref = useMagnetic<HTMLAnchorElement>();
  return <a ref={ref} {...props} />;
}

export function MagneticButton(props: ButtonHTMLAttributes<HTMLButtonElement>) {
  const ref = useMagnetic<HTMLButtonElement>();
  return <button ref={ref} {...props} />;
}
