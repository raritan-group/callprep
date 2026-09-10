// Task card: a task_draft event renders an editable card; nothing is saved until Save; Save posts to /api/tasks.
import { test, expect } from '@playwright/test'

const ME = { login: 'it@raritangroup.com', displayName: 'IT', role: 'admin', salesrepId: null, salesrepName: null, scope: 'all accounts' }
const sse = (events: object[]) => events.map(e => `data: ${JSON.stringify(e)}\n\n`).join('')
const DRAFT = { activity_id: 'CUST_FU', type_label: 'Follow up with customer', customer_id: '10046', customer_name: 'BUIST INCORPORATED', assigned_to: 'ddickman@raritangroup.com', assigned_to_name: 'Doug Dickman', subject: 'Ask if the RWJ cooling tower job was awarded', comments: 'Quote 1283698, 42 days old', due_date: '2026-09-12' }

test.use({ storageState: { cookies: [], origins: [] } })

test.beforeEach(async ({ page }) => {
  await page.route('**/api/me', r => r.fulfill({ json: ME }))
  await page.route('**/api/health', r => r.fulfill({ json: { ok: true, model: 'mock', sales_current_to: '2026-09-10' } }))
  await page.route('**/api/today**', r => r.fulfill({ json: { scope: 'all accounts', generated_at: 'x', ms: 1, sections: [
    { key: 'tasks', title: 'Your tasks', blurb: 'b', count: 1, headline: '1 due today or overdue', rows: [{ customer_id: '10046', customer_name: 'BUIST INCORPORATED', detail: 'CUST_FU · call about towers · due 2026-09-10', dollars: '', question: 'Brief me', task_no: '289' }] },
  ] } }))
  await page.route('**/api/people', r => r.fulfill({ json: [{ login: 'ddickman@raritangroup.com', name: 'Doug Dickman', role: 'rep' }, { login: 'it@raritangroup.com', name: 'IT', role: 'admin' }] }))
  await page.route('**/api/task-types', r => r.fulfill({ json: [{ activity_id: 'CUST_FU', label: 'Follow up with customer' }, { activity_id: 'CALL', label: 'Call' }] }))
})

test('draft card appears, is editable, saves only on Save', async ({ page }) => {
  let posted: Record<string, unknown> | null = null
  await page.route('**/api/chat', r => r.fulfill({ contentType: 'text/event-stream', body: sse([
    { type: 'tool', name: 'find_customer', rows: 1, ms: 40 }, { type: 'tool', name: 'draft_task', rows: 1, ms: 5 },
    { type: 'task_draft', draft: DRAFT },
    { type: 'text', text: 'Here is the task for Doug. Check it and press Save.' },
    { type: 'done', ms: 3000, input_tokens: 1, output_tokens: 1, session_id: 'x' } ]) }))
  await page.route('**/api/tasks', async r => { if (r.request().method() === 'POST') { posted = r.request().postDataJSON(); await r.fulfill({ json: { ok: true, id: 42, message: 'Saved. It is on their list now and reaches P21 within five minutes.' } }) } else await r.fulfill({ json: [] }) })
  await page.goto('/')
  await expect(page.locator('header .who')).toBeVisible()
  await page.getByPlaceholder('Ask about a customer…').fill('assign Doug a follow-up on Buist')
  await page.getByRole('button', { name: 'Ask', exact: true }).click()
  const card = page.locator('.taskcard')
  await expect(card).toBeVisible()
  await expect(card).toContainText('BUIST INCORPORATED')
  await expect(card.locator('input[type=date]')).toHaveValue('2026-09-12')
  expect(posted).toBeNull()                                              // nothing saved yet
  await card.getByLabel('Subject').fill('Ask whether the RWJ Somerset cooling tower job was awarded')
  await card.getByLabel('Type').selectOption('CALL')
  await card.getByRole('button', { name: 'Save task' }).click()
  await expect(page.locator('.saved')).toContainText('reaches P21 within five minutes')
  await expect(card).toHaveCount(0)
  expect(posted).not.toBeNull()
  expect(posted!['customerId']).toBe('10046')
  expect(posted!['assignedTo']).toBe('ddickman@raritangroup.com')
  expect(posted!['activityId']).toBe('CALL')
  expect(posted!['subject']).toContain('RWJ Somerset')
  expect(posted!['dueDate']).toBe('2026-09-12')
})

test('Cancel discards the draft without posting', async ({ page }) => {
  let posts = 0
  await page.route('**/api/chat', r => r.fulfill({ contentType: 'text/event-stream', body: sse([{ type: 'task_draft', draft: DRAFT }, { type: 'text', text: 'ok' }, { type: 'done', ms: 1, input_tokens: 0, output_tokens: 0, session_id: 'x' }]) }))
  await page.route('**/api/tasks', async r => { if (r.request().method() === 'POST') posts++; await r.fulfill({ json: {} }) })
  await page.goto('/')
  await expect(page.locator('header .who')).toBeVisible()
  await page.getByPlaceholder('Ask about a customer…').fill('remind me')
  await page.getByRole('button', { name: 'Ask', exact: true }).click()
  await page.locator('.taskcard').getByRole('button', { name: 'Cancel' }).click()
  await expect(page.locator('.taskcard')).toHaveCount(0)
  await expect(page.locator('.saved')).toContainText('discarded')
  expect(posts).toBe(0)
})

test('Done on a task row posts the completion', async ({ page }) => {
  let completed = ''
  await page.route('**/api/tasks/*/complete', async r => { completed = r.request().url(); await r.fulfill({ json: { ok: true, id: 7 } }) })
  await page.goto('/')
  await expect(page.locator('.tile')).toHaveCount(1)
  await expect(page.locator('.today-row').first()).toContainText('call about towers')
  await page.locator('.today-row').first().getByRole('button', { name: 'Done' }).click()
  await expect(page.locator('footer .hint')).toContainText('Marked done')
  expect(completed).toContain('/api/tasks/289/complete')
})
