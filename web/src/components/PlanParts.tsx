import {
  COL,
  COLUMNS,
  DOORS,
  GRID_X,
  GRID_Y,
  ROOMS,
  WALLS,
  WINDOWS,
  doorGeometry,
  openingRect,
} from '../lib/plan';

/** viewBox shared by every drawing of the example flat, so overlays line up. */
export const PLAN_VIEWBOX = '-14 -16 628 446';

export function PlanGrid() {
  return (
    <g className="pl-grid">
      {GRID_X.map((g) => (
        <g key={g.id}>
          <line className="pl-gridline pl-gridline--v" x1={g.x} y1={8} x2={g.x} y2={398} />
          <circle className="pl-bubble" cx={g.x} cy={-3} r={10} />
          <text className="pl-bubble-text" x={g.x} y={0.5} textAnchor="middle">
            {g.id}
          </text>
        </g>
      ))}
      {GRID_Y.map((g) => (
        <g key={g.id}>
          <line className="pl-gridline pl-gridline--h" x1={8} y1={g.y} x2={596} y2={g.y} />
          <circle className="pl-bubble" cx={-3} cy={g.y} r={10} />
          <text className="pl-bubble-text" x={-3} y={g.y + 3.5} textAnchor="middle">
            {g.id}
          </text>
        </g>
      ))}
    </g>
  );
}

type WallsProps = { className?: string; inset?: number; pathLength?: boolean };

/** Walls as thick strokes. `inset` thins them (used to fake CAD double-line outlines). */
export function PlanWalls({ className = 'pl-walls', inset = 0, pathLength = false }: WallsProps) {
  return (
    <g className={className}>
      {WALLS.map((w, i) => (
        <line
          key={i}
          className="pl-wall"
          x1={w.x1}
          y1={w.y1}
          x2={w.x2}
          y2={w.y2}
          strokeWidth={Math.max(0.6, w.t - inset)}
          pathLength={pathLength ? 1 : undefined}
        />
      ))}
    </g>
  );
}

export function PlanColumns({ className = 'pl-columns' }: { className?: string }) {
  return (
    <g className={className}>
      {COLUMNS.map(([x, y], i) => (
        <rect key={i} className="pl-col" x={x - COL / 2} y={y - COL / 2} width={COL} height={COL} />
      ))}
    </g>
  );
}

export function PlanWindows({ className = 'pl-windows' }: { className?: string }) {
  return (
    <g className={className}>
      {WINDOWS.map((o, i) => {
        const r = openingRect(o);
        return (
          <g key={i} className="pl-window">
            <rect x={r.x} y={r.y} width={r.w} height={r.h} />
            <path d={r.glass} />
          </g>
        );
      })}
    </g>
  );
}

type DoorsProps = { open?: boolean; className?: string };

export function PlanDoors({ open = true, className = 'pl-doors' }: DoorsProps) {
  return (
    <g className={className}>
      {DOORS.map((d, i) => {
        const g = doorGeometry(d);
        return (
          <g key={i} className="pl-door">
            <path className="pl-door-arc" d={g.arc} pathLength={1} />
            <line
              className="pl-door-leaf"
              data-hinge={`${d.hx} ${d.hy}`}
              data-sweep={d.sweep}
              x1={d.hx}
              y1={d.hy}
              x2={g.closed.x}
              y2={g.closed.y}
              transform={open ? `rotate(${d.sweep} ${d.hx} ${d.hy})` : undefined}
            />
          </g>
        );
      })}
    </g>
  );
}

export function PlanRooms({ className = 'pl-rooms' }: { className?: string }) {
  return (
    <g className={className}>
      {ROOMS.map((r) => (
        <g key={r.name}>
          <text className="pl-room" x={r.x} y={r.y} textAnchor="middle">
            {r.name}
          </text>
          <text className="pl-room-size" x={r.x} y={r.y + 13} textAnchor="middle">
            {r.size}
          </text>
        </g>
      ))}
    </g>
  );
}

const DIM_Y = 414;
const DIM_STOPS = [40, 260, 420, 560];

export function PlanDims({ className = 'pl-dims' }: { className?: string }) {
  return (
    <g className={className}>
      <line className="pl-dim-line" x1={28} y1={DIM_Y} x2={572} y2={DIM_Y} />
      {DIM_STOPS.map((x) => (
        <line key={x} className="pl-dim-tick" x1={x - 4} y1={DIM_Y + 4} x2={x + 4} y2={DIM_Y - 4} />
      ))}
      {DIM_STOPS.slice(1).map((x, i) => {
        const a = DIM_STOPS[i];
        return (
          <text key={x} className="pl-dim-text" x={(a + x) / 2} y={DIM_Y - 4} textAnchor="middle">
            {((x - a) * 20).toString()}
          </text>
        );
      })}
    </g>
  );
}
