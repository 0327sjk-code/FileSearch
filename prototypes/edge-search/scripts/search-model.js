// 文件用途：只按文件名筛选，共享便捷栏与主窗口的查询状态。
import { SAMPLE_FILES } from './files.js';
import { CATEGORY_LABELS } from './config.js';

/** @typedef {'all'|'image'|'video'} SearchCategory */
/** @typedef {{query: string, category: SearchCategory, tokens: ReadonlyArray<string>, results: ReadonlyArray<import('./files.js').DemoFile>}} SearchSnapshot */

export class SearchModel {
  /** @type {Set<(snapshot: SearchSnapshot) => void>} */
  #listeners = new Set();
  /** @type {ReadonlyArray<import('./files.js').DemoFile>} */
  #files;
  #query = '';
  /** @type {SearchCategory} */
  #category = 'all';

  /** @param {ReadonlyArray<import('./files.js').DemoFile>} files */
  constructor(files = SAMPLE_FILES) { this.#files = files; }
  /** @returns {ReadonlyArray<string>} */
  get drives() { return [...new Set(this.#files.map(file => file.path.slice(0, 2)))].sort(); }
  /** @returns {SearchSnapshot} */
  get snapshot() {
    const tokens = this.#query.normalize('NFKC').trim().toLocaleLowerCase().split(/\s+/u).filter(Boolean);
    const results = this.#files.filter(file => {
      const name = file.name.normalize('NFKC').toLocaleLowerCase();
      return (this.#category === 'all' || file.category === this.#category) && tokens.every(token => name.includes(token));
    });
    return { query: this.#query, category: this.#category, tokens, results };
  }
  /** @param {string} query */
  setQuery(query) { this.#query = query; this.#publish(); }
  /** @param {SearchCategory} category */
  setCategory(category) {
    if (!Object.hasOwn(CATEGORY_LABELS, category)) return;
    this.#category = category; this.#publish();
  }
  /** @param {(snapshot: SearchSnapshot) => void} listener @returns {() => void} */
  subscribe(listener) { this.#listeners.add(listener); listener(this.snapshot); return () => this.#listeners.delete(listener); }
  #publish() { const snapshot = this.snapshot; for (const listener of this.#listeners) listener(snapshot); }
}
