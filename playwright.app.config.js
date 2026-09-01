// Проверки НАСТОЯЩЕГО приложения: запускается Electron, а не страница в браузере.
// Медленные (секунды на запуск), поэтому отдельной командой: npm run test:app.
const { defineConfig } = require('@playwright/test');

module.exports = defineConfig({
  testDir: './tests-app',
  fullyParallel: false,   // приложение одно, окно одно
  workers: 1,
  timeout: 60000,
  reporter: [['list']],
});
