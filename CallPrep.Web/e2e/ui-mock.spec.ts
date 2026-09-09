// UI logic without a sign-in: the built page served by `vite preview`, every /api call answered by a mock.
// Runs anywhere, no tunnel, no Microsoft, no credits. Covers the Stop button and the streaming render.
import { test, expect } from '@playwright/test'

const ME = { login: 'it@raritangroup.com', displayName: 'IT', role: 'admin', salesrepId: null, salesrepName: null, scope: 'all accounts' }
const HEALTH = { ok: true, model: 'mock', sales_current_to: '2026-09-09' }
const sse = (events: object[]) => events.map(e => `data: ${JSON.stringify(e)}\n\n`).join('')

test.use({ storageState: { cookies: [], origins: [] } })

const TODAY = { scope: 'all accounts', generated_at: '2026-09-09 12:00', ms: 900, sections: [
  { key: 'stale_quotes', title: 'Quotes to chase', blurb: 'b', count: 12, headline: '$1.2M quoted', rows: [
    { customer_id: '10046', customer_name: 'BUIST INCORPORATED', detail: 'Quote #1283698 · $114,614 · 42 days old', dollars: '$114,614', question: 'Quote 1283698 with BUIST INCORPORATED is 42 days old. What should I ask on the call?' },
    { customer_id: '2', customer_name: 'B', detail: 'd', dollars: '$2', question: 'q2' }, { customer_id: '3', customer_name: 'C', detail: 'd', dollars: '$3', question: 'q3' },
    { customer_id: '4', customer_name: 'D', detail: 'd', dollars: '$4', question: 'q4' }, { customer_id: '5', customer_name: 'E', detail: 'd', dollars: '$5', question: 'q5' } ] },
  { key: 'going_quiet', title: 'Gone quiet', blurb: 'b', count: 0, headline: '$0 a year at risk', rows: [] },
  { key: 'new_accounts', title: 'New accounts', blurb: 'b', count: 1, headline: '$900 so far', rows: [{ customer_id: '9', customer_name: 'NEWCO', detail: 'First invoice 2026-09-01', dollars: '$900', question: 'NEWCO is a new account. What have they bought so far?' }] },
] }

test.beforeEach(async ({ page }) => {
  await page.route('**/api/today**', r => r.fulfill({ json: TODAY }))
  await page.route('**/api/me', r => r.fulfill({ json: ME }))
  await page.route('**/api/health', r => r.fulfill({ json: HEALTH }))
  await page.route('**/api/reset', r => r.fulfill({ json: {} }))
})

test('Stop while the answer is pending removes the question and restores the text', async ({ page }) => {
  let aborted = false
  await page.route('**/api/chat', async route => {
    // hold the request open like a slow model call; if the browser aborts, fulfill throws and we note it
    await new Promise(r => setTimeout(r, 8000))
    try { await route.fulfill({ contentType: 'text/event-stream', body: sse([{ type: 'text', text: 'late' }, { type: 'done', ms: 1, input_tokens: 0, output_tokens: 0, session_id: 'x' }]) }) }
    catch { aborted = true }
  })
  await page.goto('/')
  await expect(page.locator('header .who')).toContainText('IT')
  await page.getByPlaceholder('Ask about a customer…').fill('Snapshot of Buiat before my call')
  await page.getByRole('button', { name: 'Ask', exact: true }).click()
  await expect(page.locator('.msg')).toHaveCount(2)
  const stop = page.getByRole('button', { name: 'Stop' })
  await expect(stop).toBeVisible()
  await stop.click()
  await expect(page.getByRole('button', { name: 'Ask', exact: true })).toBeVisible()
  await expect(page.locator('.msg')).toHaveCount(0)
  await expect(page.getByPlaceholder('Ask about a customer…')).toHaveValue('Snapshot of Buiat before my call')
  await expect(page.getByPlaceholder('Ask about a customer…')).toBeEnabled()
})

test('Esc also stops', async ({ page }) => {
  await page.route('**/api/chat', async route => { await new Promise(r => setTimeout(r, 8000)); try { await route.fulfill({ body: '' }) } catch { /* aborted */ } })
  await page.goto('/')
  await expect(page.locator('header .who')).toBeVisible()
  await page.getByPlaceholder('Ask about a customer…').fill('typo question')
  await page.getByRole('button', { name: 'Ask', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Stop' })).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.getByRole('button', { name: 'Ask', exact: true })).toBeVisible()
  await expect(page.locator('.msg')).toHaveCount(0)
  await expect(page.getByPlaceholder('Ask about a customer…')).toHaveValue('typo question')
})

test('a streamed answer renders tools, text and the time stamp', async ({ page }) => {
  await page.route('**/api/chat', r => r.fulfill({ contentType: 'text/event-stream', body: sse([
    { type: 'text', text: "I'll look them up." },
    { type: 'tool', name: 'find_customer', rows: 1, ms: 50 },
    { type: 'tool', name: 'peer_gap', rows: 8, ms: 900 },
    { type: 'text', text: '\n\nBuist is not buying copper press valves.' },
    { type: 'done', ms: 12345, input_tokens: 10, output_tokens: 5, session_id: 'x' },
  ]) }))
  await page.goto('/')
  await expect(page.locator('header .who')).toBeVisible()
  await page.locator('.chips .chip').first().click()
  const a = page.locator('.msg.assistant').first()
  await expect(a.locator('.meta')).toHaveText('12.3 s')
  await expect(a.locator('.tools .tag')).toHaveText(['find_customer', 'peer_gap'])
  await expect(a.locator('.bubble')).toContainText('copper press valves')
  await expect(page.getByRole('button', { name: 'Ask', exact: true })).toBeVisible()
})

test('an error event shows in the bubble and the page recovers', async ({ page }) => {
  await page.route('**/api/chat', r => r.fulfill({ contentType: 'text/event-stream', body: sse([{ type: 'error', text: 'model unavailable' }]) }))
  await page.goto('/')
  await expect(page.locator('header .who')).toBeVisible()
  await page.getByPlaceholder('Ask about a customer…').fill('anything')
  await page.getByRole('button', { name: 'Ask', exact: true }).click()
  await expect(page.locator('.msg.assistant .bubble')).toContainText('model unavailable')
  await expect(page.getByPlaceholder('Ask about a customer…')).toBeEnabled()
})

test('the week list opens on five-number tiles and Prep opens the chat with the row question', async ({ page }) => {
  let sent = ''
  await page.route('**/api/chat', async r => { sent = r.request().postDataJSON().message; await r.fulfill({ contentType: 'text/event-stream', body: sse([{ type: 'text', text: 'ok' }, { type: 'done', ms: 1, input_tokens: 0, output_tokens: 0, session_id: 'x' }]) }) })
  await page.goto('/')
  await expect(page.locator('.today-head')).toContainText('This week')
  const tiles = page.locator('.tile')
  await expect(tiles).toHaveCount(2)                                   // empty sections get no tile
  await expect(tiles.first()).toContainText('12')
  await expect(tiles.first()).toContainText('$1.2M quoted')
  await expect(tiles.first()).toHaveClass(/on/)
  await expect(page.locator('.today-row')).toHaveCount(5)              // first tile's rows, all of them
  await expect(page.locator('.blurb')).toContainText('Showing the top 5 of 12')
  await tiles.nth(1).click()
  await expect(page.locator('.today-row')).toHaveCount(1)
  await expect(page.locator('.today-row').first()).toContainText('NEWCO')
  await tiles.first().click()
  await expect(page.locator('.today-row').first()).toContainText('BUIST INCORPORATED')
  await expect(page.locator('.today-row').first()).toContainText('$114,614')
  await page.locator('.today-row').first().getByRole('button', { name: 'Prep' }).click()
  await expect(page.locator('.msg.user .bubble')).toContainText('Quote 1283698 with BUIST INCORPORATED')
  await expect(page.locator('.msg.assistant .meta')).toBeVisible()
  expect(sent).toContain('Quote 1283698')
  await expect(page.locator('.today')).toHaveCount(0)                 // list hides while chatting
  await page.getByRole('button', { name: 'This week' }).click()
  await expect(page.locator('.today')).toBeVisible()
  await page.getByRole('button', { name: 'New conversation' }).click()
  await expect(page.locator('.msg')).toHaveCount(0)
  await expect(page.locator('.today')).toBeVisible()
})
