// Browser end-to-end against a running Call Prep (local :5070 or the live site). The Microsoft sign-in cannot be scripted
// (MFA), so it is done ONCE by a person in a Playwright-launched browser and saved as e2e/.auth/state.json (gitignored);
// every later run reuses that session cookie (10 h sliding, same as the app).
//
//   npx playwright test --project=setup            one-time: opens a browser, you sign in, the session is saved
//   npx playwright test                            demo rehearsal in headless Chromium against CALLPREP_URL
//   npx playwright test --headed                   watch it
//   CALLPREP_URL=http://localhost:5070 npx playwright test
//   CALLPREP_E2E_CUSTOMERS="Buist;Coppola Services" npx playwright test
import { defineConfig, devices } from '@playwright/test'

export const BASE_URL = process.env.CALLPREP_URL ?? 'https://callprep.raritangroup.com'
export const AUTH_FILE = 'e2e/.auth/state.json'

export default defineConfig({
  testDir: 'e2e',
  timeout: 180_000,
  expect: { timeout: 20_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: BASE_URL,
    trace: 'retain-on-failure',
    video: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    // UI logic with a mocked API: no sign-in, no tunnel, no credits.   npx playwright test --project=ui
    { name: 'ui', testMatch: /ui-(mock|voice)\.spec\.ts/, use: { ...devices['Desktop Chrome'], baseURL: 'http://localhost:4173' } },
    { name: 'setup', testMatch: /auth\.setup\.ts/, use: { ...devices['Desktop Chrome'], headless: false } },
    { name: 'chromium', testMatch: /demo\.spec\.ts/, use: { ...devices['Desktop Chrome'], storageState: AUTH_FILE } },
  ],
  webServer: {
    command: 'npx vite preview --port 4173 --strictPort',
    url: 'http://localhost:4173',
    reuseExistingServer: true,
    timeout: 30_000,
  },
})
