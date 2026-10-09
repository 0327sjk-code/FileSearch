// 文件用途：集中管理贴边尺寸、拖动阈值和反馈参数；没有收起延时或后台轮询。
export const DEMO_CONFIG = Object.freeze({
  toastDurationMs: 2200,
  dock: Object.freeze({ peekSize: 22, thumbLength: 64, dragWidth: 40, dragThreshold: 4, panelWidth: 416, margin: 8, defaultEdge: 'right', defaultPosition: .26, storageKey: 'FileSearch.EdgeDock.v2' })
});
export const EDGE_LABELS = Object.freeze({ left: '左侧', right: '右侧', top: '上侧', bottom: '下侧' });
export const CATEGORY_LABELS = Object.freeze({ all: '文件', image: '图片', video: '视频' });
export const IMAGE_EXTENSIONS = new Set(['png', 'jpg', 'jpeg', 'webp', 'gif', 'bmp', 'tif', 'tiff', 'svg']);
export const VIDEO_EXTENSIONS = new Set(['mp4', 'mov', 'mkv', 'avi', 'webm', 'm4v']);
