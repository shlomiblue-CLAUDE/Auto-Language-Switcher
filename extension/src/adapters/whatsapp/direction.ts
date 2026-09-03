import { SELECTORS, TAIL_IN, TAIL_OUT, resolve } from './selectors.js';
import type { Direction } from '../../shared/protocol.js';

/**
 * Deciding whether a message was sent or received.
 *
 * This is the single most important reading the adapter makes. Getting it backwards would feed the
 * other person's language into a decision about the user's keyboard, which is the exact failure the
 * whole product is built to avoid — so it is measured three ways, and returns null rather than
 * guessing when they cannot agree.
 *
 * All three were established by measurement against a live session on 3 September 2026, not by
 * reading class names. Class names are what broke last time.
 */

export type DirectionEvidence = 'geometry' | 'sender-label' | 'tail' | 'none';

export interface DirectionReading {
  readonly direction: Direction | null;
  readonly evidence: DirectionEvidence;
}

export interface Rect {
  readonly left: number;
  readonly right: number;
  readonly width: number;
}

/** Injectable so geometry stays testable: jsdom performs no layout and returns zeros for everything. */
export type Measure = (element: Element) => Rect;

export const domMeasure: Measure = (element) => {
  const rect = element.getBoundingClientRect();
  return { left: rect.left, right: rect.right, width: rect.width };
};

/**
 * A bubble must sit clearly nearer one edge than the other. Long messages fill most of the panel,
 * leaving both gaps small and similar, and calling those would be a coin toss.
 */
const MIN_GAP_DIFFERENCE_PX = 24;

/**
 * Accessibility prefixes WhatsApp puts on the user's own messages, per UI language.
 *
 * Only a fallback: it needs a string per locale, and a missing locale silently costs the signal.
 * Geometry is preferred precisely because it needs none of this.
 */
const SELF_MARKERS: readonly RegExp[] = [
  /^\s*את\/ה\s*:/, // Hebrew
  /^\s*You\s*:/i, // English
  /^\s*Вы\s*:/i, // Russian
  /^\s*أنت\s*:/, // Arabic
];

/**
 * The tail testids mean the opposite of what they say.
 *
 * Measured across seven consecutive messages: every row carrying `tail-out` was the OTHER
 * person's, agreeing with both geometry and the sender label; every row the user sent had
 * `tail-in` or no tail at all. Presumably the name describes the tail's visual direction rather
 * than the message's.
 *
 * Recorded as a named constant because a bare inverted mapping in a conditional is a bug waiting
 * to be "fixed" by the next person who reads it.
 */
const TAIL_OUT_MEANS_INCOMING = true;

/**
 * Which side of the panel belongs to the user.
 *
 * WhatsApp defines an outgoing message as one drawn on the user's side, so this reads the
 * definition directly. In a left-to-right layout that is the right edge; in Hebrew or Arabic the
 * layout mirrors and it is the left.
 */
function outgoingSide(panelDirection: string): 'left' | 'right' {
  return panelDirection === 'rtl' ? 'left' : 'right';
}

function byGeometry(
  bubble: Element,
  panelRect: Rect,
  panelDirection: string,
  measure: Measure,
): Direction | null {
  const rect = measure(bubble);

  // Nothing has been laid out yet, so there is nothing to read.
  if (rect.width <= 0 || panelRect.width <= 0) return null;

  const leftGap = rect.left - panelRect.left;
  const rightGap = panelRect.right - rect.right;

  if (Math.abs(leftGap - rightGap) < MIN_GAP_DIFFERENCE_PX) return null;

  const side = leftGap < rightGap ? 'left' : 'right';
  return side === outgoingSide(panelDirection) ? 'outgoing' : 'incoming';
}

function bySenderLabel(row: Element): Direction | null {
  // Every message row carries exactly one label ending in a colon: the sender's name, or the
  // localised equivalent of "You".
  for (const element of row.querySelectorAll('[aria-label]')) {
    const label = element.getAttribute('aria-label') ?? '';
    if (!label.trim().endsWith(':')) continue;

    if (SELF_MARKERS.some((marker) => marker.test(label))) return 'outgoing';
    return 'incoming';
  }
  return null;
}

function byTail(row: Element): Direction | null {
  if (row.querySelector(TAIL_OUT)) return TAIL_OUT_MEANS_INCOMING ? 'incoming' : 'outgoing';
  if (row.querySelector(TAIL_IN)) return TAIL_OUT_MEANS_INCOMING ? 'outgoing' : 'incoming';

  // Tails appear only on the first message of a group, so their absence says nothing.
  return null;
}

export interface PanelContext {
  readonly rect: Rect;
  readonly direction: string;
}

export function panelContext(panel: Element, measure: Measure = domMeasure): PanelContext {
  return {
    rect: measure(panel),
    // getComputedStyle is unavailable in some test environments; LTR is the safer assumption
    // because it is the majority case and geometry simply declines when it cannot decide.
    direction: typeof getComputedStyle === 'function' ? getComputedStyle(panel).direction : 'ltr',
  };
}

/** The bubble to measure, or null when the row is not a message at all. */
export function bubbleOf(row: Element): Element | null {
  return resolve(SELECTORS.messageBubble, row).element;
}

export function detectDirection(
  row: Element,
  panel: PanelContext,
  measure: Measure = domMeasure,
): DirectionReading {
  const bubble = bubbleOf(row);
  if (bubble) {
    const geometric = byGeometry(bubble, panel.rect, panel.direction, measure);
    if (geometric) return { direction: geometric, evidence: 'geometry' };
  }

  const labelled = bySenderLabel(row);
  if (labelled) return { direction: labelled, evidence: 'sender-label' };

  const tailed = byTail(row);
  if (tailed) return { direction: tailed, evidence: 'tail' };

  return { direction: null, evidence: 'none' };
}
