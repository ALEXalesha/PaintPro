const { contextBridge, ipcRenderer, webUtils } = require('electron');

contextBridge.exposeInMainWorld('electronAPI', {
  // Сохранение через нативный диалог
  saveFile: (dataUrl, defaultName, saveAs) =>
    ipcRenderer.invoke('save-file', { dataUrl, defaultName, saveAs }),

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
