// 文件用途：可复用的文件名搜索表单、分类、全部结果与键盘定位。
import { CATEGORY_LABELS } from './config.js';
import { highlightedName, icon } from './dom.js';
import { SearchModel } from './search-model.js';

export class SearchView {
  /** @type {HTMLInputElement} */ input;
  /** @type {HTMLButtonElement} */ #clear;
  /** @type {HTMLElement} */ #count;
  /** @type {HTMLUListElement} */ #list;
  /** @type {ReadonlyArray<import('./files.js').DemoFile>} */ #results = [];
  /** @type {HTMLButtonElement[]} */ #tabs;
  #activeIndex = -1;
  #composing = false;
  /** @type {SearchModel} */ #model;
  /** @type {(file: import('./files.js').DemoFile) => void} */ #locate;
  /** @type {HTMLElement} */ #root;
  /** @type {HTMLElement} */ #body;
  #compact = false;

  /** @param {HTMLElement} root @param {SearchModel} model @param {(file: import('./files.js').DemoFile) => void} locate @param {{compact?: boolean}} options */
  constructor(root, model, locate, options = {}) {
    this.#model = model; this.#locate = locate; this.#root = root; this.#compact = options.compact ?? false;
    root.setAttribute('role', 'search'); root.setAttribute('aria-label', '搜索示例文件名');
    const dragHandle = this.#compact ? '<button class="icon-button drag-handle" id="edge-drag" type="button" aria-label="拖动浮条，松开后吸附到最近边缘" title="拖动浮条 · 松开吸附四边"><svg class="icon" aria-hidden="true"><use href="#icon-grip"></use></svg></button>' : '';
    const actions = this.#compact ? '<button class="icon-button edge-restore" id="edge-restore" type="button" aria-label="打开主窗口" title="打开主窗口"><svg class="icon" aria-hidden="true"><use href="#icon-expand"></use></svg></button><button class="icon-button edge-collapse" id="edge-collapse" type="button" aria-label="收起便捷搜索" title="收起 · Esc"><svg class="icon" aria-hidden="true"><use href="#icon-chevron"></use></svg></button>' : '';
    root.innerHTML = `<div class="search-field">${dragHandle}<svg class="icon" aria-hidden="true"><use href="#icon-search"></use></svg><label class="visually-hidden" for="${root.id}-input">输入文件名</label><input class="search-input" id="${root.id}-input" type="search" placeholder="搜索文件名…" autocomplete="off" spellcheck="false" maxlength="256"><button class="icon-button clear-search" type="button" aria-label="清空搜索" title="清空" hidden><svg class="icon" aria-hidden="true"><use href="#icon-close"></use></svg></button>${actions}</div><div class="search-body"><div class="search-body-content"><div class="search-toolbar"><div class="search-tabs" aria-label="文件类别"></div><span class="search-count" role="status" aria-live="polite" aria-atomic="true"></span></div><ul class="result-list" aria-label="搜索结果"></ul><footer class="search-footer"><span class="search-scope"><svg class="icon" aria-hidden="true"><use href="#icon-drive"></use></svg><span>本机硬盘 · ${model.drives.join(' / ')}</span></span><span><kbd>↑↓</kbd> 选择 · <kbd>Enter</kbd> 定位</span></footer></div></div>`;
    this.input = /** @type {HTMLInputElement} */ (root.querySelector('.search-input'));
    this.#clear = /** @type {HTMLButtonElement} */ (root.querySelector('.clear-search'));
    this.#count = /** @type {HTMLElement} */ (root.querySelector('.search-count'));
    this.#list = /** @type {HTMLUListElement} */ (root.querySelector('.result-list'));
    this.#body = /** @type {HTMLElement} */ (root.querySelector('.search-body'));
    const tabs = /** @type {HTMLElement} */ (root.querySelector('.search-tabs'));
    this.#tabs = Object.entries(CATEGORY_LABELS).map(([category, label]) => {
      const button = document.createElement('button'); button.type = 'button'; button.className = 'search-tab'; button.textContent = label; button.dataset.category = category;
      button.addEventListener('click', () => model.setCategory(/** @type {import('./search-model.js').SearchCategory} */ (category)));
      tabs.append(button); return button;
    });
    this.input.addEventListener('input', () => {
      this.#clear.hidden = !this.input.value;
      if (!this.#composing) model.setQuery(this.input.value);
    });
    this.input.addEventListener('compositionstart', () => { this.#composing = true; });
    this.input.addEventListener('compositionend', () => { this.#composing = false; model.setQuery(this.input.value); });
    this.input.addEventListener('keydown', event => this.#onKey(event));
    this.#clear.addEventListener('click', () => { model.setQuery(''); this.focus(); });
    model.subscribe(snapshot => this.#render(snapshot));
  }
  focus() { this.input.focus({ preventScroll: true }); }
  commitDraft() { this.#composing = false; this.#model.setQuery(this.input.value); }
  /** @param {import('./search-model.js').SearchSnapshot} snapshot */
  #render(snapshot) {
    if (!this.#composing && this.input.value !== snapshot.query) this.input.value = snapshot.query;
    this.#clear.hidden = !this.input.value;
    for (const tab of this.#tabs) tab.setAttribute('aria-pressed', String(tab.dataset.category === snapshot.category));
    this.#count.textContent = snapshot.tokens.length ? `${snapshot.results.length} 个结果` : `${snapshot.results.length} 个示例`;
    this.#results = snapshot.results; this.#activeIndex = -1; this.#list.replaceChildren(); this.#list.scrollTop = 0;
    const showResults = !this.#compact || snapshot.tokens.length > 0;
    this.#root.dataset.hasQuery = String(snapshot.tokens.length > 0); this.#body.inert = !showResults; this.#body.setAttribute('aria-hidden', String(!showResults));
    if (!showResults) { this.#results = []; return; }
    if (!snapshot.results.length) { this.#renderEmpty(snapshot.query); return; }
    const fragment = document.createDocumentFragment();
    for (const file of snapshot.results) {
      const row = document.createElement('li'); row.className = 'result-row'; row.dataset.fileId = file.id;
      const type = document.createElement('span'); type.className = 'file-type'; type.append(icon(file.category));
      const info = document.createElement('span'); info.className = 'file-info';
      const name = document.createElement('span'); name.className = 'file-name'; name.title = file.name; name.append(highlightedName(file.name, snapshot.tokens));
      const path = document.createElement('span'); path.className = 'file-path'; path.textContent = file.folder; path.title = file.path;
      info.append(name, path);
      const locate = document.createElement('button'); locate.type = 'button'; locate.className = 'icon-button locate-file'; locate.setAttribute('aria-label', `定位文件：${file.name}`); locate.title = '打开文件夹并选中文件（演示）'; locate.append(icon('folder'));
      locate.addEventListener('click', () => this.#locate(file));
      row.append(type, info, locate); fragment.append(row);
    }
    this.#list.append(fragment);
  }
  /** @param {string} query */
  #renderEmpty(query) {
    const row = document.createElement('li'); row.className = 'search-empty'; row.append(icon('search'));
    const title = document.createElement('strong'); title.textContent = `没有匹配的${CATEGORY_LABELS[this.#model.snapshot.category]}`;
    const description = document.createElement('p'); description.textContent = query.trim() ? '仅匹配文件名。试试“水面”“鱼”或“截图”。' : '这一类别暂无示例文件。';
    row.append(title, description); this.#list.append(row);
  }
  /** @param {KeyboardEvent} event */
  #onKey(event) {
    if (event.isComposing || this.#composing || !this.#results.length) return;
    if (event.key === 'Enter') { event.preventDefault(); this.#locate(this.#results[Math.max(this.#activeIndex, 0)]); return; }
    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
    event.preventDefault();
    this.#activeIndex = event.key === 'ArrowDown' ? Math.min(this.#activeIndex + 1, this.#results.length - 1) : Math.max(this.#activeIndex - 1, 0);
    const rows = [...this.#list.querySelectorAll('.result-row')];
    rows.forEach((row, index) => { /** @type {HTMLElement} */ (row).dataset.active = String(index === this.#activeIndex); });
    rows[this.#activeIndex]?.scrollIntoView({ block: 'nearest' });
  }
}
