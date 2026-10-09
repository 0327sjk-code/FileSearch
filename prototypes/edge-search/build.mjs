// 文件用途：把模块化源码打包为一个无联网、无安装依赖的 HTML 文件。
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const sourceRoot = dirname(fileURLToPath(import.meta.url));
const output = process.argv[2];
if (!output) throw new Error('需要指定输出 HTML 的绝对路径。');

const styleFiles = ['tokens.css', 'desktop.css', 'island.css', 'search.css'];
const scriptFiles = ['config.js', 'files.js', 'dom.js', 'search-model.js', 'search-view.js', 'dock-geometry.js', 'edge-drag.js', 'edge-island.js', 'app.js'];
const styles = await Promise.all(styleFiles.map(name => readFile(resolve(sourceRoot, 'styles', name), 'utf8')));
const scripts = await Promise.all(scriptFiles.map(name => readFile(resolve(sourceRoot, 'scripts', name), 'utf8')));
const appIcon = await readFile(resolve(sourceRoot, 'assets/app.svg'), 'utf8');
const appPaths = appIcon.replace(/^[\s\S]*?<svg[^>]*>/u, '').replace(/<\/svg>[\s\S]*$/u, '');
const icons = (await readFile(resolve(sourceRoot, 'assets/icons.svg'), 'utf8')).replace('{{APP_ICON}}', () => appPaths);
const code = scripts.map(script => script.replace(/^import\s+.+?from\s+['"].+?['"];?\s*$/gmu, '').replace(/^export\s+(?=(?:const|class|function)\b)/gmu, '')).join('\n\n');
const replacements = { FAVICON: `data:image/svg+xml,${encodeURIComponent(appIcon)}`, ICONS: icons, STYLES: styles.join('\n'), SCRIPT: `(() => {\n'use strict';\n${code}\n})();` };
const template = await readFile(resolve(sourceRoot, 'index.html'), 'utf8');
const html = template.replace(/\{\{(FAVICON|ICONS|STYLES|SCRIPT)\}\}/gu, (_, key) => replacements[key]);
if (/\{\{[A-Z_]+\}\}/u.test(html)) throw new Error('模板仍有未打包的占位符。');
const target = resolve(output);
await mkdir(dirname(target), { recursive: true }); await writeFile(target, html, 'utf8');
process.stdout.write(JSON.stringify({ output: target, bytes: Buffer.byteLength(html), runtimeDependencies: 0 }) + '\n');
