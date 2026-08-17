const { contextBridge, ipcRenderer, webUtils } = require('electron');

contextBridge.exposeInMainWorld('electronAPI', {
  // Сохранение в два шага: сначала путь, потом байты. Renderer кодирует картинку
  // уже зная расширение, поэтому JPEG сохраняется как JPEG, а не как PNG под .jpg.
  pickSavePath: (defaultName, saveAs) =>
    ipcRenderer.invoke('pick-save-path', { defaultName, saveAs }),

  writeImage: (filePath, dataUrl) =>
    ipcRenderer.invoke('write-image', { filePath, dataUrl }),

  // Забыть текущий файл — следующий Ctrl+S спросит путь заново
  clearSavePath: () => ipcRenderer.invoke('clear-save-path'),

  // Открытие через нативный диалог
  openFileDialog: () => ipcRenderer.invoke('open-file-dialog'),

  // Получить путь из File-объекта (для drag&drop)
  getFilePath: (file) => {
    try { return webUtils.getPathForFile(file); } catch { return null; }
  },

  // Прочитать файл по пути
  readDroppedFile: (filePath) => ipcRenderer.invoke('read-dropped-file', filePath),

  // Слушать действия из меню
  onMenuAction: (callback) => {
    ipcRenderer.on('menu-action', (_event, action) => callback(action));
  },

  // Слушать открытие файла (через ассоциацию или CLI)
  onOpenFile: (callback) => {
    ipcRenderer.on('open-file', (_event, data) => callback(data));
  }
});
