import { gsap } from 'gsap';
import { ScrollTrigger } from 'gsap/ScrollTrigger';
import { useGSAP } from '@gsap/react';

// Register once. Every component imports gsap from here so plugins are always present.
gsap.registerPlugin(ScrollTrigger, useGSAP);

/** True when the visitor asked the OS for reduced motion. Read at animation setup time. */
export function prefersReducedMotion(): boolean {
  return typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

export { gsap, ScrollTrigger, useGSAP };
