// 文件用途：四边浮条状态、真实移出即回缩、输入刷新保护与拖动吸附；没有收起定时器。
import { DEMO_CONFIG, EDGE_LABELS } from './config.js';
import { byId } from './dom.js';
import { clamp, dockLayout, nearestPlacement, readPlacement, savePlacement, thumbRect } from './dock-geometry.js';
import { EdgeDrag } from './edge-drag.js';

export class EdgeIsland {
  /** @type {HTMLElement} */ #dock = byId('edge-dock');
  /** @type {HTMLElement} */ #surface = byId('island-surface');
  /** @type {HTMLElement} */ #panel = byId('edge-panel');
  /** @type {HTMLButtonElement} */ #trigger = byId('edge-trigger');
  /** @type {import('./search-view.js').SearchView} */ #view;
  #placement = readPlacement();
  #enabled = true; #open = false; #hovered = false; #suppressHover = false; #held = false; #dragging = false;

  /** @param {import('./search-view.js').SearchView} view */
  constructor(view) {
    this.#view = view; this.#dock.dataset.open = 'false'; this.#dock.dataset.dragging = 'false';
    this.#updateGeometry(); this.#dock.getBoundingClientRect(); this.#dock.dataset.ready = 'true'; this.#bindHover(); this.#bindKeys();
    new EdgeDrag([this.#trigger, byId('edge-drag')], { start: () => this.#startDrag(), move: point => this.#moveDrag(point), end: point => this.#endDrag(point) });
    this.#trigger.addEventListener('click', () => this.open(true));
    byId('edge-drag').addEventListener('click', () => this.#view.focus());
    byId('edge-collapse').addEventListener('click', () => this.close(true, true));
    new ResizeObserver(() => { if (!this.#dragging) this.#updateGeometry(); }).observe(this.#panel);
    window.addEventListener('resize', () => { if (!this.#dragging) this.#updateGeometry(); });
  }
  get isOpen() { return this.#open; }
  /** @param {boolean} focus */
  open(focus = false) {
    if (!this.#enabled || this.#dragging) return;
    this.#open = true; this.#dock.dataset.open = 'true'; this.#panel.inert = false; this.#panel.setAttribute('aria-hidden', 'false');
    this.#trigger.setAttribute('aria-expanded', 'true');
    if (focus) this.#view.focus();
  }
  /** @param {boolean} force @param {boolean} restoreFocus */
  close(force = false, restoreFocus = false) {
    if ((!force && this.#held) || !this.#open) return;
    this.#open = false; this.#dock.dataset.open = 'false'; this.#suppressHover = this.#hovered;
    this.#view.commitDraft();
    if (this.#panel.contains(document.activeElement) && document.activeElement instanceof HTMLElement) document.activeElement.blur();
    this.#panel.inert = true; this.#panel.setAttribute('aria-hidden', 'true'); this.#trigger.setAttribute('aria-expanded', 'false');
    if (restoreFocus && this.#enabled) this.focusTrigger();
  }
  /** @param {boolean} enabled */
  setEnabled(enabled) { this.#enabled = enabled; if (!enabled) this.close(true); this.#dock.inert = !enabled; }
  /** @param {boolean} held */
  hold(held) { this.#held = held; if (!held && !this.#hovered) this.close(false, true); }
  focusTrigger() { this.#trigger.focus({ preventScroll: true }); }
  #bindHover() {
    this.#dock.addEventListener('pointerenter', event => {
      if (event.pointerType === 'touch' || this.#dragging) return;
      this.#hovered = true; if (!this.#suppressHover) this.open();
    });
    this.#dock.addEventListener('pointerleave', event => {
      if (this.#dragging || event.pointerType === 'touch') return;
      // 输入和结果刷新会重新计算命中区域；边界事件不等于鼠标真的离开。
      if (this.#open && ((event.relatedTarget instanceof Node && this.#dock.contains(event.relatedTarget)) || this.#containsPointer(event))) return;
      this.#hovered = false; this.#suppressHover = false; this.close();
    });
    this.#panel.addEventListener('focusout', event => {
      if (this.#open && !this.#held && !this.#hovered && !(event.relatedTarget instanceof Node && this.#panel.contains(event.relatedTarget))) this.close();
    });
    document.addEventListener('pointermove', event => {
      if (this.#dragging || event.pointerType === 'touch') return;
      const outside = event.target instanceof Node && !this.#dock.contains(event.target);
      if (this.#suppressHover && outside) this.#suppressHover = false;
      // 即使先忽略了布局引发的 pointerleave，真实移动到外面也会在本次事件内收起。
      if (this.#open && !this.#held && outside && !this.#containsPointer(event)) {
        this.#hovered = false; this.#suppressHover = false; this.close();
      }
    });
    document.addEventListener('pointerdown', event => {
      if (this.#open && !this.#held && event.target instanceof Node && !this.#dock.contains(event.target)) this.close(true);
    });
  }
  /** @param {PointerEvent} event */
  #containsPointer(event) {
    const rect = this.#surface.getBoundingClientRect();
    return event.clientX >= rect.left && event.clientX < rect.right && event.clientY >= rect.top && event.clientY < rect.bottom;
  }
  #bindKeys() {
    document.addEventListener('keydown', event => {
      const edgeKeys = { ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'top', ArrowDown: 'bottom' };
      if (event.altKey && Object.hasOwn(edgeKeys, event.key) && (event.target === this.#trigger || event.target === byId('edge-drag'))) {
        event.preventDefault(); this.close(true); this.#placement = { edge: /** @type {import('./dock-geometry.js').DockEdge} */ (edgeKeys[event.key]), position: this.#placement.position };
        this.#hovered = false; this.#suppressHover = true; this.#updateGeometry(); savePlacement(this.#placement); this.focusTrigger(); return;
      }
      if (event.key !== 'Escape' || event.isComposing || this.#held || !this.#open) return;
      event.preventDefault(); this.close(true, true);
    });
  }
  /** @returns {import('./dock-geometry.js').WorkArea} */
  #area() { return { width: innerWidth, height: innerHeight - byId('tray-button').closest('.taskbar').getBoundingClientRect().height }; }
  #updateGeometry() {
    const area = this.#area();
    let layout = dockLayout(this.#placement, area, this.#panel.offsetHeight);
    byId('edge-search').dataset.resultsAbove = String(layout.above);
    this.#surface.style.width = `${layout.surface.width}px`;
    this.#panel.style.setProperty('--results-max-height', `${Math.max(layout.maxHeight - DEMO_CONFIG.dock.thumbLength, 0)}px`);
    layout = dockLayout(this.#placement, area, this.#panel.offsetHeight);
    const { thumb, surface, clip } = layout;
    this.#dock.dataset.edge = this.#placement.edge;
    this.#dock.style.width = `${thumb.width}px`; this.#dock.style.height = `${thumb.height}px`;
    this.#dock.style.setProperty('--dock-x', `${thumb.x}px`); this.#dock.style.setProperty('--dock-y', `${thumb.y}px`);
    this.#surface.style.left = `${surface.x - thumb.x}px`;
    // 向上伸展时由 CSS 锚定底边，结果高度变化不会等 ResizeObserver 后才纠正输入框位置。
    this.#surface.style.top = layout.above ? 'auto' : `${surface.y - thumb.y}px`;
    this.#surface.style.bottom = layout.above ? '0px' : 'auto';
    this.#surface.style.setProperty('--closed-clip', `inset(${clip.top}px ${clip.right}px ${clip.bottom}px ${clip.left}px round ${layout.radii})`);
    this.#surface.style.setProperty('--island-radii', layout.radii);
    this.#positionGuide(thumb);
  }
  /** @param {import('./dock-geometry.js').DockRect} thumb */
  #positionGuide(thumb) {
    const guide = byId('edge-guide'); const margin = DEMO_CONFIG.dock.margin * 2;
    guide.dataset.edge = this.#placement.edge;
    const x = this.#placement.edge === 'right' ? thumb.x - margin : this.#placement.edge === 'left' ? thumb.x + thumb.width + margin : thumb.x + thumb.width / 2;
    const y = this.#placement.edge === 'top' ? thumb.y + thumb.height + margin : this.#placement.edge === 'bottom' ? thumb.y - margin : thumb.y + thumb.height / 2;
    guide.style.left = `${x}px`; guide.style.top = `${y}px`;
  }
  #startDrag() {
    this.close(true); this.#dragging = true; this.#dock.dataset.dragging = 'true';
    this.#dock.style.width = `${DEMO_CONFIG.dock.dragWidth}px`; this.#dock.style.height = `${DEMO_CONFIG.dock.thumbLength}px`;
  }
  /** @param {import('./dock-geometry.js').PointerPoint} point */
  #moveDrag(point) {
    const area = this.#area(); const config = DEMO_CONFIG.dock;
    this.#dock.style.setProperty('--dock-x', `${clamp(point.x - config.dragWidth / 2, 0, area.width - config.dragWidth)}px`);
    this.#dock.style.setProperty('--dock-y', `${clamp(point.y - config.thumbLength / 2, 0, area.height - config.thumbLength)}px`);
    const candidate = nearestPlacement(point, area); const target = byId('dock-target'); const rect = thumbRect(candidate, area);
    target.dataset.visible = 'true'; target.dataset.edge = candidate.edge;
    target.style.left = `${rect.x}px`; target.style.top = `${rect.y}px`; target.style.width = `${rect.width}px`; target.style.height = `${rect.height}px`;
    byId('dock-target-label').textContent = `松开吸附${EDGE_LABELS[candidate.edge]}`;
  }
  /** @param {import('./dock-geometry.js').PointerPoint} point */
  #endDrag(point) {
    this.#dock.getBoundingClientRect(); this.#dragging = false; this.#hovered = false; this.#suppressHover = true;
    this.#dock.dataset.dragging = 'false'; byId('dock-target').dataset.visible = 'false';
    this.#placement = nearestPlacement(point, this.#area()); this.#updateGeometry(); savePlacement(this.#placement);
  }
}
