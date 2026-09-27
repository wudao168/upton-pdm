import { expect, test } from '@playwright/test'

test('PDF drawing renders pixels without native PDF plugin', async ({ page }) => {
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>',
    '<< /Length 25 >>\nstream\n0 0 0 rg 20 20 80 80 re f\nendstream',
  ]
  let pdf = '%PDF-1.4\n'
  const offsets = [0]
  objects.forEach((object, index) => { offsets.push(pdf.length); pdf += `${index + 1} 0 obj\n${object}\nendobj\n` })
  const xref = pdf.length
  pdf += `xref\n0 5\n0000000000 65535 f \n${offsets.slice(1).map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('')}trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF`
  await page.route('**/sample.pdf', route => route.fulfill({ contentType: 'application/pdf', body: pdf }))
  await page.route('**/pdf-viewer-test', route => route.fulfill({ contentType: 'text/html', body: `
    <html><head><title>PDF preview test</title></head><body><div id="app"></div>
    <script type="module">
      import { createApp } from '/node_modules/.vite/deps/vue.js';
      import Viewer from '/src/components/PdfDrawingViewer.vue';
      createApp(Viewer, { url: '/sample.pdf' }).mount('#app');
    </script></body></html>` }))
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.goto('/pdf-viewer-test')
  await expect(page.getByText('1 / 1 页')).toBeVisible()
  await expect(page.getByRole('status')).toHaveCount(0)
  await expect(page.getByRole('alert')).toHaveCount(0)
  expect(await page.locator('canvas').evaluate(canvas => {
    const context = (canvas as HTMLCanvasElement).getContext('2d')!
    return Array.from(context.getImageData(50, 240, 1, 1).data)
  })).toEqual([0, 0, 0, 255])
  expect(errors).toEqual([])
  for (const viewport of [{ width: 1922, height: 1112 }, { width: 600, height: 800 }, { width: 1000, height: 450 }]) {
    await page.setViewportSize(viewport)
    const bounds = await page.locator('canvas').evaluate(canvas => {
      const sheet = canvas.getBoundingClientRect()
      const host = canvas.parentElement!
      const frame = host.getBoundingClientRect()
      return { fits: sheet.left >= frame.left && sheet.right <= frame.right && sheet.top >= frame.top && sheet.bottom <= frame.bottom,
        ratio: sheet.width / sheet.height, noScroll: host.scrollHeight === host.clientHeight && host.scrollWidth === host.clientWidth }
    })
    expect(bounds.fits).toBe(true)
    expect(bounds.noScroll).toBe(true)
    expect(bounds.ratio).toBeCloseTo(1, 2)
  }
})
