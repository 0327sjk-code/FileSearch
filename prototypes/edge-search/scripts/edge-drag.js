// 文件用途：把手与窄入口共用的指针捕获拖动；只在拖动过程中请求动画帧。
import { DEMO_CONFIG } from './config.js';

/** @typedef {import('./dock-geometry.js').PointerPoint} DragPoint */
/** @typedef {{start: () => void, move: (point: DragPoint) => void, end: (point: DragPoint) => void}} DragHandlers */
export class EdgeDrag {
  /** @type {{pointerId: number, origin: DragPoint, last: DragPoint, target: HTMLElement, moved: boolean}|undefined} */ #active;
  /** @type {DragHandlers} */ #handlers;
  /** @type {number|undefined} */ #frame;
  #cancelClick = false;

  /** @param {HTMLElement[]} handles @param {DragHandlers} handlers */
  constructor(handles, handlers) {
    this.#handlers = handlers;
    for (const handle of handles) {
      handle.addEventListener('pointerdown', event => this.#down(event, handle));
      handle.addEventListener('pointermove', event => this.#move(event));
      handle.addEventListener('pointerup', event => this.#finish(event));
      handle.addEventListener('pointercancel', event => this.#finish(event));
      handle.addEventListener('lostpointercapture', event => this.#finish(event));
      handle.addEventListener('click', event => { if (this.#cancelClick) { event.preventDefault(); event.stopImmediatePropagation(); } }, true);
    }
  }
  /** @param {PointerEvent} event @param {HTMLElement} target */
  #down(event, target) {
    if (event.button !== 0 || !event.isPrimary || this.#active) return;
    event.preventDefault();
    const point = { x: event.clientX, y: event.clientY };
    this.#active = { pointerId: event.pointerId, origin: point, last: point, target, moved: false };
    target.setPointerCapture(event.pointerId);
  }
  /** @param {PointerEvent} event */
  #move(event) {
    const active = this.#active;
    if (!active || active.pointerId !== event.pointerId) return;
    active.last = { x: event.clientX, y: event.clientY };
    if (!active.moved) {
      if (Math.hypot(event.clientX - active.origin.x, event.clientY - active.origin.y) < DEMO_CONFIG.dock.dragThreshold) return;
      active.moved = true; this.#cancelClick = true; this.#handlers.start();
    }
    if (this.#frame === undefined) this.#frame = requestAnimationFrame(() => { this.#frame = undefined; if (this.#active) this.#handlers.move(this.#active.last); });
  }
  /** @param {PointerEvent} event */
  #finish(event) {
    const active = this.#active;
    if (!active || active.pointerId !== event.pointerId) return;
    this.#active = undefined;
    if (this.#frame !== undefined) cancelAnimationFrame(this.#frame);
    this.#frame = undefined;
    if (active.target.hasPointerCapture(event.pointerId)) active.target.releasePointerCapture(event.pointerId);
    if (active.moved) { this.#handlers.move(active.last); this.#handlers.end(active.last); }
    setTimeout(() => { this.#cancelClick = false; }, 0);
  }
}
