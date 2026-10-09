// 文件用途：组装主窗口、悬浮搜索、托盘和模拟资源管理器；不调用原软件。
import { DEMO_CONFIG } from './config.js';
import { SAMPLE_FILES } from './files.js';
import { byId, icon } from './dom.js';
import { SearchModel } from './search-model.js';
import { SearchView } from './search-view.js';
import { EdgeIsland } from './edge-island.js';

class DemoApp {
  #model = new SearchModel();
  /** @type {SearchView} */ #edgeView;
  /** @type {SearchView} */ #mainView;
  /** @type {EdgeIsland} */ #island;
  /** @type {HTMLDialogElement} */ #locator = byId('locator');
  /** @type {HTMLElement|null} */ #returnFocus = null;
  /** @type {ReturnType<typeof setTimeout>|undefined} */ #toastTimer;

  constructor() {
    this.#edgeView = new SearchView(byId('edge-search'), this.#model, file => this.#locate(file), { compact: true });
    this.#mainView = new SearchView(byId('main-search'), this.#model, file => this.#locate(file));
    this.#island = new EdgeIsland(this.#edgeView);
    this.#bindControls(); this.#bindTray(); this.#bindLocator();
    document.addEventListener('keydown', event => {
      if (event.isComposing || this.#locator.open) return;
      if (event.key === '/' && !(event.target instanceof HTMLInputElement) && !(event.target instanceof HTMLTextAreaElement) && !(event.target instanceof HTMLElement && event.target.isContentEditable)) {
        event.preventDefault(); this.#showQuickSearch();
      }
      if (event.key === 'Escape' && !byId('tray-menu').hidden) {
        this.#closeTray(); byId('tray-button').focus({ preventScroll: true });
      }
    });
  }
  #bindControls() {
    byId('try-search').addEventListener('click', () => this.#showQuickSearch());
    byId('show-main').addEventListener('click', () => this.#showWindow());
    byId('edge-restore').addEventListener('click', () => this.#showWindow());
    byId('minimize-main').addEventListener('click', () => {
      this.#setWindow(false); this.#island.focusTrigger(); this.#toast('已收纳到托盘 · 悬停贴边入口即可搜索');
    });
    document.querySelectorAll('[data-example]').forEach(button => button.addEventListener('click', () => {
      this.#model.setCategory('all'); this.#model.setQuery(/** @type {HTMLElement} */ (button).dataset.example ?? ''); this.#showQuickSearch();
    }));
  }
  #bindTray() {
    const tray = byId('tray-button'); const menu = byId('tray-menu');
    tray.addEventListener('click', () => { menu.hidden = !menu.hidden; tray.setAttribute('aria-expanded', String(!menu.hidden)); });
    tray.addEventListener('dblclick', () => this.#showWindow());
    tray.addEventListener('contextmenu', event => { event.preventDefault(); menu.hidden = false; tray.setAttribute('aria-expanded', 'true'); });
    byId('tray-restore').addEventListener('click', () => this.#showWindow());
    byId('tray-search').addEventListener('click', () => this.#showQuickSearch());
    document.addEventListener('pointerdown', event => {
      const area = document.querySelector('.tray-area');
      if (event.target instanceof Node && !area?.contains(event.target)) this.#closeTray();
    });
  }
  #bindLocator() {
    byId('locator-close').addEventListener('click', () => this.#locator.close());
    this.#locator.addEventListener('click', event => {
      if (event.target !== this.#locator) return;
      const bounds = this.#locator.getBoundingClientRect();
      if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom) this.#locator.close();
    });
    this.#locator.addEventListener('close', () => { this.#island.hold(false); this.#returnFocus?.focus({ preventScroll: true }); });
  }
  /** @param {boolean} visible */
  #setWindow(visible) {
    document.body.dataset.mode = visible ? 'window' : 'tray';
    byId('main-window').inert = !visible; byId('main-window').setAttribute('aria-hidden', String(!visible));
    this.#island.setEnabled(!visible); byId('mode-label').textContent = visible ? '主窗口已恢复' : '已收纳到托盘'; this.#closeTray();
  }
  #showWindow() { this.#setWindow(true); this.#mainView.focus(); }
  #showQuickSearch() { this.#setWindow(false); this.#island.open(true); }
  #closeTray() { byId('tray-menu').hidden = true; byId('tray-button').setAttribute('aria-expanded', 'false'); }
  /** @param {import('./files.js').DemoFile} file */
  #locate(file) {
    this.#returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    this.#island.hold(true); byId('locator-path').textContent = file.folder;
    const list = byId('locator-files'); list.replaceChildren();
    for (const sibling of SAMPLE_FILES.filter(candidate => candidate.folder === file.folder)) {
      const row = document.createElement('li'); row.className = 'locator-file'; row.setAttribute('aria-current', String(sibling.id === file.id));
      const name = document.createElement('span'); const text = document.createElement('span'); text.className = 'locator-file-name'; text.textContent = sibling.name; name.append(icon(sibling.category), text);
      const size = document.createElement('span'); size.textContent = sibling.size; row.append(name, size); list.append(row);
    }
    this.#locator.showModal(); byId('locator-close').focus({ preventScroll: true });
    list.querySelector('[aria-current="true"]')?.scrollIntoView({ block: 'nearest' });
  }
  /** @param {string} message */
  #toast(message) {
    const toast = byId('toast'); clearTimeout(this.#toastTimer); toast.textContent = message; toast.dataset.visible = 'true';
    this.#toastTimer = setTimeout(() => { toast.dataset.visible = 'false'; }, DEMO_CONFIG.toastDurationMs);
  }
}

new DemoApp();
