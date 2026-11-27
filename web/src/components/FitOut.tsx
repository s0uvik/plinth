import { useRef, useState } from 'react';
import { DUR, EASE, gsap, MQ, STAGGER, useGSAP } from '../lib/gsap';
import { SectionHead } from './SectionHead';

const SYSTEMS = [
  {
    name: 'Plumbing and drainage',
    what: 'Stacks in the ducts, drains laid to fall, water supply from tanks sized by occupancy, geysers.',
  },
  {
    name: 'Electrical',
    what: 'Points per room, switchboards on the latch side of the door, a distribution board per flat, circuits with loads and ratings, conduits.',
  },
  {
    name: 'HVAC',
    what: 'Split ACs sized by room area, outdoor units on exterior walls, exhaust fans.',
  },
  {
    name: 'Fire',
    what: 'The provisions for the building’s height band, extinguishers, exit signs.',
  },
];

/**
 * Bedroom 1 of the example flat (6000 x 3600) at a larger scale, 1 unit = 20 mm.
 * Inner faces of the walls: x 15..315, y 15..195. The top and right walls are exterior.
 */
function RoomDrawing({ furniture, mep }: { furniture: boolean; mep: boolean }) {
  return (
    <svg className="room" viewBox="-6 -46 342 262" aria-hidden="true">
      {/* walls, with openings: W2 top, W1 right, D2 on the left wall */}
      <g className="room-walls">
        <path d="M3 9 H127 M202 9 H327 M321 9 V75 M321 135 V201 M327 201 H3 M9 201 V185 M9 140 V9" pathLength={1} />
      </g>
      <g className="room-openings">
        <rect x="127" y="3.25" width="75" height="11.5" />
        <path d="M127 9 H202" />
        <rect x="315.25" y="75" width="11.5" height="60" />
        <path d="M321 75 V135" />
        <path className="room-door-arc" d="M15 140 A45 45 0 0 1 60 185" />
        <path d="M15 185 H60" />
      </g>

      <g className={`room-layer room-furniture${furniture ? '' : ' is-off'}`}>
        {/* clearances kept in front of each item */}
        <g className="room-clear">
          <rect x="45" y="15" width="30" height="90" />
          <rect x="128" y="65" width="90" height="30" />
          <rect x="255" y="140" width="30" height="55" />
        </g>
        <g className="room-item">
          <rect x="128" y="95" width="90" height="100" />
          <path d="M128 182 H218" />
          <text x="173" y="140" textAnchor="middle">BED</text>
        </g>
        <g className="room-item">
          <rect x="104" y="173" width="22" height="22" />
        </g>
        <g className="room-item">
          <rect x="220" y="173" width="22" height="22" />
        </g>
        <g className="room-item">
          <rect x="15" y="15" width="30" height="90" />
          <text x="30" y="63" textAnchor="middle" transform="rotate(-90 30 60)">WARDROBE</text>
        </g>
        <g className="room-item">
          <rect x="285" y="140" width="30" height="55" />
          <text x="300" y="170" textAnchor="middle" transform="rotate(-90 300 167)">DESK</text>
        </g>
        <g className="room-item">
          <rect x="266" y="158" width="16" height="16" rx="2" />
        </g>
      </g>

      <g className={`room-layer room-mep${mep ? '' : ' is-off'}`}>
        {/* conduit runs from the switchboard */}
        <path className="room-run" d="M20 120 H165 V80" />
        <path className="room-run" d="M165 80 H230 V40" />
        <g className="room-point">
          {/* light point */}
          <circle cx="165" cy="80" r="7" />
          <path d="M160 75 L170 85 M170 75 L160 85" />
        </g>
        <g className="room-point">
          {/* fan */}
          <circle cx="230" cy="40" r="5" />
          <path d="M230 30 V50 M220 40 H240" />
        </g>
        {/* switchboard on the latch side of the door */}
        <rect x="16" y="114" width="8" height="12" className="room-solid room-point" />
        {/* sockets by the bed and the desk */}
        <path d="M108 170 a5 5 0 0 1 10 0 Z" className="room-solid room-point" />
        <path d="M228 170 a5 5 0 0 1 10 0 Z" className="room-solid room-point" />
        <path d="M306 135 a5 5 0 0 1 0 10 Z" className="room-solid room-point" />
        {/* split AC indoor unit, its outdoor unit on the exterior wall */}
        <g className="room-point">
          <rect x="40" y="16" width="70" height="10" />
          <text x="94" y="38" textAnchor="middle">AC</text>
        </g>
        <path className="room-run" d="M75 16 V-14" />
        <g className="room-point">
          <rect x="47" y="-38" width="56" height="24" />
          <text x="75" y="-23" textAnchor="middle">ODU</text>
        </g>
      </g>
    </svg>
  );
}

export function FitOut() {
  const [furniture, setFurniture] = useState(true);
  const [mep, setMep] = useState(true);
  const root = useRef<HTMLElement>(null);

  useGSAP(
    () => {
      const q = gsap.utils.selector(root);
      const mm = gsap.matchMedia();
      mm.add(MQ.motion, () => {
        // The room is drawn, the furniture is set down inside its clearances, then the services are laid out.
        gsap
          .timeline({ scrollTrigger: { trigger: q('.room')[0], start: 'top 75%', once: true } })
          .from(q('.room-walls path'), { strokeDasharray: 1, strokeDashoffset: 1, duration: DUR.xl, ease: EASE.draw })
          .from(q('.room-openings > *'), { autoAlpha: 0, duration: DUR.m, stagger: STAGGER.tight }, 0.6)
          .from(q('.room-clear rect'), { autoAlpha: 0, scale: 1.15, transformOrigin: '50% 50%', duration: DUR.m, stagger: STAGGER.base }, 0.9)
          .from(
            q('.room-item'),
            { autoAlpha: 0, scale: 0.85, y: -6, transformOrigin: '50% 50%', duration: DUR.m, ease: EASE.stamp, stagger: STAGGER.base },
            1.05,
          )
          .from(q('.room-run'), { autoAlpha: 0, duration: DUR.m, stagger: STAGGER.base }, 1.6)
          .from(q('.room-point'), { autoAlpha: 0, scale: 0.4, transformOrigin: '50% 50%', duration: DUR.s, ease: EASE.stamp, stagger: STAGGER.tight }, 1.75);
      });
    },
    { scope: root },
  );

  return (
    <section className="section fitout" ref={root} aria-labelledby="fit-out">
      <SectionHead
        grid="E"
        id="fit-out"
        title="Fit-out, room by room"
        lede="Once the building is in, Plinth lays out the rooms: furniture from your own families, then the services. Each is its own run, so each can be removed on its own."
      />
      <div className="fitout__body">
        <div className="fitout__text">
          <h3 className="fitout__h">Furniture</h3>
          <p>
            Placed from the families your office already has loaded. Plinth measures each family’s real size, keeps the
            clearances and avoids clashes, and asks when nothing fits. Tell it to, and it makes a plain box family
            instead.
          </p>
          <p className="fitout__proof anno">Real G+4 set: 338 items placed, no clashes.</p>

          <h3 className="fitout__h">
            MEP <span className="tag tag--red">In testing</span>
          </h3>
          <p>Laid out by rules and built as native pipes, conduits, fittings, devices and circuits.</p>
          <dl className="systems">
            {SYSTEMS.map((s) => (
              <div key={s.name} className="system">
                <dt>{s.name}</dt>
                <dd>{s.what}</dd>
              </div>
            ))}
          </dl>
          <p>
            A problem such as a family without connectors is listed as an issue under the run. It never stops the build.
          </p>
          <p className="caveat" role="note">
            <span className="caveat__label">Note</span>
            The MEP layout is an indicative coordination model, not a design. An MEP engineer must verify it before
            anyone relies on it. It has not yet been built on a live project.
          </p>
        </div>

        <figure className="fitout__figure">
          <div className="layer-toggles" role="group" aria-label="Show layers in the drawing">
            <button type="button" className="layer-toggle" aria-pressed={furniture} onClick={() => setFurniture((v) => !v)}>
              <span className="layer-toggle__box" aria-hidden="true" />
              Furniture
            </button>
            <button type="button" className="layer-toggle" aria-pressed={mep} onClick={() => setMep((v) => !v)}>
              <span className="layer-toggle__box" aria-hidden="true" />
              MEP, indicative
            </button>
          </div>
          <RoomDrawing furniture={furniture} mep={mep} />
          <figcaption className="anno">
            Bedroom 1 of the example flat, 6000 x 3600, drawn for this page. Furniture as plain boxes with the clearances
            it keeps (dashed); electrical points and the split AC shown indicatively.
          </figcaption>
        </figure>
      </div>
    </section>
  );
}
