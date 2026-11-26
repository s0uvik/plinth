/**
 * One small, original flat plan used across the page (hero, run story, fit-out).
 * Units: 1 unit = 20 mm. Overall 10 400 x 6 800 mm. Exterior walls 230 mm, interior 115 mm.
 * Grid 1-4 runs along x, grid A-C along y.
 */

export const EXT = 11.5; // 230 mm
export const INT = 5.75; // 115 mm
const e = EXT / 2;

export type Wall = { x1: number; y1: number; x2: number; y2: number; t: number };

const h = (y: number, x1: number, x2: number, t: number): Wall => ({ x1, y1: y, x2, y2: y, t });
const v = (x: number, y1: number, y2: number, t: number): Wall => ({ x1: x, y1, x2: x, y2, t });

export const WALLS: Wall[] = [
  // exterior, top (W1 110-170, W2 372-447)
  h(40, 40 - e, 110, EXT),
  h(40, 170, 372, EXT),
  h(40, 447, 560 + e, EXT),
  // exterior, bottom (D1 140-190, W1 310-370, V1 475-505)
  h(380, 40 - e, 140, EXT),
  h(380, 190, 310, EXT),
  h(380, 370, 475, EXT),
  h(380, 505, 560 + e, EXT),
  // exterior, left (W2 172-247)
  v(40, 40 + e, 172, EXT),
  v(40, 247, 380 - e, EXT),
  // exterior, right (W1 100-160)
  v(560, 40 + e, 100, EXT),
  v(560, 160, 380 - e, EXT),
  // interior
  v(260, 40 + e, 165, INT),
  v(260, 210, 232, INT),
  v(260, 277, 380 - e, INT),
  h(220, 260 + INT / 2, 560 - e, INT),
  v(420, 220 + INT / 2, 236, INT),
  v(420, 273.5, 380 - e, INT),
];

export type Opening = {
  mark: string;
  kind: 'window' | 'ventilator';
  /** wall axis */
  axis: 'h' | 'v';
  /** fixed coordinate of the wall centre line */
  at: number;
  from: number;
  to: number;
};

export const WINDOWS: Opening[] = [
  { mark: 'W1', kind: 'window', axis: 'h', at: 40, from: 110, to: 170 },
  { mark: 'W2', kind: 'window', axis: 'h', at: 40, from: 372, to: 447 },
  { mark: 'W1', kind: 'window', axis: 'h', at: 380, from: 310, to: 370 },
  { mark: 'V1', kind: 'ventilator', axis: 'h', at: 380, from: 475, to: 505 },
  { mark: 'W2', kind: 'window', axis: 'v', at: 40, from: 172, to: 247 },
  { mark: 'W1', kind: 'window', axis: 'v', at: 560, from: 100, to: 160 },
];

export type Door = {
  mark: string;
  hx: number;
  hy: number;
  len: number;
  /** closed leaf angle, degrees, screen coordinates (clockwise positive) */
  a0: number;
  /** swing, degrees */
  sweep: number;
};

export const DOORS: Door[] = [
  { mark: 'D1', hx: 140, hy: 380, len: 50, a0: 0, sweep: -90 },
  { mark: 'D2', hx: 260, hy: 210, len: 45, a0: -90, sweep: 90 },
  { mark: 'D2', hx: 260, hy: 232, len: 45, a0: 90, sweep: -90 },
  { mark: 'D3', hx: 420, hy: 236, len: 37.5, a0: 90, sweep: -90 },
];

const rad = (d: number) => (d * Math.PI) / 180;

export function doorGeometry(d: Door) {
  const cx = d.hx + d.len * Math.cos(rad(d.a0));
  const cy = d.hy + d.len * Math.sin(rad(d.a0));
  const ox = d.hx + d.len * Math.cos(rad(d.a0 + d.sweep));
  const oy = d.hy + d.len * Math.sin(rad(d.a0 + d.sweep));
  const sweepFlag = d.sweep > 0 ? 1 : 0;
  return {
    closed: { x: cx, y: cy },
    open: { x: ox, y: oy },
    arc: `M${cx} ${cy} A${d.len} ${d.len} 0 0 ${sweepFlag} ${ox} ${oy}`,
  };
}

export const COLUMNS: Array<[number, number]> = [
  [40, 40], [260, 40], [560, 40],
  [40, 380], [260, 380], [420, 380], [560, 380],
  [260, 220], [420, 220], [560, 220],
];
export const COL = 15; // 300 mm

export const GRID_X = [
  { id: '1', x: 40 },
  { id: '2', x: 260 },
  { id: '3', x: 420 },
  { id: '4', x: 560 },
];
export const GRID_Y = [
  { id: 'A', y: 40 },
  { id: 'B', y: 220 },
  { id: 'C', y: 380 },
];

export const ROOMS = [
  { name: 'LIVING', size: '4400 x 6800', x: 150, y: 205 },
  { name: 'BEDROOM 1', size: '6000 x 3600', x: 410, y: 125 },
  { name: 'BEDROOM 2', size: '3200 x 3200', x: 340, y: 305 },
  { name: 'TOILET', size: '2800 x 3200', x: 490, y: 315 },
];

/** Window symbol: the opening outline plus the glass line. */
export function openingRect(o: Opening) {
  const t = EXT;
  if (o.axis === 'h') {
    return { x: o.from, y: o.at - t / 2, w: o.to - o.from, h: t, glass: `M${o.from} ${o.at}H${o.to}` };
  }
  return { x: o.at - t / 2, y: o.from, w: t, h: o.to - o.from, glass: `M${o.at} ${o.from}V${o.to}` };
}

export function openingCentre(o: Opening) {
  const m = (o.from + o.to) / 2;
  return o.axis === 'h' ? { x: m, y: o.at } : { x: o.at, y: m };
}

/** Door/window counts for the estimate illustration, derived from the plan above. */
export const ESTIMATE_ROWS = [
  { mark: 'D1', item: 'Main door, 1000 wide', nos: 1 },
  { mark: 'D2', item: 'Bedroom door, 900 wide', nos: 2 },
  { mark: 'D3', item: 'Toilet door, 750 wide', nos: 1 },
  { mark: 'W1', item: 'Window, 1200 wide', nos: 3 },
  { mark: 'W2', item: 'Window, 1500 wide', nos: 2 },
  { mark: 'V1', item: 'Ventilator, 600 wide', nos: 1 },
];

/** A revision cloud around a rectangle, built from small arcs. */
export function revisionCloud(x: number, y: number, w: number, hgt: number, bump = 9): string {
  const pts: Array<[number, number]> = [];
  const along = (x1: number, y1: number, x2: number, y2: number) => {
    const len = Math.hypot(x2 - x1, y2 - y1);
    const n = Math.max(2, Math.round(len / (bump * 1.6)));
    for (let i = 0; i < n; i++) pts.push([x1 + ((x2 - x1) * i) / n, y1 + ((y2 - y1) * i) / n]);
  };
  along(x, y, x + w, y);
  along(x + w, y, x + w, y + hgt);
  along(x + w, y + hgt, x, y + hgt);
  along(x, y + hgt, x, y);
  let d = `M${pts[0][0].toFixed(1)} ${pts[0][1].toFixed(1)}`;
  for (let i = 1; i <= pts.length; i++) {
    const [px, py] = pts[i % pts.length];
    d += ` A${bump} ${bump} 0 0 1 ${px.toFixed(1)} ${py.toFixed(1)}`;
  }
  return d;
}
