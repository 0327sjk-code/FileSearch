// 文件用途：离线演示数据；不扫描用户电脑，不访问网络或移动硬盘。
import { IMAGE_EXTENSIONS, VIDEO_EXTENSIONS } from './config.js';

/** @typedef {{id: string, name: string, folder: string, path: string, size: string, category: 'file'|'image'|'video'}} DemoFile */

/** @param {string} name @param {string} folder @param {string} size @returns {Readonly<DemoFile>} */
function demoFile(name, folder, size) {
  const extension = name.split('.').at(-1)?.toLowerCase() ?? '';
  const category = IMAGE_EXTENSIONS.has(extension) ? 'image' : VIDEO_EXTENSIONS.has(extension) ? 'video' : 'file';
  const path = `${folder}\\${name}`;
  return Object.freeze({ id: path, name, folder, path, size, category });
}

const PROJECT = 'F:\\项目\\冰河鱼叉队';
const MATERIALS = `${PROJECT}\\Content\\水面`;
const FISH = `${PROJECT}\\Content\\鱼`;
const PICTURES = 'D:\\图片\\截图';
const VIDEOS = 'D:\\视频\\录制';
const DOCUMENTS = 'D:\\文档\\项目资料';

/** @type {ReadonlyArray<Readonly<DemoFile>>} */
export const SAMPLE_FILES = Object.freeze([
  demoFile('水面材质.uasset', MATERIALS, '148 KB'),
  demoFile('水面材质参数.png', MATERIALS, '2.4 MB'),
  demoFile('水面效果测试.mp4', VIDEOS, '86.2 MB'),
  demoFile('水面法线贴图.png', MATERIALS, '4.8 MB'),
  demoFile('鱼_BP.uasset', FISH, '216 KB'),
  demoFile('鱼游动动画.fbx', FISH, '8.6 MB'),
  demoFile('冰河鱼叉队.uproject', PROJECT, '4 KB'),
  demoFile('鱼转向测试.mp4', VIDEOS, '42.8 MB'),
  demoFile('桌面截图_2026-10-08.png', PICTURES, '1.8 MB'),
  demoFile('材质节点截图.png', PICTURES, '960 KB'),
  demoFile('界面截图.png', PICTURES, '1.2 MB'),
  demoFile('项目需求.docx', DOCUMENTS, '36 KB'),
  demoFile('文件搜索设计.pdf', DOCUMENTS, '1.6 MB'),
  demoFile('场景录制_01.mp4', VIDEOS, '128 MB'),
  demoFile('水面反射参考.jpg', MATERIALS, '3.1 MB'),
  demoFile('水面优化记录.txt', MATERIALS, '8 KB'),
  demoFile('水面测试场景.umap', MATERIALS, '12.4 MB'),
  demoFile('水面测试_低角度.mp4', VIDEOS, '64.3 MB'),
  demoFile('鱼模型预览.webp', FISH, '820 KB'),
  demoFile('Water_Surface_Reference.PNG', MATERIALS, '2.1 MB'),
  demoFile('需要保留完整文件名的水面与鱼群联合效果测试截图_最终版.png', PICTURES, '3.8 MB')
]);
