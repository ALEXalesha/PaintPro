const { app, BrowserWindow, Menu, dialog, ipcMain, shell } = require('electron');
const path = require('path');
const fs = require('fs');

let mainWindow;
let pendingFileOpen = null; // Путь к файлу, который нужно открыть после старта

// Файл передан через аргументы командной строки (ассоциация файлов)
function getFileFromArgs(argv) {
  const args = argv.slice(app.isPackaged ? 1 : 2);
  for (const arg of args) {
    if (arg && !arg.startsWith('--') && fs.existsSync(arg)) {
      const ext = path.extname(arg).toLowerCase();
      if (['.png', '.jpg', '.jpeg', '.bmp', '.gif', '.webp'].includes(ext)) {
        return arg;
      }
    }
  }
  return null;
}

// Single instance lock - вторая копия передаст файл в первую
const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
} else {
  app.on('second-instance', (event, argv) => {
    const file = getFileFromArgs(argv);
    if (mainWindow) {
      if (mainWindow.isMinimized()) mainWindow.restore();
      mainWindow.focus();
      if (file) openFileInApp(file);
    }
  });
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1400,
    height: 900,
    minWidth: 900,
    minHeight: 600,
    backgroundColor: '#1a1a1f',
    icon: path.join(__dirname, 'build', 'icon.ico'),
    title: 'Paint Pro',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      webSecurity: true
    }
  });

  mainWindow.loadFile('paint-pro.html');

  // Скрываем нативное меню — у нас своё (macOS-style menu bar в renderer)
  mainWindow.setMenuBarVisibility(false);
  mainWindow.setAutoHideMenuBar(true);

  // Открытие файла после загрузки, если передан через CLI
  mainWindow.webContents.on('did-finish-load', () => {
    if (pendingFileOpen) {
      openFileInApp(pendingFileOpen);
      pendingFileOpen = null;
    }
  });
}

function openFileInApp(filePath) {
  if (!mainWindow) return;
  try {
    const data = fs.readFileSync(filePath);
    const ext = path.extname(filePath).toLowerCase().slice(1);
    const mimeMap = {
      png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg',
      bmp: 'image/bmp', gif: 'image/gif', webp: 'image/webp'
    };
    const mime = mimeMap[ext] || 'image/png';
    const dataUrl = `data:${mime};base64,${data.toString('base64')}`;
    // Открытый файл становится "текущим" - Ctrl+S сохранит обратно в него,
    // а не в ранее открытый/сохранённый.
    global.lastSavedPath = filePath;
    mainWindow.webContents.send('open-file', { dataUrl, fileName: path.basename(filePath), filePath });
  } catch (err) {
    dialog.showErrorBox('Ошибка открытия файла', err.message);
  }
}

// buildMenu() удалён — нативное меню отключено через Menu.setApplicationMenu(null) ниже


// ============ IPC handlers ============

// Форматы, которые canvas.toDataURL умеет кодировать. Всё остальное (.bmp, .gif)
// молча превратилось бы в PNG-байты под чужим расширением, поэтому имя меняем на .png.
const WRITABLE_EXT = ['.png', '.jpg', '.jpeg', '.webp'];

function normalizeTarget(filePath) {
  const ext = path.extname(filePath).toLowerCase();
  if (WRITABLE_EXT.includes(ext)) return { filePath, changed: false };
  const base = filePath.slice(0, filePath.length - ext.length);
  return { filePath: base + '.png', changed: true };
}

// Шаг 1: выяснить, куда сохраняем. Кодировать renderer должен уже под известное
// расширение, иначе «Сохранить как JPEG» пишет PNG-байты в файл с именем .jpg.
ipcMain.handle('pick-save-path', async (event, { defaultName, saveAs }) => {
  if (!saveAs && global.lastSavedPath) return normalizeTarget(global.lastSavedPath);

  const result = await dialog.showSaveDialog(mainWindow, {
    defaultPath: defaultName || 'paint-pro.png',
    filters: [
      { name: 'PNG', extensions: ['png'] },
      { name: 'JPEG', extensions: ['jpg', 'jpeg'] },
      { name: 'WebP', extensions: ['webp'] }
    ]
  });
  if (result.canceled || !result.filePath) return { canceled: true };
  return normalizeTarget(result.filePath);
});

// Шаг 2: записать уже закодированные байты.
ipcMain.handle('write-image', async (event, { filePath, dataUrl }) => {
  try {
    const match = dataUrl.match(/^data:(.+?);base64,(.+)$/);
    if (!match) return { success: false, error: 'Неверный формат данных' };
    fs.writeFileSync(filePath, Buffer.from(match[2], 'base64'));
    global.lastSavedPath = filePath;
    return { success: true, filePath };
  } catch (err) {
    return { success: false, error: err.message };
  }
});

// Открытие файла через системный диалог
ipcMain.handle('open-file-dialog', async () => {
  const result = await dialog.showOpenDialog(mainWindow, {
    properties: ['openFile'],
    filters: [
      { name: 'Изображения', extensions: ['png', 'jpg', 'jpeg', 'bmp', 'gif', 'webp'] },
      { name: 'Все файлы', extensions: ['*'] }
    ]
  });
  if (result.canceled || !result.filePaths.length) return null;
  const filePath = result.filePaths[0];
  const data = fs.readFileSync(filePath);
  const ext = path.extname(filePath).toLowerCase().slice(1);
  const mimeMap = { png:'image/png', jpg:'image/jpeg', jpeg:'image/jpeg', bmp:'image/bmp', gif:'image/gif', webp:'image/webp' };
  const mime = mimeMap[ext] || 'image/png';
  // Открытый файл становится "текущим" для последующего Ctrl+S.
  global.lastSavedPath = filePath;
  return {
    dataUrl: `data:${mime};base64,${data.toString('base64')}`,
    fileName: path.basename(filePath),
    filePath
  };
});

// Чтение файла по пути (для drag & drop)
ipcMain.handle('read-dropped-file', async (event, filePath) => {
  try {
    const data = fs.readFileSync(filePath);
    const ext = path.extname(filePath).toLowerCase().slice(1);
    const mimeMap = { png:'image/png', jpg:'image/jpeg', jpeg:'image/jpeg', bmp:'image/bmp', gif:'image/gif', webp:'image/webp' };
    const mime = mimeMap[ext] || 'image/png';
    global.lastSavedPath = filePath;
    return {
      dataUrl: `data:${mime};base64,${data.toString('base64')}`,
      fileName: path.basename(filePath),
      filePath
    };
  } catch (err) {
    return { error: err.message };
  }
});

// ============ App lifecycle ============

app.whenReady().then(() => {
  // Полностью отключаем нативное меню приложения — у нас своё в renderer
  Menu.setApplicationMenu(null);
  pendingFileOpen = getFileFromArgs(process.argv);
  createWindow();

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});

// macOS - открытие файла через Finder
app.on('open-file', (event, filePath) => {
  event.preventDefault();
  if (mainWindow) openFileInApp(filePath);
  else pendingFileOpen = filePath;
});
