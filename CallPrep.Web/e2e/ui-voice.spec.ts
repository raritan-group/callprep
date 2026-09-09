// Voice flow with a fake microphone (Chromium flags), mocked API, no sign-in. launchOptions must be set at file scope.
import { test, expect } from '@playwright/test'

const ME = { login: 'it@raritangroup.com', displayName: 'IT', role: 'admin', salesrepId: null, salesrepName: null, scope: 'all accounts' }
const HEALTH = { ok: true, model: 'mock', sales_current_to: '2026-09-09' }
const sse = (events: object[]) => events.map(e => `data: ${JSON.stringify(e)}

`).join('')

test.use({
  storageState: { cookies: [], origins: [] },
  launchOptions: { args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream'] },
  permissions: ['microphone'],
})

test.beforeEach(async ({ page }) => {
  await page.route('**/api/today**', r => r.fulfill({ json: { scope: 'all accounts', generated_at: 'x', ms: 1, sections: [] } }))
  await page.route('**/api/me', r => r.fulfill({ json: ME }))
  await page.route('**/api/health', r => r.fulfill({ json: HEALTH }))
  await page.route('**/api/reset', r => r.fulfill({ json: {} }))
})

test('transcript lands in the box for review and is NOT sent until Ask', async ({ page }) => {
  let chatCalls = 0
  await page.route('**/api/transcribe', r => r.fulfill({ json: { text: 'What is Buist not buying that similar contractors are?', ms: 500 } }))
  await page.route('**/api/chat', r => { chatCalls++; return r.fulfill({ contentType: 'text/event-stream', body: sse([{ type: 'text', text: 'ok' }, { type: 'done', ms: 1, input_tokens: 0, output_tokens: 0, session_id: 'x' }]) }) })
  await page.goto('/')
  await expect(page.locator('header .who')).toBeVisible()
  const mic = page.getByRole('button', { name: 'Start recording' })
  await mic.click()
  await expect(page.getByRole('button', { name: 'Stop recording' })).toBeVisible()
  await page.waitForTimeout(600)
  await page.getByRole('button', { name: 'Stop recording' }).click()
  const box = page.getByPlaceholder('Ask about a customer…')
  await expect(box).toHaveValue('What is Buist not buying that similar contractors are?')
  await expect(page.locator('footer .hint')).toContainText('Check the text')
  await expect(page.locator('.msg')).toHaveCount(0)
  expect(chatCalls).toBe(0)
  // the rep fixes a word, then sends
  await box.fill('What is Buist not buying that similar mechanical contractors are?')
  await page.getByRole('button', { name: 'Ask', exact: true }).click()
  await expect(page.locator('.msg.assistant .meta')).toBeVisible()
  expect(chatCalls).toBe(1)
})

test('empty transcript shows a retry hint', async ({ page }) => {
  await page.route('**/api/transcribe', r => r.fulfill({ json: { text: '', ms: 200 } }))
  await page.goto('/')
  await expect(page.locator('header .who')).toBeVisible()
  await page.getByRole('button', { name: 'Start recording' }).click()
  await page.waitForTimeout(400)
  await page.getByRole('button', { name: 'Stop recording' }).click()
  await expect(page.locator('footer .hint')).toContainText("Didn't catch that")
  await expect(page.locator('.msg')).toHaveCount(0)
})
