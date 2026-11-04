import { expect, test } from '@playwright/test'
import { join } from 'node:path'
import { tmpdir } from 'node:os'

test('数量按钮与上下键按1增减，小数和工时例外保留', async ({page}) => {
  const errors:string[]=[];page.on('pageerror',error=>errors.push(error.message))
  await page.setViewportSize({width:1366,height:900})
  await page.goto('/tests/e2e/fixtures/quantity-step.html')
  await expect(page).toHaveTitle('UPLM 数量步进验证')
  await expect(page.getByRole('heading',{name:'数量统一按1增减'})).toBeVisible()
  const shared=page.getByLabel('共用数量',{exact:true}), native=page.getByLabel('原生数量',{exact:true})
  for(const input of [shared,native]) {
    await expect(input).toHaveValue('1.25')
    await input.press('ArrowUp');await expect(input).toHaveValue('2.25')
    await input.press('ArrowDown');await expect(input).toHaveValue('1.25')
  }
  await page.locator('#shared .el-input-number__increase').click();await expect(shared).toHaveValue('2.25')
  await page.locator('#shared .el-input-number__decrease').click();await expect(shared).toHaveValue('1.25')
  const box=(await native.boundingBox())!
  await page.mouse.click(box.x+box.width-8,box.y+box.height/2-4);await expect(native).toHaveValue('2.25')
  await page.mouse.click(box.x+box.width-8,box.y+box.height/2+4);await expect(native).toHaveValue('1.25')
  expect(await native.evaluate(element=>(element as HTMLInputElement).checkValidity())).toBe(true)
  await shared.fill('1.0500');await shared.blur();await expect(shared).toHaveValue('1.05')
  await native.fill('1.0500');await native.blur();await expect(native).toHaveValue('1.05')
  await shared.fill('4.75');await shared.blur();await shared.press('ArrowUp');await expect(shared).toHaveValue('5')
  // Native arrows stop if a complete +1 would cross max; Element Plus retains its existing clamp.
  await native.fill('4.75');await native.press('ArrowUp');await expect(native).toHaveValue('4.75')
  await native.fill('5');await native.press('ArrowUp');await expect(native).toHaveValue('5')
  await native.fill('0.0001');await native.press('ArrowDown');await expect(native).toHaveValue('0.0001')
  const hours=page.getByLabel('特殊工时',{exact:true});await hours.press('ArrowUp');await expect(hours).toHaveValue('1.75')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0);expect(errors).toEqual([])
  await page.screenshot({path:join(tmpdir(),'pdm-quantity-step-20261103.png'),animations:'disabled'})
})
