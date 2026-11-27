import { useEffect } from 'react';
import Lenis from 'lenis';
import { gsap, MQ, ScrollTrigger } from './gsap';

/** expo-out, the same feel as the page's arrival ease */
const glide = (t: number) => (t === 1 ? 1 : 1 - Math.pow(2, -10 * t));

/**
 * Wheel smoothing with Lenis, driven by GSAP's ticker so ScrollTrigger and Lenis read the same frame.
 * The page still scrolls natively: nothing is pinned or hijacked, keyboard and scrollbar scrolling are untouched,
 * and touch scrolling stays native (syncTouch off). Off entirely under prefers-reduced-motion, and switched off or
 * on live if the visitor changes that setting. In-page links glide to their target, then move keyboard focus
 * there and update the address bar, as a native jump would.
 */
export function useSmoothScroll(): void {
  useEffect(() => {
    const reduced = window.matchMedia(MQ.reduced);
    let lenis: Lenis | null = null;
    const tick = (time: number) => lenis?.raf(time * 1000);

    const onClick = (event: MouseEvent) => {
      if (!lenis || event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey) return;
      const link = (event.target as Element | null)?.closest<HTMLAnchorElement>('a[href^="#"]');
      const hash = link?.getAttribute('href') ?? '';
      if (hash.length < 2) return;
      const target = document.getElementById(decodeURIComponent(hash.slice(1)));
      if (!target) return;
      event.preventDefault();
      history.pushState(null, '', hash);
      lenis.scrollTo(target, {
        // no offset: Lenis honours the page's scroll-padding-top (base.css), which keeps headings clear of the header
        duration: 1.1,
        easing: glide,
        onComplete: () => {
          if (!target.hasAttribute('tabindex') && !target.matches('a, button, input, select, textarea')) {
            target.setAttribute('tabindex', '-1');
          }
          target.focus({ preventScroll: true });
        },
      });
    };

    const start = () => {
      if (lenis) return;
      lenis = new Lenis({
        lerp: 0.12,
        wheelMultiplier: 1,
        syncTouch: false,
        autoRaf: false,
        anchors: false,
        stopInertiaOnNavigate: true,
      });
      lenis.on('scroll', ScrollTrigger.update);
      gsap.ticker.add(tick);
      gsap.ticker.lagSmoothing(0);
      document.addEventListener('click', onClick);
    };

    const stop = () => {
      if (!lenis) return;
      document.removeEventListener('click', onClick);
      gsap.ticker.remove(tick);
      gsap.ticker.lagSmoothing(500, 33);
      lenis.destroy();
      lenis = null;
    };

    const sync = () => (reduced.matches ? stop() : start());
    sync();
    reduced.addEventListener('change', sync);
    return () => {
      reduced.removeEventListener('change', sync);
      stop();
    };
  }, []);
}
