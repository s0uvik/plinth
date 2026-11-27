import type { ReactNode, SVGProps } from 'react';

/*
 * Small diagrams for the drawing register, one per row, 160 x 100.
 * .pv-s  ink stroke, drawn with the pen (pathLength 1)
 * .pv-r  redline stroke, drawn with the pen
 * .pv-f  set down (fades and scales in)
 * .pv-d  dashed or faint marks that appear rather than draw
 */

type P = SVGProps<SVGPathElement>;
const S = (p: P) => <path className="pv-s" pathLength={1} {...p} />;
const R = (p: P) => <path className="pv-r" pathLength={1} {...p} />;
const F = ({ className = '', ...p }: SVGProps<SVGRectElement>) => <rect className={`pv-f ${className}`} {...p} />;
const D = (p: P) => <path className="pv-d" {...p} />;

function Frame({ children }: { children: ReactNode }) {
  return (
    <svg className="pv" viewBox="0 0 160 100" aria-hidden="true" focusable="false">
      {children}
    </svg>
  );
}

/** a tiny axonometric block: floor, wall edges, roof outline */
function Axo({ x, y }: { x: number; y: number }) {
  return (
    <>
      <S d={`M${x} ${y} l26 -13 l26 13 l-26 13 Z`} />
      <S d={`M${x} ${y} v-16 M${x + 26} ${y + 13} v-16 M${x + 52} ${y} v-16`} />
      <S d={`M${x} ${y - 16} l26 -13 l26 13 l-26 13 Z`} />
    </>
  );
}

export const PREVIEWS: Record<string, () => ReactNode> = {
  'A-101': () => (
    <Frame>
      {/* the brief, as lines of text */}
      <S d="M12 26 H58 M12 36 H52 M12 46 H56 M12 56 H40" />
      <S d="M66 46 H86 M81 41 l5 5 l-5 5" />
      {/* the planned floor */}
      <S d="M96 20 H150 V78 H96 Z" />
      <S d="M122 20 V78 M122 50 H150" />
      {/* checked against the rules */}
      <F x="128" y="82" width="22" height="12" className="pv-f--red" />
      <R d="M133 88 l3 3 l6 -6" />
    </Frame>
  ),
  'A-102': () => (
    <Frame>
      {/* the sheets */}
      <F x="18" y="22" width="48" height="34" />
      <F x="14" y="28" width="48" height="34" />
      <F x="10" y="34" width="48" height="34" />
      {/* what was recognised, drawn over the top sheet */}
      <R d="M16 40 H52 V62 H16 Z M34 40 V62" />
      <S d="M70 52 H86 M81 47 l5 5 l-5 5" />
      <Axo x={96} y={66} />
    </Frame>
  ),
  'A-201': () => (
    <Frame>
      <S d="M12 18 H76 V82 H12 Z M44 18 V82 M44 52 H76" />
      <D d="M14 20 H74 V80 H14 Z" />
      {/* the comparison, row by row */}
      <S d="M92 26 H150 M92 44 H150 M92 62 H150 M92 80 H150" />
      <R d="M95 20 l3 3 l6 -6 M95 38 l3 3 l6 -6 M95 56 l3 3 l6 -6 M95 74 l3 3 l6 -6" />
    </Frame>
  ),
  'A-301': () => (
    <Frame>
      <F x="20" y="10" width="92" height="66" />
      <F x="16" y="14" width="92" height="66" />
      <S d="M12 18 H104 V84 H12 Z" />
      <S d="M18 24 H60 V62 H18 Z M38 24 V62 M38 44 H60" />
      <S d="M66 24 H98 V52 H66 Z M66 38 H98" />
      <S d="M72 66 H104 M72 66 V84 M88 66 V84" />
      {/* the PDF */}
      <S d="M122 30 H142 L150 38 V80 H122 Z M142 30 V38 H150" />
      <text className="pv-text" x="136" y="64" textAnchor="middle">
        PDF
      </text>
    </Frame>
  ),
  'A-401': () => (
    <Frame>
      <S d="M10 14 H96 V86 H10 Z M10 28 H96 M10 42 H96 M10 56 H96 M10 70 H96 M30 14 V86 M78 14 V86" />
      <F x="82" y="32" width="10" height="6" />
      <F x="82" y="46" width="6" height="6" />
      <F x="82" y="60" width="12" height="6" />
      <F x="82" y="74" width="8" height="6" />
      {/* your office's estimate, compared cell by cell */}
      <S d="M110 14 H150 V86 H110 Z M110 28 H150 M110 42 H150 M110 56 H150 M110 70 H150" />
      <R d="M124 33 l3 3 l6 -6 M124 47 l3 3 l6 -6 M124 75 l3 3 l6 -6" />
      <R d="M130 63 m-9 0 a9 6 0 1 0 18 0 a9 6 0 1 0 -18 0" />
    </Frame>
  ),
  'M-501': () => (
    <Frame>
      <S d="M10 12 H150 V88 H10 Z" />
      <F x="64" y="38" width="34" height="46" />
      <F x="14" y="16" width="12" height="38" />
      <F x="124" y="62" width="22" height="22" />
      {/* clearances kept */}
      <D d="M28 16 H44 V54 H28 Z M64 30 H98 M124 56 H146" />
    </Frame>
  ),
  'M-502': () => (
    <Frame>
      <S d="M10 12 H150 V88 H10 Z" />
      {/* a sloping drain to the stack */}
      <R d="M22 70 L132 80" />
      <F x="132" y="74" width="12" height="12" className="pv-f--red" />
      {/* conduit from the board to the points */}
      <D d="M18 30 H74 V50 M74 30 H118 V22" />
      <R d="M74 50 m-5 0 a5 5 0 1 0 10 0 a5 5 0 1 0 -10 0 M118 22 m-4 0 a4 4 0 1 0 8 0 a4 4 0 1 0 -8 0" />
      <F x="14" y="26" width="6" height="9" className="pv-f--red" />
      {/* split AC on the wall */}
      <S d="M40 16 H66 V21 H40 Z" />
    </Frame>
  ),
  'R-601': () => (
    <Frame>
      <S d="M10 16 H64 V30 H10 Z" />
      <S d="M22 38 H76 V60 H22 Z" />
      <S d="M28 46 H66 M28 53 H54" />
      <S d="M84 46 H96 M91 41 l5 5 l-5 5" />
      <S d="M104 18 H150 V82 H104 Z M128 18 V82 M128 52 H150" />
      {/* the wall Claude was asked about */}
      <R d="M128 54 V82" style={{ strokeWidth: 4 }} />
    </Frame>
  ),
  'R-602': () => (
    <Frame>
      {/* the family as found */}
      <S d="M20 74 l14 -7 l14 7 l-14 7 Z M20 74 V40 M48 74 V40 M34 81 V47 M20 40 l14 -7 l14 7 l-14 7 Z" />
      {/* the adapted copy, a new size */}
      <D d="M84 74 l18 -9 l18 9 l-18 9 Z M84 74 V30 M120 74 V30 M102 83 V39 M84 30 l18 -9 l18 9 l-18 9 Z" />
      <R d="M136 30 V83 M132 34 l8 -8 M132 87 l8 -8" />
      <S d="M54 56 H76 M71 51 l5 5 l-5 5" />
    </Frame>
  ),
};
