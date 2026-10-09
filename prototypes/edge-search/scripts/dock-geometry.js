// 文件用途：四边停靠、归一化位置保存和浮条可见区域的纯几何计算。
import { DEMO_CONFIG, EDGE_LABELS } from './config.js';

/** @typedef {'left'|'right'|'top'|'bottom'} DockEdge */
/** @typedef {{edge: DockEdge, position: number}} DockPlacement */
/** @typedef {{x: number, y: number}} PointerPoint */
/** @typedef {{width: number, height: number}} WorkArea */
/** @typedef {{x: number, y: number, width: number, height: number}} DockRect */

/** @param {number} value @param {number} min @param {number} max */
export function clamp(value, min, max) { return Math.min(Math.max(value, min), Math.max(min, max)); }
/** @param {number} position @param {number} length */
function axisCenter(position, length) {
  const inset = Math.min(DEMO_CONFIG.dock.thumbLength / 2 + DEMO_CONFIG.dock.margin, length / 2);
  return inset + clamp(position, 0, 1) * Math.max(length - inset * 2, 0);
}
/** @param {number} coordinate @param {number} length */
function axisPosition(coordinate, length) {
  const inset = Math.min(DEMO_CONFIG.dock.thumbLength / 2 + DEMO_CONFIG.dock.margin, length / 2);
  return clamp((coordinate - inset) / Math.max(length - inset * 2, 1), 0, 1);
}
/** @param {PointerPoint} point @param {WorkArea} area @returns {DockPlacement} */
export function nearestPlacement(point, area) {
  const distances = { left: Math.abs(point.x), right: Math.abs(area.width - point.x), top: Math.abs(point.y), bottom: Math.abs(area.height - point.y) };
  const edge = /** @type {DockEdge} */ (Object.keys(distances).reduce((closest, candidate) => distances[candidate] < distances[closest] ? candidate : closest));
  return { edge, position: axisPosition(edge === 'left' || edge === 'right' ? point.y : point.x, edge === 'left' || edge === 'right' ? area.height : area.width) };
}
/** @param {DockPlacement} placement @param {WorkArea} area @returns {DockRect} */
export function thumbRect(placement, area) {
  const { peekSize, thumbLength } = DEMO_CONFIG.dock;
  const vertical = placement.edge === 'left' || placement.edge === 'right';
  const center = axisCenter(placement.position, vertical ? area.height : area.width);
  if (vertical) return { x: placement.edge === 'right' ? area.width - peekSize : 0, y: center - thumbLength / 2, width: peekSize, height: thumbLength };
  return { x: center - thumbLength / 2, y: placement.edge === 'bottom' ? area.height - peekSize : 0, width: thumbLength, height: peekSize };
}
/** @param {DockPlacement} placement @param {WorkArea} area @param {number} panelHeight */
export function dockLayout(placement, area, panelHeight) {
  const thumb = thumbRect(placement, area);
  const { margin, panelWidth, thumbLength } = DEMO_CONFIG.dock;
  const vertical = placement.edge === 'left' || placement.edge === 'right';
  const above = placement.edge === 'bottom' || (vertical && placement.position > .5);
  const maxHeight = vertical ? (above ? thumb.y + thumb.height - margin : area.height - thumb.y - margin) : area.height - margin;
  const width = Math.min(panelWidth, area.width - margin * 2);
  const height = Math.min(panelHeight, maxHeight);
  const x = vertical ? (placement.edge === 'right' ? area.width - width : 0) : clamp(thumb.x + thumbLength / 2 - width / 2, margin, area.width - width - margin);
  const y = vertical ? clamp(above ? thumb.y + thumb.height - height : thumb.y, 0, area.height - height) : placement.edge === 'bottom' ? area.height - height : 0;
  const surface = { x, y, width, height };
  const radii = { right: '16px 0 0 16px', left: '0 16px 16px 0', top: '0 0 16px 16px', bottom: '16px 16px 0 0' }[placement.edge];
  const clip = { top: Math.max(thumb.y - y, 0), right: Math.max(x + width - thumb.x - thumb.width, 0), bottom: Math.max(y + height - thumb.y - thumb.height, 0), left: Math.max(thumb.x - x, 0) };
  return { thumb, surface, clip, maxHeight, above, radii };
}
/** @returns {DockPlacement} */
export function readPlacement() {
  const fallback = { edge: /** @type {DockEdge} */ (DEMO_CONFIG.dock.defaultEdge), position: DEMO_CONFIG.dock.defaultPosition };
  try {
    const saved = JSON.parse(localStorage.getItem(DEMO_CONFIG.dock.storageKey) ?? 'null');
    if (saved && Object.hasOwn(EDGE_LABELS, saved.edge) && Number.isFinite(saved.position)) return { edge: saved.edge, position: clamp(saved.position, 0, 1) };
  } catch { /* 浏览器禁用本地存储时仍可拖动；使用默认位置。 */ }
  return fallback;
}
/** @param {DockPlacement} placement */
export function savePlacement(placement) {
  try { localStorage.setItem(DEMO_CONFIG.dock.storageKey, JSON.stringify(placement)); } catch { /* 文件页限制本地存储时不影响交互。 */ }
}
