// Demo rehearsal in a real browser: the exact clicks a rep makes, for several customers, with the model in the loop.
// Every question costs Anthropic credits (about $0.10-0.20 each on Opus 5). Needs e2e/.auth/state.json (see auth.setup.ts).
import { test, expect, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'

const CUSTOMERS = (process.env.CALLPREP_E2E_CUSTOMERS ?? 'Buist;Northeast Remsco;Hungerford & Terry')
  .split(';').map(s => s.trim()).filter(Boolean)

async function ask(page: Page, question: string) {
  const before = await page.locator('.msg.assistant').count()
  await page.getByPlaceholder('Ask about a customer…').fill(question)
  await page.getByRole('button', { name: 'Ask', exact: true }).click()
  const answer = page.locator('.msg.assistant').nth(before)
  // done = the elapsed-time stamp appears under the bubble
  await expect(answer.locator('.meta')).toBeVisible({ timeout: 150_000 })
  const text = (await answer.locator('.bubble').textContent()) ?? ''
  const tools = await answer.locator('.tools .tag').allTextContents()
  const secs = (await answer.locator('.meta').textContent()) ?? ''
  return { text, tools, secs }
}

test.describe('Call Prep demo rehearsal', () => {
  test('page loads signed in, shows scope, health and suggestion chips', async ({ page }) => {
    await page.goto('/')
    await expect(page.locator('header .brand')).toHaveText('Call Prep')
    await expect(page.locator('header .who')).toBeVisible()
    await expect(page.locator('header .health')).toContainText('sales current to')
    await expect(page.locator('.chips .chip').first()).toBeVisible()
    await expect(page.getByRole('button', { name: /recording/i })).toBeVisible()   // mic button exists (https or localhost)
    await expect(page.locator('main')).not.toContainText('not set up for Call Prep')
    await expect(page.locator('main')).not.toContainText("Sign-in didn't complete")
  })

  test('transcription endpoint works from the browser session with the sample recording', async ({ page }) => {
    const wav = path.resolve(__dirname, '..', '..', 'models', 'test_buist.wav')
    test.skip(!fs.existsSync(wav), 'models/test_buist.wav missing')
    await page.goto('/')
    await expect(page.locator('header .who')).toBeVisible()
    const r = await page.request.post('/api/transcribe', { headers: { 'Content-Type': 'audio/wav' }, data: fs.readFileSync(wav) })
    expect(r.ok()).toBeTruthy()
    const j = await r.json()
    console.log('transcript:', j.text, `${j.ms} ms`)
    expect(j.text.toLowerCase()).toContain('buist')
  })

  for (const customer of CUSTOMERS) {
    test(`gap question + follow-up for ${customer}`, async ({ page }) => {
      await page.goto('/')
      await expect(page.locator('header .who')).toBeVisible()
      await page.getByRole('button', { name: 'New conversation' }).click()

      const a1 = await ask(page, `What is ${customer} not buying that similar customers are?`)
      console.log(`\n── ${customer} (${a1.secs}) tools: ${a1.tools.join(' → ')}\n${a1.text}\n`)
      expect(a1.tools).toContain('find_customer')
      expect(a1.text.length).toBeGreaterThan(80)
      expect(a1.text).not.toContain('⚠')
      expect(a1.text).not.toMatch(/HTTP \d{3}/)

      const a2 = await ask(page, 'Any open quotes I should follow up on before the call?')
      console.log(`── follow-up (${a2.secs}) tools: ${a2.tools.join(' → ')}\n${a2.text}\n`)
      expect(a2.text.length).toBeGreaterThan(40)
      expect(a2.text).not.toContain('⚠')
    })
  }

  test('Stop cancels a question with a typo and hands the text back', async ({ page }) => {
    await page.goto('/')
    await expect(page.locator('header .who')).toBeVisible()
    await page.getByRole('button', { name: 'New conversation' }).click()
    await page.getByPlaceholder('Ask about a customer…').fill('Snapshot of Buiat before my call')
    await page.getByRole('button', { name: 'Ask', exact: true }).click()
    const stop = page.getByRole('button', { name: 'Stop' })
    await expect(stop).toBeVisible()
    await stop.click()
    await expect(page.getByRole('button', { name: 'Ask', exact: true })).toBeVisible()
    await expect(page.locator('.msg')).toHaveCount(0)
    await expect(page.getByPlaceholder('Ask about a customer…')).toHaveValue('Snapshot of Buiat before my call')
  })

  test('New conversation clears the thread', async ({ page }) => {
    await page.goto('/')
    await expect(page.locator('header .who')).toBeVisible()
    await page.getByRole('button', { name: 'New conversation' }).click()
    await expect(page.locator('.msg')).toHaveCount(0)
    await expect(page.locator('.chips .chip').first()).toBeVisible()
  })
})
