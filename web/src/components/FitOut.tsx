import { useState } from 'react';
import { SectionHead } from './SectionHead';

/**
 * Bedroom 1 of the example flat (6000 x 3600) at a larger scale, 1 unit = 20 mm.
 * Inner faces of the walls: x 15..315, y 15..195.
 */
function RoomDrawing({ furniture, mep }: { furniture: boolean; mep: boolean }) {
  return (
    <svg className="room" viewBox="-6 -6 342 222" aria-hidden="true">
      {/* walls, with openings: W2 top, W1 right, D2 on the left wall */}
      <g className="room-walls">
        <path d="M3 9 H127 M202 9 H327 M321 9 V75 M321 135 V201 M327 201 H3 M9 201 V185 M9 140 V9" />
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
        <rect x="128" y="95" width="90" height="100" />
        <path d="M128 182 H218" />
        <text x="173" y="140" textAnchor="middle">BED</text>
        <rect x="104" y="173" width="22" height="22" />
        <rect x="220" y="173" width="22" height="22" />
        <rect x="15" y="15" width="30" height="90" />
        <text x="30" y="63" textAnchor="middle" transform="rotate(-90 30 60)">WARDROBE</text>
        <rect x="285" y="140" width="30" height="55" />
        <text x="300" y="170" textAnchor="middle" transform="rotate(-90 300 167)">DESK</text>
        <rect x="266" y="158" width="16" height="16" rx="2" />
      </g>

      <g className={`room-layer room-mep${mep ? '' : ' is-off'}`}>
        {/* conduit runs from the switchboard */}
        <path className="room-run" d="M20 120 H165 V80" />
        <path className="room-run" d="M165 80 H230 V40" />
        {/* light point */}
        <circle cx="165" cy="80" r="7" />
        <path d="M160 75 L170 85 M170 75 L160 85" />
        {/* fan */}
        <circle cx="230" cy="40" r="5" />
        <path d="M230 30 V50 M220 40 H240" />
        {/* switchboard by the door */}
        <rect x="16" y="114" width="8" height="12" className="room-solid" />
        {/* sockets by the bed and the desk */}
        <path d="M108 170 a5 5 0 0 1 10 0 Z" className="room-solid" />
        <path d="M228 170 a5 5 0 0 1 10 0 Z" className="room-solid" />
        <path d="M306 135 a5 5 0 0 1 0 10 Z" className="room-solid" />
        {/* split AC indoor unit */}
        <rect x="40" y="16" width="70" height="10" />
        <text x="75" y="38" textAnchor="middle">AC</text>
      </g>
    </svg>
  );
}

export function FitOut() {
  const [furniture, setFurniture] = useState(true);
  const [mep, setMep] = useState(true);

  return (
    <section className="section fitout" aria-labelledby="fit-out">
      <SectionHead
        grid="D"
        id="fit-out"
        title="Fit-out, room by room"
        lede="Once the structure and architecture are in, Plinth can lay out the rooms."
      />
      <div className="fitout__body">
        <div className="fitout__text">
          <h3 className="fitout__h">Furniture</h3>
          <p>
            Laid out per room from your office’s own families. If you’d rather have placeholders, say so and you get a
            plain box of the right size instead.
          </p>

          <h3 className="fitout__h">MEP</h3>
          <p>
            Plumbing and drainage, electrical, HVAC and fire safety, laid out by rules from NBC 2016 and Indian
            residential practice.
          </p>
          <p className="caveat" role="note">
            <span className="caveat__label">Note</span>
            The MEP layout is an indicative coordination model. An MEP engineer must verify it before anyone relies on
            it.
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
            Bedroom 1 of the example flat, 6000 x 3600. Furniture as plain boxes; electrical and HVAC points shown
            indicatively.
          </figcaption>
        </figure>
      </div>
    </section>
  );
}
