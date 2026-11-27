import { gsap } from 'gsap';
import { ScrollTrigger } from 'gsap/ScrollTrigger';
import { SplitText } from 'gsap/SplitText';
import { CustomEase } from 'gsap/CustomEase';
import { useGSAP } from '@gsap/react';

// Register once. Every component imports gsap from here so plugins and eases are always present.
gsap.registerPlugin(ScrollTrigger, SplitText, CustomEase, useGSAP);

/*
 * Motion tokens. One set of eases and durations for the whole page, so every movement belongs to the same hand.
 * The CSS mirrors the eases as --ease-* custom properties (tokens.css) for hover transitions.
 */
CustomEase.create('pl.out', '0.16, 1, 0.3, 1'); // arrivals: quick start, long soft landing
CustomEase.create('pl.inOut', '0.65, 0, 0.35, 1'); // a drafting move between two settled states
CustomEase.create('pl.draw', '0.45, 0, 0.15, 1'); // a pen stroke: pen down, sweep, ease off at the end
CustomEase.create('pl.camera', '0.76, 0, 0.24, 1'); // the camera: patient start and stop, nothing abrupt
CustomEase.create('pl.stamp', '0.34, 1.4, 0.64, 1'); // a small overshoot, for things set down (columns, stamps)
CustomEase.create('pl.in', '0.55, 0, 0.75, 0'); // leaving

export const EASE = {
  out: 'pl.out',
  inOut: 'pl.inOut',
  draw: 'pl.draw',
  camera: 'pl.camera',
  stamp: 'pl.stamp',
  in: 'pl.in',
} as const;

/** seconds */
export const DUR = {
  xs: 0.18,
  s: 0.32,
  m: 0.56,
  l: 0.9,
  xl: 1.4,
  camera: 1.9,
} as const;

/** seconds between siblings */
export const STAGGER = {
  chars: 0.016,
  tight: 0.035,
  base: 0.07,
  loose: 0.12,
} as const;

/** gsap.matchMedia conditions used everywhere */
export const MQ = {
  motion: '(prefers-reduced-motion: no-preference)',
  reduced: '(prefers-reduced-motion: reduce)',
  /** a mouse or trackpad: crosshair cursor and magnetic buttons only here */
  fine: '(prefers-reduced-motion: no-preference) and (pointer: fine) and (hover: hover)',
} as const;

gsap.defaults({ ease: EASE.out, duration: DUR.m });

/** True when the visitor asked the OS for reduced motion. Read at animation setup time. */
export function prefersReducedMotion(): boolean {
  return typeof window !== 'undefined' && window.matchMedia(MQ.reduced).matches;
}

export { gsap, ScrollTrigger, SplitText, useGSAP };
