// 文件用途：安全创建文本、统一线性图标和文件名匹配高亮。
/** @template {HTMLElement} T @param {string} id @returns {T} */
export function byId(id) {
  const element = document.getElementById(id);
  if (!element) throw new Error(`缺少界面元素：${id}`);
  return /** @type {T} */ (element);
}

/** @param {string} name @returns {SVGSVGElement} */
export function icon(name) {
  const namespace = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(namespace, 'svg');
  svg.classList.add('icon');
  svg.setAttribute('aria-hidden', 'true');
  const use = document.createElementNS(namespace, 'use');
  use.setAttribute('href', `#icon-${name}`);
  svg.append(use);
  return svg;
}

/** @param {string} name @param {ReadonlyArray<string>} tokens @returns {DocumentFragment} */
export function highlightedName(name, tokens) {
  const output = document.createDocumentFragment();
  const lower = name.toLocaleLowerCase();
  const indices = new Set();
  for (const token of tokens) {
    let start = lower.indexOf(token);
    while (start !== -1) {
      for (let offset = 0; offset < token.length; offset++) indices.add(start + offset);
      start = lower.indexOf(token, start + Math.max(token.length, 1));
    }
  }
  let start = 0;
  while (start < name.length) {
    const matching = indices.has(start);
    let end = start + 1;
    while (end < name.length && indices.has(end) === matching) end++;
    const text = name.slice(start, end);
    if (matching) { const mark = document.createElement('mark'); mark.textContent = text; output.append(mark); }
    else output.append(document.createTextNode(text));
    start = end;
  }
  return output;
}
