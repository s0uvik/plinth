import { useRef } from 'react';
import { EASE, gsap, MQ, useGSAP } from './gsap';

/**
 * A button that leans toward the pointer, a few pixels at most, and settles back when the pointer leaves.
 * Mouse and trackpad only (pointer: fine), never under reduced motion. Moves with transform only.
 */
export function useMagnetic<T extends HTMLElement>(strength = 0.28, max = 9) {
  const ref = useRef<T>(null);

  useGSAP(
    () => {
      const el = ref.current;
      if (!el) return;
      const mm = gsap.matchMedia();
      mm.add(MQ.fine, () => {
        const toX = gsap.quickTo(el, 'x', { duration: 0.45, ease: EASE.out });
        const toY = gsap.quickTo(el, 'y', { duration: 0.45, ease: EASE.out });
        const clamp = gsap.utils.clamp(-max, max);
        let box: DOMRect | null = null;

        const enter = () => {
          // read once per hover, never on every move
          box = el.getBoundingClientRect();
        };
        const move = (e: PointerEvent) => {
          if (!box) box = el.getBoundingClientRect();
          toX(clamp((e.clientX - (box.left + box.width / 2)) * strength));
          toY(clamp((e.clientY - (box.top + box.height / 2)) * strength));
        };
        const leave = () => {
          box = null;
          toX(0);
          toY(0);
        };

        el.addEventListener('pointerenter', enter);
        el.addEventListener('pointermove', move);
        el.addEventListener('pointerleave', leave);
        return () => {
          el.removeEventListener('pointerenter', enter);
          el.removeEventListener('pointermove', move);
          el.removeEventListener('pointerleave', leave);
          gsap.set(el, { clearProps: 'transform' });
        };
      });
    },
    { scope: ref },
  );

  return ref;
}
