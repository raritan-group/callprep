// One-time interactive sign-in. Opens the site in a visible browser; you complete the Microsoft sign-in (and MFA);
// the resulting session cookie is saved to e2e/.auth/state.json for the real tests. Re-run when the session expires.
import { test as setup, expect } from '@playwright/test'
import { AUTH_FILE } from '../playwright.config'
import fs from 'node:fs'

setup('sign in with Microsoft once and save the session', async ({ page }) => {
  setup.setTimeout(10 * 60_000)
  fs.mkdirSync('e2e/.auth', { recursive: true })
  await page.goto('/')
  // The page bounces to Microsoft on load. Wait (up to 10 minutes) for it to come back signed in.
  await expect(page.locator('header .who')).toBeVisible({ timeout: 10 * 60_000 })
  const who = await page.locator('header .who').textContent()
  console.log('signed in as:', who)
  await page.context().storageState({ path: AUTH_FILE })
})
