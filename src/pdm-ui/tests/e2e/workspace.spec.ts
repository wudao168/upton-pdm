import { permissionDirectory as projectPermissionDirectory } from '../projectPermissionFixture'
import { expect, test, type Page } from '@playwright/test'
import type { AssessmentGroup } from '../../src/projectBudget'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import jsQR from 'jsqr'
import { budgetCategories, calculateBudgetRow, assessmentItemTotal, assessmentSum } from '../../src/projectBudget'
import { defaultProductionFlow } from '../../src/productionFlowSettings'
import type { ProjectApplication, ProjectApplicationEvent, ProjectApplicationInput } from '../../src/projectApplications'
import type { SaveWorkHours, WorkHoursView, WorkHoursManagement } from '../../src/workHours'

test('procurement drawing attachment column opens published files read only over LAN HTTP', async ({ page }, testInfo) => {
  test.setTimeout(90_000)
  await mockPlanCompanionPanels(page)
  await page.route('**/*jelly*.html*', route => route.fulfill({ contentType: 'text/html', body: '<html><body></body></html>' }))
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [] }))
  await page.route('**/api/material-stock-plans', route => route.fulfill({ json: { enterpriseCode:'01',organizationCode:'7',items:[] } }))
  const errors: string[] = [], fileRequests: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  page.on('request', request => { if (request.url().includes('/procurement-files')) fileRequests.push(request.method()) })
  const files = [
    { id:'purchase-old',materialId:'purchase-material',kind:'Drawing2D',fileName:'采购图V01.pdf',versionNumber:1,purpose:'图纸' },
    { id:'purchase-agreement',materialId:'purchase-material',kind:'Document',fileName:'采购技术协议.pdf',versionNumber:0,purpose:'技术协议' },
  ].map(file => ({ ...file,fileLength:100,sha256:'A'.repeat(64),uploadedBy:'标准化',uploadedAt:'2026-10-30T01:00:00Z',source:'标准物料' }))
  const row = { sequence:1,projectCode:'PRJ-REAL-001',subprojectCode:null,materialCode:'PUR-001',materialName:'带图采购件',specification:'UT-01',brand:'UPTON',quantity:2,bomKind:'标准件',releasePackageNumber:'RP-PURCHASE-V01',purchaseRequisitionNumbers:[],purchaseRequisitionStatus:'未请购',purchaseOrderNumbers:[],purchaseOrderStatus:'未采购',purchaseQuantity:0,arrivedQuantity:0,requestedQuantity:0,approvedQuantity:0,details:[],files }
  await page.route(`**/api/projects/${projectId}/procurement-tracking`, route => route.fulfill({ json: { projectId,projectCode:row.projectCode,hasPublishedBom:true,items:[row],lastSuccessfulRefreshAt:'2026-10-30T01:00:00Z' } }))
  await page.route(`**/api/projects/${projectId}/boms/Standard*`, route => route.fulfill({ json: [{id:'work-item',kind:'Standard',sequence:1,drawingNumber:'PUR-001',name:'带图采购件',quantity:2,unit:'001',revision:'W2',isComplete:true,files:[{...files[0],id:'new-file',fileName:'未发布V02.pdf',versionNumber:2}]}] }))
  const stream = 'BT /F1 20 Tf 50 240 Td (Purchase Drawing - V01) Tj ET\n50 80 300 100 re S\n'
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>','<< /Type /Pages /Kids [3 0 R] /Count 1 >>','<< /Type /Page /Parent 2 0 R /MediaBox [0 0 450 320] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>',`<< /Length ${Buffer.byteLength(stream)} >>\nstream\n${stream}endstream`,'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>']
  let pdf = '%PDF-1.4\n'; const offsets: number[] = []
  objects.forEach((object,index) => { offsets.push(Buffer.byteLength(pdf)); pdf += `${index+1} 0 obj\n${object}\nendobj\n` })
  const xref = Buffer.byteLength(pdf)
  pdf += `xref\n0 6\n0000000000 65535 f \n${offsets.map(offset => `${String(offset).padStart(10,'0')} 00000 n \n`).join('')}trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF`
  await page.route(`**/api/projects/${projectId}/procurement-files**`, route => new URL(route.request().url()).pathname.endsWith('/file')
    ? route.fulfill({contentType:'application/pdf',body:Buffer.from(pdf)}) : route.fulfill({json:files}))
  await page.setViewportSize({width:1959,height:1080})
  await page.goto('/'); await expect(page).toHaveTitle(/PLM|UPLM|PDM/)
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox',{name:'账号'}).fill('engineer');await login.getByRole('textbox',{name:'密码'}).fill('correct-password');await login.getByRole('button',{name:'登录',exact:true}).click()
  await enterProject(page);await page.getByRole('button',{name:'备料',exact:true}).click()
  const panel = page.getByLabel('采购跟踪',{exact:true})
  await expect(panel.getByRole('columnheader',{name:'图纸附件',exact:true})).toHaveCount(0)
  await panel.getByRole('button',{name:'列设置',exact:true}).click()
  const settings = page.getByRole('dialog',{name:'采购跟踪列设置',exact:true})
  await settings.getByText('图纸附件',{exact:true}).click()
  await expect(settings.getByRole('checkbox',{name:'图纸附件',exact:true})).toBeChecked()
  await settings.getByRole('button',{name:'保存',exact:true}).click()
  await expect(panel.getByRole('columnheader',{name:'图纸附件',exact:true})).toBeVisible()
  const link = panel.getByRole('button',{name:'PUR-001 · 图纸附件',exact:true})
  await expect(link).toHaveText('查看（2）');await link.click()
  const dialog = page.getByRole('dialog',{name:'PUR-001 · 图纸附件',exact:true})
  await expect(dialog.getByRole('cell',{name:'采购图V01.pdf',exact:true})).toBeVisible()
  await expect(dialog.getByRole('cell',{name:'采购技术协议.pdf',exact:true})).toBeVisible()
  await expect(dialog.getByRole('cell',{name:'未发布V02.pdf',exact:true})).toHaveCount(0)
  await expect(dialog.getByLabel('上传项目附件')).toHaveCount(0)
  await expect(dialog.getByRole('button',{name:'移除',exact:true})).toHaveCount(0)
  const drawing = dialog.getByRole('row').filter({hasText:'采购图V01.pdf'})
  await drawing.getByRole('button',{name:'预览',exact:true}).click()
  await expect(dialog.locator('canvas')).toBeVisible()
  const download = page.waitForEvent('download');await drawing.getByRole('button',{name:'下载',exact:true}).click()
  expect((await download).suggestedFilename()).toBe('采购图V01.pdf')
  await page.screenshot({path:testInfo.outputPath('procurement-drawing-files.png'),fullPage:false})
  await page.setViewportSize({width:900,height:900})
  await expect(dialog.getByRole('button',{name:'关闭',exact:true})).toBeVisible()
  await page.screenshot({path:testInfo.outputPath('procurement-drawing-files-narrow.png'),fullPage:false})
  expect(fileRequests.length).toBeGreaterThan(1);expect(fileRequests.every(method=>method==='GET')).toBe(true)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('standard drawing versions and BOM files preserve published references over LAN HTTP', async ({ page }, testInfo) => {
  test.setTimeout(90_000)
  await mockPlanCompanionPanels(page)
  // 装饰性WebGPU背景另行验证，图纸流程在普通内网HTTP下执行。
  await page.route('**/*jelly*.html*', route => route.fulfill({ contentType: 'text/html', body: '<html><body></body></html>' }))
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [] }))
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text() + ' ' + message.location().url) })
  const material = { id: 'version-material', materialCode: 'STD-001', name: '内部标准轴套', kind: 'Standard', supplyMode: 'Manufacture', unitCode: '001', specification: 'UT-01', brand: '内部', drawing2DAttachmentCount: 1, model3DAttachmentCount: 1, documentAttachmentCount: 0, enabledDrawing2DVersion: 1, enabledModel3DVersion: 1 }
  let files = ['Drawing2D','Model3D'].map(kind => ({ id: `${kind}-1`, materialId: material.id, kind, originalFileName: kind === 'Drawing2D' ? '轴套2D.pdf' : '轴套3D.step', fileLength: 20, sha256: 'A'.repeat(64), versionNumber: 1, isEnabled: true, uploadedBy: '标准化', uploadedAt: '2026-10-29T10:00:00Z' }))
  const frozenFiles = files.map(file => ({ ...file, fileName: file.originalFileName, source: '标准物料', purpose: file.kind === 'Drawing2D' ? '图纸' : '模型' }))
  let projectFiles = [...frozenFiles], rowVersion = 1
  const row = { id: 'standard-bom-file', projectId, kind: 'Standard', sequence: 1, drawingNumber: material.materialCode, name: material.name, quantity: 2, unit: '001', specification: 'UT-01', brand: '内部', revision: 'W1', isComplete: true, files: frozenFiles }
  await page.route('**/api/material-categories?*', route => route.fulfill({ json: [] }))
  await page.route('**/api/standard-library/categories*', route => route.fulfill({ json: [] }))
  await page.route('**/api/standard-library/materials?*', route => route.fulfill({ json: { items: [{ material, categories: [], coverImage: null }], total: 1, page: 1, pageSize: 50 } }))
  let uploadKind = 'Drawing2D'
  await page.route('**/api/materials/version-material/**', route => {
    const url = new URL(route.request().url())
    if (url.pathname.endsWith('/attachments/uploads')) { uploadKind = route.request().postDataJSON().kind; return route.fulfill({ json: { id: 'version-upload', chunkSize: 1048576 } }) }
    if (url.pathname.endsWith('/enable')) {
      const id = url.pathname.split('/').at(-2)!
      const file = files.find(file => file.id === id)!
      files = files.map(value => value.kind === file.kind ? { ...value, isEnabled: value.id === id } : value)
      if (file.kind === 'Drawing2D') material.enabledDrawing2DVersion = file.versionNumber
      else material.enabledModel3DVersion = file.versionNumber
      return route.fulfill({ json: files.find(value => value.id === id) })
    }
    return route.fulfill({ json: files.filter(file => !url.searchParams.get('kind') || file.kind === url.searchParams.get('kind')) })
  })
  await page.route('**/api/material-attachment-uploads/version-upload/**', route => {
    if (route.request().url().endsWith('/complete')) {
      const file = { ...files.find(file => file.kind === uploadKind)!, id: `${uploadKind}-2`, versionNumber: 2, isEnabled: false, originalFileName: uploadKind === 'Drawing2D' ? '轴套2D新版.pdf' : '轴套3D新版.step' }
      files.push(file)
      if (uploadKind === 'Drawing2D') material.drawing2DAttachmentCount++
      else material.model3DAttachmentCount++
      return route.fulfill({ json: file })
    }
    return route.fulfill({ status: 200, json: {} })
  })
  await page.route(`**/api/projects/${projectId}/boms/Standard*`, route => route.fulfill({ json: [row, { ...row, id: 'missing-code-row', drawingNumber: '', name: '未填写料号样例', files: [] }] }))
  await page.route(`**/api/projects/${projectId}/bom-versions*`, route => route.fulfill({ json: [{ id: 'frozen-bom', projectId, kind: 'Standard', versionNumber: 1, label: 'S-V01', state: 'Released', items: [row] }] }))
  await page.route(`**/api/projects/${projectId}/bom-files/standard-bom-file**`, route => {
    const url = new URL(route.request().url())
    if (url.pathname.endsWith('/uploads')) return route.fulfill({ json: { id: 'project-upload', chunkSize: 1048576 } })
    if (url.pathname.endsWith('/chunks/0')) return route.fulfill({ status: 204 })
    if (url.pathname.endsWith('/complete')) { projectFiles = [...projectFiles, { ...frozenFiles[0]!, id: 'agreement', kind: 'Document', fileName: '采购技术协议.pdf', source: '项目上传', purpose: '技术协议', versionNumber: 0 }]; rowVersion++ }
    return route.fulfill({ json: { files: url.searchParams.has('bomVersionId') ? frozenFiles : projectFiles, libraryUpdateAvailable: !url.searchParams.has('bomVersionId'), rowVersion } })
  })
  await page.setViewportSize({ width: 1959, height: 1080 })
  await page.goto('/'); await expect(page).toHaveTitle(/PLM|UPLM|PDM/)
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox',{ name: '账号' }).fill('admin'); await login.getByRole('textbox',{ name: '密码' }).fill('correct-password'); await login.getByRole('button',{ name: '登录', exact: true }).click()
  await page.locator('.pdm-sidebar').getByRole('button',{ name: '标准物料', exact: true }).click()
  const materialRow = page.locator('.material-list-table .el-table__body tr').filter({hasText:'内部标准轴套'})
  await materialRow.locator('label.el-checkbox').click()
  await page.getByRole('button',{name:'维护',exact:true}).click()
  const editor = page.getByRole('dialog',{name:'料品明细',exact:true})
  await expect(editor.locator('.material-editor-file-list').nth(0)).toHaveAttribute('aria-label','2D图纸附件')
  await expect(editor.locator('.material-editor-file-list').nth(1)).toHaveAttribute('aria-label','3D附件')
  const section2d = editor.getByLabel('2D图纸附件',{exact:true})
  await editor.getByLabel('选择2D附件',{exact:true}).setInputFiles({name:'轴套2D新版.pdf',mimeType:'application/pdf',buffer:Buffer.from('test drawing')})
  await expect(section2d.getByRole('cell',{name:'V02',exact:true})).toBeVisible()
  await expect(section2d.getByRole('cell',{name:'V01 · 已启用',exact:true})).toBeVisible()
  await section2d.getByRole('button',{name:'版本管理',exact:true}).click()
  const versionDialog = page.getByRole('dialog',{name:'STD-001 · 2D图纸版本',exact:true})
  await versionDialog.getByRole('row').filter({hasText:'轴套2D新版.pdf'}).getByRole('button',{name:'启用',exact:true}).click()
  await page.getByRole('button',{name:'确认启用',exact:true}).click()
  await expect(versionDialog.locator('p strong')).toHaveText('V02')
  await versionDialog.getByRole('button',{name:'关闭',exact:true}).click()
  await expect(section2d.getByRole('cell',{name:'V02 · 已启用',exact:true})).toBeVisible()
  await expect(section2d.getByRole('cell',{name:'轴套2D.pdf',exact:true})).toBeVisible()
  await section2d.scrollIntoViewIfNeeded()
  await page.screenshot({path:testInfo.outputPath('material-detail-2d-above-3d.png'),fullPage:false})
  await editor.getByRole('button',{name:'取消',exact:true}).click()
  for (const [kind, label, name] of [['Model3D','3D模型','轴套3D新版.step']]) {
    await page.getByRole('button',{ name: `STD-001 · ${label}版本`, exact: true }).click()
    const dialog = page.getByRole('dialog',{ name: `STD-001 · ${label}版本`, exact: true })
    await dialog.getByLabel(`上传${label}新版本`).setInputFiles({ name: name!, mimeType: 'application/octet-stream', buffer: Buffer.from('test drawing') })
    await expect(dialog.getByRole('cell',{ name: 'V02', exact: true })).toBeVisible()
    await expect(dialog.locator('p strong')).toHaveText('V01')
    expect(files.find(file => file.kind === kind && file.isEnabled)?.versionNumber).toBe(1)
    await dialog.getByRole('row').filter({ hasText: name }).getByRole('button',{ name: '启用', exact: true }).click()
    await page.getByRole('button',{ name: '确认启用', exact: true }).click()
    await expect(dialog.locator('p strong')).toHaveText('V02')
    await expect(dialog.getByRole('button',{ name: '删除', exact: true })).toHaveCount(0)
    await page.screenshot({ path: testInfo.outputPath(`${kind}-versions.png`), fullPage: false })
    await dialog.getByRole('button',{ name: '关闭', exact: true }).click()
  }
  await page.locator('.pdm-sidebar').getByRole('button',{ name: '项目中心', exact: true }).click()
  await enterProject(page); await page.getByRole('button',{ name: 'BOM', exact: true }).click(); await page.getByRole('tab',{ name: /^标准件BOM/ }).click()
  const bom = page.locator('.pdm-bom-table')
  await expect(bom.getByRole('columnheader',{ name: '图纸附件', exact: true })).toHaveCount(1)
  for (const label of ['2D图纸','3D模型','附件']) await expect(bom.getByRole('columnheader',{ name: label, exact: true })).toHaveCount(0)
  const info = bom.getByRole('button',{ name: '查看问题详情：未填写料号', exact: true })
  await expect(info).toHaveCSS('border-top-width', '0px')
  await expect(info).toHaveCSS('box-shadow', 'none')
  await info.hover()
  await expect(info).toHaveCSS('outline-style', 'none')
  await info.click()
  await expect(page.getByRole('button',{ name: '关闭问题详情', exact: true })).toBeVisible()
  await page.getByRole('button',{ name: '关闭问题详情', exact: true }).click()
  const filesLink = page.getByRole('button',{ name: 'STD-001 · 图纸附件', exact: true })
  await expect(filesLink).toHaveText('查看（2）')
  await expect(filesLink).toHaveCSS('border-top-width', '0px')
  await filesLink.click()
  let dialog = page.getByRole('dialog',{ name: 'STD-001 · 图纸附件', exact: true })
  await expect(dialog.getByRole('cell',{ name: '2D图纸', exact: true })).toBeVisible()
  await expect(dialog.getByRole('cell',{ name: '3D模型', exact: true })).toBeVisible()
  await dialog.getByLabel('上传项目附件').setInputFiles({ name: '采购技术协议.pdf', mimeType: 'application/pdf', buffer: Buffer.from('agreement') })
  await expect(dialog.getByRole('cell',{ name: '采购技术协议.pdf', exact: true })).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('bom-files-list.png'), fullPage: false })
  await dialog.getByRole('button',{ name: '关闭', exact: true }).click()
  await expect(filesLink).toHaveText('查看（3）')
  await page.screenshot({ path: testInfo.outputPath('bom-current-files.png'), fullPage: false })
  await page.getByRole('button',{ name: '选择发布包', exact: true }).click()
  await page.getByText('单版查看', { exact: true }).click()
  await page.getByLabel('选择BOM版本').selectOption('frozen-bom')
  await page.getByRole('button',{ name: 'STD-001 · 图纸附件', exact: true }).click()
  dialog = page.getByRole('dialog',{ name: 'STD-001 · 图纸附件', exact: true })
  await expect(dialog.getByRole('cell',{ name: '轴套2D.pdf', exact: true })).toBeVisible()
  await expect(dialog.getByLabel('上传项目附件')).toHaveCount(0)
  await expect(dialog.getByRole('cell',{ name: '采购技术协议.pdf', exact: true })).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('bom-frozen-files.png'), fullPage: false })
  await page.setViewportSize({ width: 900, height: 900 })
  await expect(dialog.getByRole('button',{ name: '关闭', exact: true })).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('bom-frozen-files-narrow.png'), fullPage: false })
  expect(errors).toEqual([])
})

test('engineering kit library metadata uses material editor with read-only identity over LAN HTTP', async ({ page }, testInfo) => {
  await mockPlanCompanionPanels(page)
  await page.setViewportSize({ width: 1959, height: 1080 })
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.route('**/client-bootstrap.json*', route => route.fulfill({ json: { ConfigurationVersion:'2026.10.23.kit-metadata-qa', Desktop:{Version:'QA'}, SolidWorksAddin:{Version:'QA'} } }))
  await page.route('**/api/material-stock-plans', route => route.fulfill({json:[]}))
  await page.route('**/api/auth/heartbeat', route => route.fulfill({json:{}}))
  let kit = { id:'kit-metadata-qa',code:'UKIT-000003',name:'套件资料验证样本',model:'UTJ-10-0001',brand:'UPTON',description:'原备注',selectionAdvice:'',referencePrice:null as number|null,isRecommended:false,coverImageAttachmentId:null as string|null,
    currentReleasedRevisionId:'kit-released',revisions:[{id:'kit-released',kitId:'kit-metadata-qa',versionNumber:1,state:'Released',components:[]}],rowVersion:2,createdBy:'admin',createdAt:'2026-10-23T01:00:00Z',updatedBy:'admin',updatedAt:'2026-10-23T01:00:00Z' }
  const attachments: Array<{id:string;materialId:string;kind:string;originalFileName:string}> = []
  const requests: Array<Record<string,unknown>> = []
  const uploads = new Map<string,{kind:string;fileName:string}>()
  const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jR0kAAAAASUVORK5CYII=','base64')
  await page.route(/\/api\/standard-library\/categories(?:\?.*)?$/, route => route.fulfill({json:[]}))
  await page.route('**/api/standard-library/materials?*', route => route.fulfill({json:{items:[{kit,categories:[],coverImage:attachments.find(item=>item.id===kit.coverImageAttachmentId)??null,
    material:{id:kit.id,materialCode:kit.code,name:kit.name,specification:kit.model,brand:kit.brand,remark:kit.description,selectionAdvice:kit.selectionAdvice,isRecommended:kit.isRecommended,coverImageAttachmentId:kit.coverImageAttachmentId,referencePrice:kit.referencePrice,
      rowVersion:kit.rowVersion,kind:'Standard',supplyMode:'Purchase',unitCode:'001',approvalStatus:'Approved',syncStatus:'Succeeded',u9SyncConfirmed:true,model3DAttachmentCount:attachments.filter(a=>a.kind==='Model3D').length,documentAttachmentCount:attachments.filter(a=>a.kind==='Document').length}}],total:1,page:1,pageSize:50}}))
  await page.route('**/api/materials/kit-metadata-qa/attachments', route => route.fulfill({json:attachments}))
  await page.route('**/api/materials/kit-metadata-qa/attachments/*/file', route => route.fulfill({contentType:'image/png',body:png}))
  await page.route('**/api/materials/kit-metadata-qa/attachments/uploads', route => {
    const input=route.request().postDataJSON();const id='kit-upload-'+uploads.size;uploads.set(id,input);return route.fulfill({json:{id,chunkSize:1024}})
  })
  await page.route('**/api/material-attachment-uploads/*/chunks/*', route => route.fulfill({status:204}))
  await page.route('**/api/material-attachment-uploads/*/complete', route => {
    const id=new URL(route.request().url()).pathname.split('/')[3]!,file=uploads.get(id)!
    const attachment={id:'attachment-'+id,materialId:kit.id,kind:file.kind,originalFileName:file.fileName};attachments.push(attachment);return route.fulfill({json:attachment})
  })
  await page.route('**/api/engineering-kits/kit-metadata-qa/library-metadata', route => {
    const input=route.request().postDataJSON();requests.push(input)
    expect(input.expectedRowVersion).toBe(kit.rowVersion);expect(input).not.toHaveProperty('name');expect(input).not.toHaveProperty('components')
    kit={...kit,description:input.remark,selectionAdvice:input.selectionAdvice??'',referencePrice:input.referencePrice??null,isRecommended:input.isRecommended,coverImageAttachmentId:input.coverImageAttachmentId,rowVersion:kit.rowVersion+1}
    return route.fulfill({json:kit})
  })
  await page.goto('/')
  await expect(page).toHaveTitle('UPTON-PLM');await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  const login=page.getByLabel('登录PLM');await login.getByRole('textbox',{name:'账号'}).fill('admin');await login.getByRole('textbox',{name:'密码'}).fill('correct-password');await login.getByRole('button',{name:'登录',exact:true}).click()
  await page.locator('.pdm-sidebar').getByRole('button',{name:'标准物料',exact:true}).click()
  const open = async () => {const row=page.locator('.material-list-table .el-table__body tr').filter({hasText:'套件资料验证样本'});await expect(row.getByRole('button',{name:'套件资料验证样本',exact:true})).toHaveCount(0);await row.getByText('套件资料验证样本',{exact:true}).click();await expect(page.getByRole('dialog',{name:'套件资料维护'})).not.toBeVisible();if(!await row.getByRole('checkbox').isChecked())await row.locator('label.el-checkbox').click();await page.getByRole('button',{name:'维护',exact:true}).click();return page.getByRole('dialog',{name:'套件资料维护'})}
  let dialog=await open()
  for(const label of ['料号','名称','型号','品牌']) await expect(dialog.getByLabel(label,{exact:true})).toBeDisabled()
  await expect(dialog.getByLabel('料号',{exact:true})).toHaveValue('UKIT-000003');await expect(dialog.getByLabel('型号',{exact:true})).toHaveValue('UTJ-10-0001')
  await dialog.getByLabel('备注',{exact:true}).fill('适用于真空泵组合')
  await dialog.getByLabel('选型建议',{exact:true}).fill('优先用于真空系统，安装前核对接口')
  await dialog.getByLabel('选择套件图片').setInputFiles([{name:'封面.png',mimeType:'image/png',buffer:png},{name:'细节.png',mimeType:'image/png',buffer:png}])
  await expect(dialog.locator('.material-image-card')).toHaveCount(2)
  expect(requests[0]?.remark).toBe('原备注');await expect(dialog.getByLabel('备注',{exact:true})).toHaveValue('适用于真空泵组合')
  await dialog.getByRole('button',{name:'将 细节.png 设为封面',exact:true}).click()
  await expect.poll(()=>kit.coverImageAttachmentId).toBe(attachments[1]!.id)
  await dialog.getByRole('button',{name:'清除当前封面'}).click();await expect.poll(()=>kit.coverImageAttachmentId).toBe(null)
  await expect(dialog.locator('.material-image-card')).toHaveCount(2)
  await dialog.getByRole('button',{name:'将 封面.png 设为封面',exact:true}).click();await expect.poll(()=>kit.coverImageAttachmentId).toBe(attachments[0]!.id)
  await dialog.getByLabel('上传套件3D附件').setInputFiles({name:'组合泵.step',mimeType:'application/octet-stream',buffer:Buffer.from('step fixture')})
  await expect(dialog.getByRole('cell',{name:'组合泵.step',exact:true})).toBeVisible()
  await dialog.getByLabel('上传套件资料附件').setInputFiles({name:'选型资料.pdf',mimeType:'application/pdf',buffer:Buffer.from('pdf fixture')})
  await expect(dialog.getByRole('cell',{name:'选型资料.pdf',exact:true})).toBeVisible()
  await page.screenshot({path:testInfo.outputPath('kit-material-editor-desktop.png'),fullPage:false})
  await page.setViewportSize({width:780,height:900});await expect(dialog.getByRole('button',{name:'保存修改'})).toBeVisible()
  expect(await page.locator('body').evaluate(element=>element.scrollWidth<=innerWidth)).toBe(true)
  await page.screenshot({path:testInfo.outputPath('kit-material-editor-narrow.png'),fullPage:false})
  await dialog.getByRole('button',{name:'保存修改'}).click();await expect(dialog).not.toBeVisible()
  expect(kit.description).toBe('适用于真空泵组合');expect(kit.selectionAdvice).toBe('优先用于真空系统，安装前核对接口');expect(kit.currentReleasedRevisionId).toBe('kit-released')
  dialog=await open();await expect(dialog.getByLabel('备注',{exact:true})).toHaveValue(kit.description)
  await expect(dialog.getByLabel('选型建议',{exact:true})).toHaveValue(kit.selectionAdvice);await expect(dialog.locator('.material-image-card')).toHaveCount(2)
  expect(errors).toEqual([])
})

test('folder template save refreshes existing project permissions on LAN HTTP', async ({ page }) => {
  test.setTimeout(120_000)
  await mockPlanCompanionPanels(page)
  await page.route('**/api/material-stock-plans', route => route.fulfill({ json: { enterpriseCode: '01', organizationCode: '7', items: [] } }))
  await page.setViewportSize({ width: 1959, height: 1080 })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  let saved = false
  let reads = 0
  let created = false
  const folders = () => [
    { id: 'acl-root', rootProjectId: projectId, folderKey: 'root', templateKey: 'root', name: 'PRJ-REAL-001', purpose: 0, sortOrder: 0, isSystem: true, inheritPermissions: true, effectiveAccess: 127, permissions: [] },
    { id: 'acl-inherited', rootProjectId: projectId, parentFolderId: 'acl-root', folderKey: 'project-files', templateKey: 'project-files', name: '项目资料', purpose: 5, sortOrder: 10, isSystem: true, inheritPermissions: !saved, effectiveAccess: saved ? 3 : 15, permissions: [] },
    { id: 'acl-independent', rootProjectId: projectId, parentFolderId: 'acl-root', folderKey: 'purchase', templateKey: 'purchase', name: '独立权限目录', purpose: 5, sortOrder: 20, isSystem: true, inheritPermissions: true, effectiveAccess: 15, permissions: [{ principalType: 0, principalKey: 'Engineer', access: 15 }] },
    ...(created ? [{ id: 'acl-created', rootProjectId: projectId, parentFolderId: 'acl-inherited', folderKey: 'custom:created', templateKey: 'custom', name: '新建资料子目录', purpose: 5, sortOrder: 10, isSystem: false, inheritPermissions: true, effectiveAccess: saved ? 3 : 15, permissions: [] }] : []),
  ]
  await page.route(`**/api/projects/${projectId}/folders`, route => { reads++; return route.fulfill({ json: folders() }) })
  await page.route(`**/api/projects/${projectId}/folders/acl-inherited/children`, async route => {
    expect(route.request().postDataJSON().name).toBe('新建资料子目录')
    created = true; await route.fulfill({ json: folders().find(folder => folder.id === 'acl-created') })
  })
  await page.route('**/api/folder-template', async route => {
    if (route.request().method() === 'PUT') { saved = true; return route.fulfill({ json: route.request().postDataJSON().nodes }) }
    return route.fulfill({ json: [{ folderKey: 'project-files', name: '项目资料', purpose: 5, sortOrder: 10, isSystem: true, inheritPermissions: true, permissions: [] }] })
  })
  await page.route(`**/api/projects/${projectId}/files?*`, route => route.fulfill({ json: [] }))
  await page.goto('/'); await expect(page).toHaveTitle('UPTON-PLM')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page); await page.getByRole('button', { name: '文件', exact: true }).click()
  await page.locator('.pdm-folder-pane').getByText('项目资料', { exact: true }).click()
  await page.getByRole('button', { name: '新建文件夹', exact: true }).click()
  const prompt = page.getByRole('dialog', { name: '新建文件夹', exact: true })
  await prompt.getByRole('textbox').fill('新建资料子目录')
  await prompt.getByRole('button', { name: '确定', exact: true }).click()
  await expect.poll(() => created).toBe(true)
  await expect(page.locator('.pdm-global-status')).toContainText('文件夹已创建')
  await page.locator('.pdm-folder-pane .el-tree-node__content').filter({ hasText: '项目资料' }).locator('.el-tree-node__expand-icon').click()
  await expect(page.locator('.pdm-folder-pane')).toContainText('新建资料子目录')
  await page.getByRole('button', { name: '系统设置', exact: true }).click()
  await page.getByRole('button', { name: '文件夹模板', exact: true }).click()
  await page.locator('.pdm-template-settings tbody tr').first().locator('.el-switch').click()
  const beforeSave = reads
  await page.getByRole('button', { name: '保存模板', exact: true }).click()
  await expect.poll(() => reads).toBeGreaterThan(beforeSave)
  await expect(page.locator('.pdm-global-status')).toContainText('独立权限保持不变')
  await page.locator('.pdm-sidebar').getByRole('button', { name: '项目中心', exact: true }).click()
  await page.getByRole('button', { name: '文件', exact: true }).click()
  await page.locator('.pdm-folder-pane').getByText('项目资料', { exact: true }).click()
  await expect(page.getByRole('button', { name: '新建文件夹', exact: true })).toHaveCount(0)
  await page.locator('.pdm-folder-pane').getByText('独立权限目录', { exact: true }).click()
  await expect(page.getByRole('button', { name: '新建文件夹', exact: true })).toBeVisible()
  await page.screenshot({ path: join(tmpdir(), 'pdm-folder-template-preserved.png'), fullPage: false })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('table column settings follow the account after clearing browser storage', async ({ page }) => {
  await mockPlanCompanionPanels(page)
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [] }))
  await page.setViewportSize({ width: 1959, height: 1080 })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  const login = async (username: string) => {
    const form = page.getByLabel('登录PLM')
    await form.getByRole('textbox', { name: '账号' }).fill(username)
    await form.getByRole('textbox', { name: '密码' }).fill('correct-password')
    await form.getByRole('button', { name: '登录', exact: true }).click()
    await enterProject(page)
  }
  const procurement = async () => {
    await page.getByRole('button', { name: '备料', exact: true }).click()
    return page.getByLabel('采购跟踪', { exact: true })
  }
  await page.goto('/'); await login('engineer')
  let panel = await procurement()
  await panel.getByRole('button', { name: '列设置', exact: true }).click()
  let dialog = page.getByRole('dialog', { name: '采购跟踪列设置', exact: true })
  await dialog.getByText('PR', { exact: true }).click()
  await dialog.getByText('品牌', { exact: true }).click()
  await dialog.getByRole('button', { name: '保存', exact: true }).click()
  await expect(dialog).not.toBeVisible()
  await expect(panel.getByRole('columnheader', { name: 'PR', exact: true })).toBeVisible()
  await expect(panel.getByRole('columnheader', { name: '品牌', exact: true })).toHaveCount(0)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '标准件BOM' }).click()
  await page.getByRole('button', { name: '列设置', exact: true }).click()
  const bomDialog = page.getByRole('dialog', { name: 'BOM列设置', exact: true })
  await bomDialog.getByText('备注信息', { exact: true }).click()
  await bomDialog.getByRole('button', { name: '保存', exact: true }).click()
  await expect(bomDialog).not.toBeVisible()
  await page.evaluate(() => { localStorage.clear(); sessionStorage.clear() }); await page.reload(); await login('engineer')
  panel = await procurement()
  await expect(panel.getByRole('columnheader', { name: 'PR', exact: true })).toBeVisible()
  await expect(panel.getByRole('columnheader', { name: '品牌', exact: true })).toHaveCount(0)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '标准件BOM' }).click()
  await expect(page.locator('.pdm-bom-table').getByRole('columnheader', { name: '备注信息', exact: true })).toHaveCount(0)
  await page.getByRole('button', { name: '退出', exact: true }).click(); await login('admin')
  panel = await procurement()
  await expect(panel.getByRole('columnheader', { name: 'PR', exact: true })).toHaveCount(0)
  await expect(panel.getByRole('columnheader', { name: '品牌', exact: true })).toBeVisible()
  await page.getByRole('button', { name: '退出', exact: true }).click(); await login('engineer')
  panel = await procurement()
  await expect(panel.getByRole('columnheader', { name: 'PR', exact: true })).toBeVisible()
  await panel.getByRole('button', { name: '列设置', exact: true }).click()
  dialog = page.getByRole('dialog', { name: '采购跟踪列设置', exact: true })
  await expect(dialog.getByRole('checkbox', { name: 'PR', exact: true })).toBeChecked()
  await expect(dialog.getByRole('checkbox', { name: '品牌', exact: true })).not.toBeChecked()
  await page.screenshot({ path: join(tmpdir(), 'account-table-preferences-restored.png'), fullPage: false })
  await dialog.getByRole('button', { name: '恢复默认', exact: true }).click()
  await dialog.getByRole('button', { name: '保存', exact: true }).click(); await expect(dialog).not.toBeVisible()
  await page.evaluate(() => { localStorage.clear(); sessionStorage.clear() }); await page.reload(); await login('engineer')
  panel = await procurement()
  await expect(panel.getByRole('columnheader', { name: 'PR', exact: true })).toHaveCount(0)
  await expect(panel.getByRole('columnheader', { name: '品牌', exact: true })).toBeVisible()
  expect(errors).toEqual([])
})

for (const [field,lookup] of [['型号','PK-MOUNT-01'],['物料编码','UKIT-000001']]) {
  test('BOM paginated insertion keeps typing visible and confirms kit details via '+field,async({page})=>{
    test.setTimeout(120_000);await mockPlanCompanionPanels(page);await page.setViewportSize({width:1958,height:1080})
    const errors:string[]=[],saves:any[]=[],expansions:any[]=[];page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text())})
    const original=Array.from({length:160},(_,i)=>({id:'page-item-'+i,kind:'Standard',sequence:i+1,drawingNumber:'0102'+String(i+1).padStart(7,'0'),name:'已有物料'+i,specification:'ORIGINAL-'+i,brand:'UPTON',quantity:1,unit:'001',revision:'W1',complete:true,source:'Manual'}))
    const kit={id:'page-kit',code:'UKIT-000001',model:'PK-MOUNT-01',name:'分页确认套件',brand:'UPTON',currentReleasedRevisionId:'page-kit-release',revisions:[{id:'page-kit-release',versionNumber:1,state:'Released',components:[{id:'page-part-1',materialCode:'01029999001',materialName:'安装螺栓',specification:'M8',brand:'UPTON',quantity:5,unit:'001'},{id:'page-part-2',materialCode:'01029999002',materialName:'安装垫圈',specification:'D8',brand:'UPTON',quantity:2,unit:'001'}]}]}
    await page.route('**/api/engineering-kits?*',r=>r.fulfill({json:[kit]}));await page.route('**/api/material-code/resolve',r=>r.fulfill({json:[]}));await page.route('**/api/material-code/validate',r=>r.fulfill({json:[]}))
    await page.route('**/api/engineering-kits/*/expand',r=>{const input=r.request().postDataJSON();expansions.push(input);return r.fulfill({json:{referenceId:'page-reference',kitId:kit.id,revisionId:'page-kit-release',kitCode:kit.code,kitName:kit.name,versionNumber:1,kitQuantity:input.quantity,lines:kit.revisions[0]!.components.map(c=>({kitComponentId:c.id,materialCode:c.materialCode,materialName:c.materialName,specification:c.specification,brand:c.brand,quantity:c.quantity*input.quantity,unit:c.unit,isOptional:false}))}})})
    await page.route('**/api/projects/*/boms/Standard**',r=>{if(r.request().method()==='GET')return r.fulfill({json:original});const items=r.request().postDataJSON().items;saves.push(items);return r.fulfill({json:{items,draft:{id:'paged-draft',projectId,kind:'Standard',state:'Draft',versionNumber:1,label:'S-PAGED-V01',items:[],createdBy:'engineer',createdAt:'2026-10-23T04:00:00Z',updatedBy:'engineer',updatedAt:'2026-10-23T04:00:00Z'},draftItemIds:items.map((x:any)=>x.id),draftItemOverrides:[]}})})
    await page.goto('/',{waitUntil:'domcontentloaded'});const login=page.getByLabel('登录PLM');await login.getByRole('textbox',{name:'账号'}).fill('engineer');await login.getByRole('textbox',{name:'密码'}).fill('correct-password');await login.getByRole('button',{name:'登录',exact:true}).click();await enterProject(page);await page.getByRole('button',{name:'BOM',exact:true}).click();await page.getByRole('tab',{name:'标准件BOM'}).click()
    await expect(page.getByLabel('BOM明细分页',{exact:true})).toContainText('1 / 4')
    if(field==='物料编码')await page.getByRole('button',{name:'在第 50 行下方插入物料',exact:true}).click()
    const name=field==='型号'?page.locator('tr.is-quick-entry input[aria-label="物料名称"]'):page.locator('tr.is-inserted-row input[aria-label="物料名称"]');await name.click();await page.keyboard.type('NEW-ROW',{delay:20})
    const row=page.locator('tr.is-inserted-row');await expect(row).toHaveAttribute('data-row-index','50');await expect(row.getByLabel('物料名称',{exact:true})).toHaveValue('NEW-ROW');await expect(row.getByLabel('物料名称',{exact:true})).toBeFocused();await expect(page.getByLabel('BOM明细分页',{exact:true})).toContainText('2 / 4')
    const input=row.getByLabel(field!,{exact:true});await input.click();await page.keyboard.type(lookup!,{delay:20});await page.keyboard.press('Tab')
    const dialog=page.getByRole('dialog',{name:'确认添加套件',exact:true});await expect(dialog).toBeVisible();const preview=dialog.getByRole('table',{name:'套件展开明细',exact:true});await expect(preview.locator('tbody tr')).toHaveCount(2);expect(expansions).toHaveLength(0);await dialog.getByLabel('套件数量',{exact:true}).fill('2');await expect(preview.locator('tbody tr').first().locator('td').last()).toHaveText('10');await expect(preview.locator('tbody tr').nth(1).locator('td').last()).toHaveText('4')
    await page.screenshot({path:join(tmpdir(),'pdm-kit-confirm-'+(field==='型号'?'model':'code')+'.png'),fullPage:false})
    await dialog.getByRole('button',{name:'取消',exact:true}).click();await expect(dialog).not.toBeVisible();expect(expansions).toHaveLength(0);await expect(row.getByLabel('物料名称',{exact:true})).toHaveValue('NEW-ROW');expect(saves).toHaveLength(0)
    await input.press('Enter');await expect(dialog).toBeVisible();await dialog.getByLabel('套件数量',{exact:true}).fill('2');await dialog.getByRole('button',{name:'确认添加到BOM',exact:true}).click();await expect(dialog).not.toBeVisible();expect(expansions).toHaveLength(1);await expect(page.locator('tr.is-inserted-row')).toHaveCount(2);await expect(page.getByLabel('BOM明细分页',{exact:true})).toContainText('2 / 4');await expect(page.locator('tr.is-inserted-row').first().getByLabel('物料名称',{exact:true})).toHaveValue('安装螺栓')
    await page.screenshot({path:join(tmpdir(),'pdm-kit-insert-visible-'+(field==='型号'?'model':'code')+'.png'),fullPage:false})
    await page.locator('.pdm-bom-save-action').click();await expect.poll(()=>saves.length).toBe(1);expect(saves[0]).toHaveLength(162);expect(saves[0][50].drawingNumber).toBe('01029999001');expect(saves[0][51].drawingNumber).toBe('01029999002');expect(saves[0].filter((x:any)=>x.source==='EngineeringKit').map((x:any)=>x.quantity)).toEqual([10,4]);expect(saves[0].some((x:any)=>x.drawingNumber.startsWith('UKIT-'))).toBe(false);expect(original.every(old=>saves[0].some((x:any)=>x.id===old.id&&x.drawingNumber===old.drawingNumber&&x.quantity===1))).toBe(true)
    await expect(page.locator('vite-error-overlay')).toHaveCount(0);expect(errors).toEqual([])
  })
}

test('project record viewers can read history without note maintenance or plan access on LAN HTTP',async({page})=>{
  test.setTimeout(120_000);await mockPlanCompanionPanels(page);await page.setViewportSize({width:1959,height:1080})
  const errors:string[]=[],recordReads:string[]=[],writes:string[]=[];let planDenied=false
  page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error'&&!m.location().url.includes('/plan/portfolio'))errors.push(m.text())});page.on('request',r=>{const u=new URL(r.url());if(r.method()!=='GET'&&(u.pathname.endsWith('/manager-notes')||u.pathname.endsWith('/todos')))writes.push(u.pathname)})
  await page.route('**/api/projects/*/plan/portfolio',r=>r.fulfill(planDenied?{status:403,json:{message:'当前角色无权查看计划'}}:{json:{rootProjectId:projectId,currentStage:'Design',completionPercent:0,laggingProjectCount:0,riskProjectCount:0,projects:[{projectId,projectCode:'PRJ-REAL-001',projectName:'真实装配项目',isRoot:true,hasPlan:false,completionPercent:0,isLagging:false,isAtRisk:false}]}}))
  await page.route('**/api/projects/*/audit?*',r=>{const id=new URL(r.request().url()).pathname.split('/')[3]!;recordReads.push(id);return r.fulfill({json:[{id:'record-'+id,actor:'project-manager',action:'project.manager-note',entityType:'Project',entityId:id,detail:'项目经理填写的可见记录',occurredAt:'2026-10-23T06:00:00Z'}]})})
  await page.goto('/',{waitUntil:'domcontentloaded'});expect(await page.title()).toBe('UPTON-PLM');const login=page.getByLabel('登录PLM');await login.getByRole('textbox',{name:'账号'}).fill('engineer');await login.getByRole('textbox',{name:'密码'}).fill('correct-password');await login.getByRole('button',{name:'登录',exact:true}).click();await enterProject(page)
  const record=page.getByRole('button',{name:'查看 PRJ-REAL-001 的记录',exact:true});await expect(record).toBeVisible();await record.click()
  const dialog=page.getByRole('dialog',{name:'查看项目记录 · PRJ-REAL-001',exact:true});await expect(dialog).toBeVisible();await expect(dialog).toContainText('项目经理填写的可见记录');await expect(dialog.getByLabel('项目备注内容',{exact:true})).toHaveCount(0);await expect(dialog.getByLabel('待办接收人',{exact:true})).toHaveCount(0);await expect(dialog.getByRole('button',{name:'保存记录',exact:true})).toHaveCount(0);await expect.poll(async()=>{const b=(await dialog.boundingBox())!;return b.x+b.width}).toBeLessThanOrEqual(1960);await expect.poll(()=>dialog.evaluate(el=>getComputedStyle(el).opacity)).toBe('1');await page.screenshot({path:join(tmpdir(),'pdm-project-record-readonly.png'),fullPage:false});await dialog.getByRole('button',{name:'关闭',exact:true}).click();await expect(dialog).not.toBeVisible()
  planDenied=true;const denied=page.waitForResponse(r=>new URL(r.url()).pathname.endsWith('/plan/portfolio')&&r.status()===403);await page.getByRole('button',{name:'刷新项目概览',exact:true}).click();await denied
  const fallback=page.getByRole('button',{name:'查看记录',exact:true});await expect(fallback).toBeVisible();await fallback.click();await expect(dialog).toBeVisible();await expect(dialog).toContainText('项目经理填写的可见记录');await expect(dialog.getByRole('button',{name:'保存记录',exact:true})).toHaveCount(0);expect(recordReads).toContain(projectId);expect(writes).toEqual([])
  await page.setViewportSize({width:1000,height:900});await expect(dialog).toBeVisible();await expect.poll(async()=>{const b=(await dialog.boundingBox())!;return b.x+b.width}).toBeLessThanOrEqual(1001);await expect.poll(()=>dialog.evaluate(el=>getComputedStyle(el).opacity)).toBe('1');await page.screenshot({path:join(tmpdir(),'pdm-project-record-readonly-without-plan.png'),fullPage:false});await dialog.getByRole('button',{name:'关闭',exact:true}).click();await expect(page.locator('vite-error-overlay')).toHaveCount(0);expect(errors).toEqual([])
})

test('BOM indexes released kits by model and confirms duplicate models on LAN HTTP',async({page})=>{
  test.setTimeout(120_000);await mockPlanCompanionPanels(page);await page.setViewportSize({width:1958,height:1080})
  const errors:string[]=[],saves:any[]=[],expansions:any[]=[];let duplicate=false
  page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text())})
  const kit={id:'kit-model-one',code:'UKIT-000001',model:'PK-MOUNT-01',name:'型号索引套件',brand:'UPTON',currentReleasedRevisionId:'kit-release',revisions:[{id:'kit-release',versionNumber:1,state:'Released',components:[{id:'kit-part-1',materialCode:'01029999001',materialName:'安装螺栓',specification:'M8',brand:'UPTON',quantity:5,unit:'001'},{id:'kit-part-2',materialCode:'01029999002',materialName:'安装垫圈',specification:'D8',brand:'UPTON',quantity:2,unit:'001'}]}]}
  await page.route('**/api/engineering-kits?*',r=>r.fulfill({json:duplicate?[kit,{...kit,id:'kit-model-two',code:'UKIT-000002',name:'同型号另一套件'}]:[kit]}))
  await page.route('**/api/engineering-kits/*/expand',async r=>{const input=r.request().postDataJSON();expansions.push(input);return r.fulfill({json:{referenceId:'kit-ref-'+expansions.length,kitId:kit.id,revisionId:'kit-release',kitCode:kit.code,kitName:kit.name,versionNumber:1,kitQuantity:input.quantity,lines:[{kitComponentId:'kit-part-1',materialCode:'01029999001',materialName:'安装螺栓',quantity:5*input.quantity,unit:'001',specification:'M8',brand:'UPTON',material:'钢',isOptional:false},{kitComponentId:'kit-part-2',materialCode:'01029999002',materialName:'安装垫圈',quantity:2*input.quantity,unit:'001',specification:'D8',brand:'UPTON',material:'钢',isOptional:false}]}})})
  await page.route('**/api/material-code/resolve',r=>r.fulfill({json:[]}))
  await page.route('**/api/material-code/validate',r=>r.fulfill({json:[]}))
  await page.route('**/boms/Standard?includeDraft=true',async r=>{const items=r.request().postDataJSON().items;saves.push(items);return r.fulfill({json:{items,draft:{id:'kit-saved-draft',projectId,kind:'Standard',state:'Draft',versionNumber:1,label:'S-KIT-V01',items:[],createdBy:'engineer',createdAt:'2026-10-23T03:00:00Z',updatedBy:'engineer',updatedAt:'2026-10-23T03:00:00Z'},draftItemIds:items.map((x:any)=>x.id),draftItemOverrides:[]}})})
  await page.goto('/',{waitUntil:'domcontentloaded'});expect(await page.title()).toBe('UPTON-PLM');const login=page.getByLabel('登录PLM');await login.getByRole('textbox',{name:'账号'}).fill('engineer');await login.getByRole('textbox',{name:'密码'}).fill('correct-password');await login.getByRole('button',{name:'登录',exact:true}).click()
  await enterProject(page);await page.getByRole('button',{name:'BOM',exact:true}).click();await page.getByRole('tab',{name:'标准件BOM'}).click()
  let model=page.locator('tr.is-quick-entry input[aria-label="型号"]');await model.fill(' pk-mount-01 ');await model.press('Enter')
  const confirmation=page.getByRole('dialog',{name:'确认添加套件',exact:true});await expect(confirmation).toBeVisible();expect(expansions).toHaveLength(0);await expect(confirmation.getByRole('table',{name:'套件展开明细',exact:true}).locator('tbody tr')).toHaveCount(2);await confirmation.getByRole('button',{name:'确认添加到BOM',exact:true}).click();await expect.poll(()=>expansions.length).toBe(1);await expect(page.locator('.pdm-global-status')).toContainText('已确认添加套件 UKIT-000001');expect(saves).toHaveLength(0)
  await page.screenshot({path:join(tmpdir(),'pdm-bom-kit-model-expanded.png'),fullPage:false})
  await page.locator('.pdm-bom-save-action').click();await expect.poll(()=>saves.length).toBe(1)
  const children=saves[0].filter((x:any)=>x.source==='EngineeringKit');expect(children.map((x:any)=>x.drawingNumber)).toEqual(['01029999001','01029999002']);expect(children.map((x:any)=>x.quantity)).toEqual([5,2]);expect(children.every((x:any)=>x.engineeringKitRevisionId==='kit-release')).toBe(true)
  duplicate=true;model=page.locator('tr.is-quick-entry input[aria-label="型号"]');await model.fill('PK-MOUNT-01');await model.press('Tab')
  const choice=page.getByRole('dialog',{name:'确认添加套件',exact:true});await expect(choice).toBeVisible();await expect(choice.locator('.pdm-kit-reference-list tbody tr')).toHaveCount(2);await expect(choice.getByRole('columnheader',{name:'型号',exact:true})).toBeVisible();expect(expansions).toHaveLength(1)
  await choice.getByLabel('搜索套件',{exact:true}).fill('mount');await expect(choice.locator('.pdm-kit-reference-list tbody tr')).toHaveCount(2);await choice.getByLabel('套件数量',{exact:true}).fill('2')
  await page.screenshot({path:join(tmpdir(),'pdm-bom-kit-model-choices.png'),fullPage:false})
  await page.setViewportSize({width:1000,height:900});await expect(choice).toBeVisible();const box=(await choice.boundingBox())!;expect(box.x+box.width).toBeLessThanOrEqual(1001);await page.screenshot({path:join(tmpdir(),'pdm-bom-kit-model-choices-narrow.png'),fullPage:false})
  await choice.getByRole('button',{name:'取消',exact:true}).click();await expect(choice).not.toBeVisible();await page.setViewportSize({width:1958,height:1080});await page.locator('.pdm-bom-save-action').click();await expect(page.locator('.pdm-global-status')).toContainText('套件型号尚未展开');expect(saves).toHaveLength(1)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0);expect(errors).toEqual([])
})

test('manual work hour reminders repeat and show explicit skip reasons on LAN HTTP',async({page})=>{
  test.setTimeout(120_000)
  await mockPlanCompanionPanels(page);await page.setViewportSize({width:1958,height:1080});await page.clock.setFixedTime(new Date('2026-10-23T01:20:00Z'))
  const errors:string[]=[],sent:string[][]=[];let paused=false
  page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text())})
  const view:WorkHoursView={today:'2026-10-23',startDate:'2026-10-22',username:'engineer',displayName:'技术助理',scope:'授权部门',defaultDepartment:'设计部',canManage:true,manageableUsernames:['worker'],people:[{username:'worker',displayName:'员工甲',department:'设计部',isActive:true}],projects:[],sheets:[],compliance:[{username:'worker',days:[{date:'2026-10-22',expected:8,actual:0,status:'未填写',muted:false,reason:''}]}]}
  await page.route('**/api/work-hours**',async r=>{if(r.request().method()==='POST'&&new URL(r.request().url()).pathname.endsWith('/remind')){const names=r.request().postDataJSON().usernames;sent.push(names);return r.fulfill({json:{sent:paused?0:1,skipped:paused?1:0,results:[{username:'worker',displayName:'员工甲',sent:!paused,reason:paused?'缺填日期已暂停提醒':'已发送站内工时提醒'}]}})}return r.fulfill({json:view})})
  await page.goto('/',{waitUntil:'domcontentloaded'});expect(await page.title()).toBe('UPTON-PLM');const login=page.getByLabel('登录PLM');await login.getByRole('textbox',{name:'账号'}).fill('engineer');await login.getByRole('textbox',{name:'密码'}).fill('correct-password');await login.getByRole('button',{name:'登录',exact:true}).click()
  await page.locator('.pdm-sidebar').getByRole('button',{name:'工时管理',exact:true}).click();const panel=page.getByRole('region',{name:'工时管理',exact:true});await panel.getByRole('button',{name:'汇总工时',exact:true}).click();const checks=panel.getByRole('region',{name:'人员填报完整性',exact:true})
  await expect(checks).not.toContainText('每人每天最多一次');await expect(checks).not.toContainText('手动提醒可以再次发送')
  for(let i=0;i<2;i++){await checks.getByRole('button',{name:'提醒',exact:true}).click();await expect.poll(()=>sent.length).toBe(i+1);await expect(page.locator('.pdm-global-status')).toContainText('已向1人发送站内工时提醒')}
  await checks.getByRole('button',{name:'提醒当前缺填人员',exact:true}).click();await expect.poll(()=>sent.length).toBe(3);expect(sent).toEqual([['worker'],['worker'],['worker']]);await expect(checks).toContainText('1天待处理')
  await page.screenshot({path:join(tmpdir(),'pdm-work-hours-manual-reminder.png'),fullPage:false})
  paused=true;await checks.getByRole('button',{name:'提醒',exact:true}).click();await expect(page.locator('.pdm-global-status')).toContainText('本次未发送提醒：员工甲：缺填日期已暂停提醒。');await expect(page.locator('.pdm-global-status')).not.toHaveClass(/is-success/)
  await page.screenshot({path:join(tmpdir(),'pdm-work-hours-reminder-skip.png'),fullPage:false});await page.setViewportSize({width:1000,height:900});await expect(checks).toBeVisible();await page.screenshot({path:join(tmpdir(),'pdm-work-hours-reminder-narrow.png'),fullPage:false});expect(errors).toEqual([]);await expect(page.locator('vite-error-overlay')).toHaveCount(0)
})

test('drawing revisions use formal letters and pending status on LAN HTTP without changing archive ids', async ({ page }) => {
  await mockPlanCompanionPanels(page)
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [] }))
  await page.setViewportSize({ width: 1959, height: 1080 })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(`${message.text()} ${message.location().url}`) })
  const rows = [
    { id: 'revision-a', sourceDocumentId: 'doc-part', sequence: 1, drawingNumber: 'PART-A', name: '已发布底板', revision: 'W1' },
    { id: 'revision-b', sourceDocumentId: 'model-b', sequence: 2, drawingNumber: 'PART-B', name: '修改中的支架', revision: 'A-W2' },
    { id: 'revision-first', sourceDocumentId: 'model-first', sequence: 3, drawingNumber: 'PART-FIRST', name: '首次设计件', revision: 'W7' },
  ].map(item => ({ ...item, kind: 'NonStandard', quantity: 1, unit: '001', material: '钢', isComplete: true, source: 'Auto' }))
  await page.route(new RegExp(`/api/projects/${projectId}/boms/NonStandard(?:\\?.*)?$`), route => route.fulfill({ json: rows }))
  await page.route(`**/api/projects/${projectId}/release-packages`, route => route.fulfill({ json: [{
    id: 'revision-release', number: 'RP-REVISION-A', state: 5, scope: 'NonStandardWithDrawing', createdAt: '2026-10-21T00:00:00Z', publishedAt: '2026-10-21T01:00:00Z',
    standardBomSnapshot: [], nonStandardBomSnapshot: [rows[0]], electricalBomSnapshot: [], selectedBomItemIds: ['revision-a'], approvalTasks: [],
    versionBindings: [{ documentId: 'doc-part', versionId: 'frozen-work', revision: 'W1', targetRevision: 'A', publish: true, requiresPromotion: true, sha256: 'hash' }],
  }] }))
  await page.goto('/')
  await expect(page).toHaveTitle('UPTON-PLM')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '非标件BOM' }).click()
  const table = page.locator('.pdm-bom-table')
  await expect(table.getByRole('columnheader', { name: '图纸版次', exact: true })).toBeVisible()
  const first = table.locator('tbody tr').filter({ hasText: '已发布底板' })
  await expect(first.locator('[title="已发布；存档版本：W1"]')).toHaveText('A')
  await expect(table.locator('tbody tr').filter({ hasText: '修改中的支架' })).toContainText('B-D')
  const pendingRevision = table.locator('[title="修订中；当前生产使用 A 版；存档版本：A-W2"]')
  expect(await pendingRevision.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
  await expect(table.locator('tbody tr').filter({ hasText: '首次设计件' })).toContainText('A-D')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  if (process.env.PDM_REVISION_SCREENSHOT_DIR) {
    await page.screenshot({ path: join(process.env.PDM_REVISION_SCREENSHOT_DIR, 'drawing-revision-desktop.png'), fullPage: false })
  }
  await page.getByRole('tab', { name: '标准件BOM' }).click()
  await page.getByRole('tab', { name: '非标件BOM' }).click()
  await expect(first.locator('[title="已发布；存档版本：W1"]')).toHaveText('A')
  await page.setViewportSize({ width: 1000, height: 900 })
  await expect(table.locator('tbody tr').filter({ hasText: '修改中的支架' })).toContainText('B-D')
  if (process.env.PDM_REVISION_SCREENSHOT_DIR) {
    await page.screenshot({ path: join(process.env.PDM_REVISION_SCREENSHOT_DIR, 'drawing-revision-narrow.png'), fullPage: false })
  }
  expect(rows.map(item => item.revision)).toEqual(['W1', 'A-W2', 'W7'])
  expect(errors).toEqual([])
})

test('nonstandard procurement uses manufacturing quantities, batches and exports on LAN HTTP',async({page})=>{
  await mockPlanCompanionPanels(page)
  await page.setViewportSize({width:1916,height:1080})
  const errors:string[]=[],inventoryRequests:string[]=[],refreshRequests:string[]=[]
  page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text())})
  page.on('request',r=>{if(r.url().includes('/api/material-inventory'))inventoryRequests.push(r.url());if(r.url().includes('/procurement-tracking/refresh'))refreshRequests.push(r.url())})
  const base={projectCode:'PRJ-REAL-001',materialCode:'02040001',materialName:'非标底板',specification:'BASE-42',quantity:10,bomKind:'非标件',releasePackageNumber:'RP-MFG-0001',purchaseRequisitionNumbers:[],purchaseRequisitionStatus:'',purchaseOrderNumbers:[],purchaseOrderStatus:'',purchaseQuantity:999,arrivedQuantity:999,requestedQuantity:null,approvedQuantity:null,warehouseMovements:[],details:[]}
  const movement={id:'move',requestId:'req',lotId:'lot',kind:'QualityReceived',quantity:8,actor:'制造人员',occurredAt:'2026-10-21T10:00:00+08:00',note:'分批交检，待完成检验'}
  const manufacturing={releasePackageId:'release',lineId:'line',revision:'A',publishedAt:'2026-10-20T10:00:00+08:00',receivedAt:'2026-10-21T09:00:00+08:00',status:'质检',assignedQuantity:10,submittedQuantity:8,qualifiedQuantity:5,issuedQuantity:2,responsible:'制造负责人',suppliers:'外协厂',requiredOn:'2026-10-24',forecastOn:'2026-10-26',qualifiedAt:'2026-10-21',issuedAt:'2026-10-21',batches:[{id:'batch-external',route:'External',quantity:10,status:'质检',supplier:'外协厂',assignee:'制造负责人',promisedOn:'2026-10-26',deliveredQuantity:10,submittedQuantity:8,qualifiedQuantity:5,issuedQuantity:2,history:[movement]}]}
  const response:any={projectId,projectCode:'PRJ-REAL-001',hasPublishedBom:true,manufacturingReadAt:'2026-10-22T10:00:00+08:00',items:[{...base,sequence:1,manufacturing},{...base,sequence:2,quantity:2,manufacturing:{...manufacturing,lineId:'line-b',revision:'B',receivedAt:null,status:'待生产接收',assignedQuantity:0,submittedQuantity:0,qualifiedQuantity:0,issuedQuantity:0,responsible:null,suppliers:null,forecastOn:null,batches:[]}}]}
  await page.route(`**/api/projects/${projectId}/procurement-tracking`,r=>r.fulfill({json:response}))
  await page.route(`**/api/projects/${projectId}/export-metadata`,r=>r.fulfill({json:{title:'物料跟踪',company:'测试公司',scope:'当前项目',versionLabel:'版本',version:'测试',exportedAt:'2026-10-22T10:00:00+08:00',exportedBy:'测试人员',fields:[]}}))
  await page.route('**/api/material-inventory?*',r=>r.fulfill({json:{items:[],total:0,lastSuccessfulRefreshAt:'2026-10-22T10:00:00+08:00',warehouseNames:[]}}))
  await page.goto('/');expect(await page.title()).toBe('UPTON-PLM')
  const login=page.getByLabel('登录PLM');await login.getByRole('textbox',{name:'账号'}).fill('engineer');await login.getByRole('textbox',{name:'密码'}).fill('correct-password');await login.getByRole('button',{name:'登录',exact:true}).click()
  await enterProject(page);await page.getByRole('button',{name:'备料',exact:true}).click()
  const panel=page.getByLabel('采购跟踪',{exact:true}),rows=panel.locator('.el-table__body tbody tr')
  const expectOriginalHeaders=async()=>{
    const headers=(await panel.locator('.el-table__header th').allTextContents()).map(label=>label.trim())
    expect(headers).toEqual(expect.arrayContaining(['库存','请购状态','请购日期','PO','采购状态','采购员','购买数','到货数','入库日期','入库数','出库日期','出库数']))
    expect(headers.some(label=>/[／/]/.test(label))).toBe(false)
    await expect(panel.getByRole('button',{name:'筛选请购状态',exact:true})).toHaveText('全部请购状态')
    await expect(panel.getByRole('button',{name:'筛选采购状态',exact:true})).toHaveText('全部采购状态')
    await expect(panel.getByRole('button',{name:'筛选采购员',exact:true})).toHaveText('全部采购员')
  }
  const stats = panel.locator('.procurement-stats-card')
  const statsLabels = ['齐套率','超期物料','交期不符','缺料风险','关键物料','待落实物料']
  await expect(rows).toHaveCount(2);await expect(stats.locator('.procurement-stats-card__label')).toHaveText(statsLabels);await expect(panel).not.toContainText('无PO')
  await expect(stats.first()).toContainText('0/2 项已齐套')
  await expect(stats.last().locator('.procurement-stats-card__value')).toHaveText('1')
  await expectOriginalHeaders()
  await panel.getByRole('button',{name:'列设置',exact:true}).click()
  const columnSettings=page.getByRole('dialog',{name:'采购跟踪列设置',exact:true})
  await columnSettings.getByText('PR',{exact:true}).click()
  await expect(columnSettings.getByRole('checkbox',{name:'PR',exact:true})).toBeChecked()
  await columnSettings.getByRole('button',{name:'保存',exact:true}).click()
  await expect(panel.getByRole('columnheader',{name:'PR',exact:true})).toBeVisible()
  await expect(panel.getByRole('columnheader',{name:'PO',exact:true})).toBeVisible()
  await expect(panel.getByRole('button',{name:'库存设置',exact:true})).toHaveCount(0)
  const first=rows.first();await expect(first).toContainText('制造中心');await expect(first).not.toContainText('999')
  expect(inventoryRequests).toEqual([])
  await panel.getByRole('button',{name:'查看非标底板制造明细',exact:true}).first().click()
  const drawer=page.getByRole('dialog',{name:'非标件制造明细',exact:true});await expect(drawer).toContainText('RP-MFG-0001');await expect(drawer).toContainText('外协厂');await expect(drawer).toContainText('分批交检，待完成检验')
  await expect.poll(async()=>{const b=(await drawer.boundingBox())!;return b.x+b.width}).toBeLessThanOrEqual(1917)
  await page.screenshot({path:join(tmpdir(),'pdm-procurement-manufacturing-batches.png'),fullPage:false})
  await drawer.getByRole('button',{name:'关闭此对话框'}).click();await expect(drawer).not.toBeVisible()
  const downloaded=page.waitForEvent('download');await panel.getByRole('button',{name:'导出 Excel',exact:true}).click();expect((await downloaded).suggestedFilename()).toMatch(/物料状态清单.*xlsx$/)
  await panel.getByRole('button',{name:'立即刷新',exact:true}).click();expect(refreshRequests).toEqual([]);expect(inventoryRequests).toEqual([])
  await page.screenshot({path:join(tmpdir(),'pdm-procurement-manufacturing.png'),fullPage:false})
  await page.setViewportSize({width:1000,height:900});await expect(panel).toBeVisible();await panel.getByRole('button',{name:'查看非标底板制造明细',exact:true}).first().click();await expect(drawer).toContainText('外协厂');await expect.poll(async()=>{const b=(await drawer.boundingBox())!;return b.x+b.width}).toBeLessThanOrEqual(1001);await page.screenshot({path:join(tmpdir(),'pdm-procurement-manufacturing-narrow.png'),fullPage:false})
  await drawer.getByRole('button',{name:'关闭此对话框'}).click();await expect(drawer).not.toBeVisible();await page.setViewportSize({width:1916,height:1080})
  response.items.push({...base,sequence:3,bomKind:'标准件',materialName:'标准紧固件',quantity:2,purchaseOrderNumbers:['PO-STD'],purchaseRequisitionNumbers:['PR-STD'],purchaseRequisitionStatus:'已核准',purchaseOrderStatus:'执行中',purchaseQuantity:2,arrivedQuantity:1})
  await panel.getByRole('button',{name:'立即刷新',exact:true}).click();await expect(rows).toHaveCount(3)
  await expectOriginalHeaders()
  await expect(stats.locator('.procurement-stats-card__label')).toHaveText(statsLabels);await expect(stats.first()).toContainText('0/3 项已齐套');await expect(rows.last()).toContainText('PO-STD');await expect(rows.last()).toContainText('U9C')
  await expect.poll(()=>inventoryRequests.length).toBeGreaterThan(0)
  await page.screenshot({path:join(tmpdir(),'pdm-procurement-mixed-sources.png'),fullPage:false})
  await panel.getByLabel('筛选物料分类').selectOption('标准件');await expect(rows).toHaveCount(1);await expectOriginalHeaders()
  await expect(stats.locator('.procurement-stats-card__label')).toHaveText(statsLabels);await expect(stats.first()).toContainText('0/1 项已齐套')
  await panel.getByLabel('筛选物料分类').selectOption('非标件');await expect(rows).toHaveCount(2);await expectOriginalHeaders()
  await expect(stats.locator('.procurement-stats-card__label')).toHaveText(statsLabels);await expect(stats.first()).toContainText('0/2 项已齐套')
  await panel.getByRole('button',{name:'筛选采购状态',exact:true}).click();await page.locator('.procurement-tracking__multi-options:visible').getByRole('checkbox',{name:'待生产接收',exact:true}).check();await expect(rows).toHaveCount(1);await expect(rows).toContainText('B')
  expect(errors).toEqual([]);await expect(page.locator('vite-error-overlay')).toHaveCount(0)
})

test('work hours organization tree scopes personnel, charts and completeness on LAN HTTP',async({page})=>{
  await mockPlanCompanionPanels(page)
  await page.clock.setFixedTime(new Date('2026-10-21T02:00:00Z'))
  await page.setViewportSize({width:1916,height:1080})
  const errors:string[]=[]
  page.on('pageerror',error=>errors.push(error.message))
  page.on('console',message=>{if((message.type()==='error'||message.type()==='warning')&&!/Component "El(Drawer|Dialog)" has already been registered/.test(message.text()))errors.push(message.text())})
  const project={id:'hours-root',code:'P700012',name:'装配设备',rootProjectId:'hours-root',rootProjectCode:'P700012',rootProjectName:'装配设备'}
  const people=[{username:'a',displayName:'员工甲',department:'机械组',departmentId:'t2-mech',isActive:true},{username:'b',displayName:'员工乙',department:'电气组',departmentId:'t2-elec',isActive:true},{username:'c',displayName:'员工丙',department:'机械组',departmentId:'t3-mech',isActive:true}]
  const value:WorkHoursView={today:'2026-10-21',startDate:'2026-10-20',username:'admin',displayName:'系统管理员',scope:'当前公司',people,projects:[project],defaultDepartmentId:'t2-mech',organizationUnits:[
    {id:'t2',parentUnitId:null,name:'T2事业部',sortOrder:1,isActive:true},{id:'t2-mech',parentUnitId:'t2',name:'机械组',sortOrder:1,isActive:true},{id:'t2-elec',parentUnitId:'t2',name:'电气组',sortOrder:2,isActive:true},
    {id:'t3',parentUnitId:null,name:'T3事业部',sortOrder:2,isActive:true},{id:'t3-mech',parentUnitId:'t3',name:'机械组',sortOrder:1,isActive:true}],
    sheets:people.map((p,i)=>({companyId:'org-ks',username:p.username,month:'2026-10',rowVersion:1,history:[],entries:[{...project,id:`entry-${i}`,projectId:project.id,projectCode:project.code,projectName:project.name,date:'2026-10-20',location:'厂内',hours:[8,4,6][i]!}]})),
    compliance:people.map((p,i)=>({username:p.username,days:[{date:'2026-10-20',expected:8,actual:[8,4,6][i]!,status:i===0?'完整':'填写不完整',muted:false,reason:''}]}))}
  await page.route('**/api/work-hours**',route=>route.fulfill({json:value}))
  await page.goto('/')
  expect(await page.title()).toBe('UPTON-PLM')
  const login=page.getByLabel('登录PLM')
  await login.getByRole('textbox',{name:'账号'}).fill('admin');await login.getByRole('textbox',{name:'密码'}).fill('correct-password');await login.getByRole('button',{name:'登录',exact:true}).click()
  await page.locator('.pdm-sidebar').getByRole('button',{name:'工时管理',exact:true}).click()
  const panel=page.getByRole('region',{name:'工时管理',exact:true})
  await panel.getByRole('button',{name:'汇总工时',exact:true}).click()
  const department=panel.getByRole('combobox',{name:'汇总部门',exact:true}),staff=panel.getByLabel('汇总人员',{exact:true})
  await expect(staff.locator('option')).toHaveText(['全部','员工甲'])
  await expect(panel.locator('.wh-toolbar')).toHaveCount(0)
  const bottom=panel.locator('.wh-report-bottom'),left=bottom.getByRole('region',{name:'人员填报完整性',exact:true}),right=bottom.getByRole('region',{name:'工时统计列表',exact:true})
  const [lb,rb,dc,pc]=await Promise.all([left.boundingBox(),right.boundingBox(),panel.locator('.wh-charts>section').nth(0).boundingBox(),panel.locator('.wh-charts>section').nth(1).boundingBox()])
  expect(Math.abs(lb!.width-rb!.width)).toBeLessThan(1);expect(lb!.height).toBe(rb!.height);expect(lb!.y).toBe(rb!.y);expect(rb!.x).toBeGreaterThan(lb!.x)
  expect(Math.abs(dc!.width-pc!.width)).toBeLessThan(1);expect(dc!.height).toBe(pc!.height);expect(dc!.y).toBe(pc!.y)
  await expect(left).not.toContainText('暂停提醒仍显示缺填');await expect(left).not.toContainText('自动提醒在北京时间9点后检查')
  await expect(right.getByRole('button',{name:'导出Excel',exact:true})).toBeVisible()
  await department.click()
  const tree=page.getByRole('tree')
  const t2=tree.getByRole('treeitem').filter({has:page.getByRole('option',{name:'T2事业部',exact:true})}).first()
  if(await t2.getAttribute('aria-expanded')==='true')await t2.locator(':scope > .el-tree-node__content > .el-tree-node__expand-icon').click()
  await t2.locator(':scope > .el-tree-node__content > .el-tree-node__expand-icon').click()
  await expect(t2.getByText('机械组',{exact:true})).toBeVisible()
  await expect(t2.getByText('电气组',{exact:true})).toBeVisible()
  await expect.poll(async()=> (await t2.boundingBox())!.height).toBeGreaterThan(70)
  await page.screenshot({path:join(tmpdir(),'pdm-work-hours-organization-tree.png'),fullPage:false})
  await t2.getByText('T2事业部',{exact:true}).click()
  await expect(staff.locator('option')).toHaveText(['全部','员工甲','员工乙'])
  await expect(panel.locator('.wh-report-summary').locator('b').first()).toHaveText('12')
  await staff.selectOption('b')
  await expect(panel.locator('.wh-report-summary').locator('b').first()).toHaveText('4')
  await department.click();await tree.getByText('T3事业部',{exact:true}).click()
  await expect(staff).toHaveValue('')
  await expect(staff.locator('option')).toHaveText(['全部','员工丙'])
  await expect(panel.locator('.wh-report-summary').locator('b').first()).toHaveText('6')
  await expect(panel.locator('.wh-report-table tbody')).toContainText('员工丙')
  await expect(panel.locator('.wh-report-table tbody')).not.toContainText('员工甲')
  await expect(panel.getByRole('region',{name:'人员填报完整性'}).locator('tbody')).toContainText('员工丙')
  await expect(panel.getByRole('region',{name:'人员填报完整性'}).locator('tbody')).not.toContainText('员工乙')
  await expect(panel.getByRole('img',{name:'按日期显示厂内、厂外工时'})).toContainText('6')
  const projectBars=panel.getByRole('group',{name:'按项目显示厂内、厂外工时',exact:true})
  await expect(projectBars.locator('button')).toHaveCount(1);await expect(projectBars).toContainText('6')
  await projectBars.getByRole('button',{name:'查看P700012项目工时明细',exact:true}).click()
  const projectDetail=page.getByRole('dialog',{name:'P700012 · 装配设备',exact:true});await expect(projectDetail).toContainText('员工丙');await projectDetail.getByRole('button',{name:'关闭此对话框'}).click();await expect(projectDetail).not.toBeVisible()
  await right.getByRole('button',{name:'按项目',exact:true}).click();await expect(right.locator('tbody')).toContainText('6小时')
  await right.getByRole('button',{name:'填报明细',exact:true}).click();await expect(right.locator('thead')).toContainText('日期');await expect(right.locator('tbody')).toContainText('装配设备')
  await right.getByRole('button',{name:'按人员',exact:true}).click();await expect(right.locator('tbody')).toContainText('员工丙')
  const download=page.waitForEvent('download');await panel.getByRole('button',{name:'导出Excel',exact:true}).click();expect((await download).suggestedFilename()).toMatch(/工时汇总.*\.xlsx$/)
  await page.screenshot({path:join(tmpdir(),'pdm-work-hours-organization-filtered.png'),fullPage:false})
  await department.hover();await panel.locator('.el-select__clear').click()
  await expect(staff.locator('option')).toHaveText(['全部','员工甲','员工乙','员工丙'])
  await expect(panel.locator('.wh-report-summary').locator('b').first()).toHaveText('18')
  await page.setViewportSize({width:1000,height:900})
  await expect(department).toBeVisible();await department.click();await tree.getByText('T3事业部',{exact:true}).click()
  await expect(staff.locator('option')).toHaveText(['全部','员工丙'])
  await expect(tree).not.toBeVisible()
  await page.screenshot({path:join(tmpdir(),'pdm-work-hours-organization-narrow.png'),fullPage:false})
  const [narrowLeft,narrowRight]=await Promise.all([left.boundingBox(),right.boundingBox()]);expect(narrowLeft!.width).toBe(narrowRight!.width);expect(narrowLeft!.height).toBe(narrowRight!.height);expect(narrowRight!.y).toBeGreaterThanOrEqual(narrowLeft!.y+narrowLeft!.height)
  await panel.getByLabel('汇总所在地',{exact:true}).selectOption('厂外');await expect(panel.locator('.wh-project-empty')).toHaveText('当前范围暂无工时记录');await expect(right.locator('.wh-empty')).toContainText('没有填报记录');await expect(left).toContainText('员工丙')
  expect(errors).toEqual([]);await expect(page.locator('vite-error-overlay')).toHaveCount(0)
})

test('work hours settings, completeness, mute and audited correction work on LAN HTTP', async ({ page }) => {
  test.setTimeout(90_000)
  await mockPlanCompanionPanels(page)
  await page.clock.setFixedTime(new Date('2026-10-21T02:00:00Z'))
  await page.setViewportSize({width:1916,height:1080})
  const errors: string[] = [], writes: Array<{path:string;input:any}> = []
  page.on('pageerror',error=>errors.push(error.message))
  page.on('console',message=>{if(message.type()==='error')errors.push(message.text())})
  const person={username:'hours-worker',displayName:'员工甲',department:'设计部',isActive:true}
  const project={id:'hours-child',code:'P700012-1',name:'上料单元',rootProjectId:'hours-root',rootProjectCode:'P700012',rootProjectName:'装配设备'}
  const entry={...project,id:'hours-entry',projectId:project.id,projectCode:project.code,projectName:project.name,date:'2026-10-20',location:'厂内',hours:4}
  const value:WorkHoursView={today:'2026-10-21',startDate:'2026-10-20',username:'admin',displayName:'系统管理员',scope:'当前公司',people:[person,{username:'test',displayName:'测试账号',department:'设计部',isActive:true}],projects:[project],sheets:[{companyId:'org-ks',username:person.username,month:'2026-10',rowVersion:1,entries:[entry],history:[]}],canManage:true,manageableUsernames:[person.username],defaultDepartment:'设计部',compliance:[{username:person.username,days:[{date:'2026-10-20',expected:8,actual:4,status:'填写不完整',muted:false,reason:''}]}]}
  const settings:WorkHoursManagement={today:value.today,rowVersion:1,canAuthorize:true,scope:'当前公司',people:value.people,units:[{id:'unit-design',name:'设计部'}],assistants:[{username:'assistant',displayName:'技术助理',department:'设计部',isActive:true}],requirements:[{id:'requirement',username:person.username,from:'2026-10-20',to:null,hours:8}],exceptions:[],grants:[],history:[]}
  settings.departmentGroups=[{id:'unit-design',name:'设计部',participating:true,people:[{...person,required:true,canEdit:false}]},{id:'unit-assembly',name:'装配部',participating:true,people:[{username:'assembly-worker',displayName:'员工乙',department:'装配组',isActive:true,required:false,canEdit:false}]}]
  await page.route('**/api/work-hours**',async route=>{
    const request=route.request(),path=new URL(request.url()).pathname
    if(request.method()!=='GET'){
      const input=request.postDataJSON();writes.push({path,input})
      if(path.endsWith('/settings')){settings.rowVersion++;for(const change of input.departments){settings.departmentGroups!.find(g=>g.id===change.unitId)!.participating=change.participating}return route.fulfill({json:settings})}
      if(path.endsWith('/requirements')){settings.rowVersion++;settings.requirements[0]!.to='2026-10-20';settings.requirements.push({id:'new',username:person.username,from:input.from,to:input.to,hours:input.hours});return route.fulfill({json:settings})}
      if(path.endsWith('/exceptions')){settings.rowVersion++;settings.exceptions.push({...input,id:'exception'});const day=value.compliance![0]!.days[0]!;day.muted=true;day.reason=`暂停提醒：${input.reason}`;return route.fulfill({json:settings})}
      if(path.endsWith('/correction')){const sheet=value.sheets[0]!;sheet.history.push({id:'rev',at:'2026-10-21T02:00:00Z',actor:'admin',dates:['2026-10-20'],before:[{...entry}],after:[{...entry,hours:8}],reason:input.reason});entry.hours=8;sheet.rowVersion++;Object.assign(value.compliance![0]!.days[0]!,{actual:8,status:'完整'});return route.fulfill({status:204})}
      if(path.endsWith('/remind'))return route.fulfill({json:{sent:1,skipped:0}})
    }
    if(path.endsWith('/correction'))return route.fulfill({json:{sheet:value.sheets[0],projects:[project]}})
    if(path.includes('/settings'))return route.fulfill({json:settings})
    return route.fulfill({json:value})
  })
  await page.goto('/')
  expect(await page.title()).toBe('UPTON-PLM')
  const login=page.getByLabel('登录PLM')
  const loginResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/auth/login')
  await login.getByRole('textbox',{name:'账号'}).fill('admin');await login.getByRole('textbox',{name:'密码'}).fill('correct-password');await login.getByRole('button',{name:'登录',exact:true}).click()
  const loginSession = await (await loginResponse).json()
  await page.route('**/api/auth/resume', route => route.fulfill({json:loginSession}))
  await page.locator('.pdm-sidebar').getByRole('button',{name:'工时管理',exact:true}).click()
  const panel=page.getByRole('region',{name:'工时管理',exact:true})
  await panel.getByRole('button',{name:'工时设置',exact:true}).click()
  await expect(panel.getByRole('region',{name:'工时设置',exact:true})).toBeVisible()
  await expect(panel.getByRole('region',{name:'设计部',exact:true})).toBeVisible()
  await expect(panel.getByRole('region',{name:'装配部',exact:true})).toBeVisible()
  await expect(panel.getByRole('checkbox',{name:'设计部员工甲需要填报',exact:true})).toBeDisabled()
  await panel.getByRole('checkbox',{name:'设计部参与工时',exact:true}).uncheck()
  await panel.getByRole('button',{name:'保存设置',exact:true}).click()
  await expect.poll(()=>writes.filter(x=>x.path.endsWith('/settings')).length).toBe(1)
  expect(writes[0]!.input).toMatchObject({companyId:'org-ks',rowVersion:1,departments:[{unitId:'unit-design',participating:false}],people:[]})
  await panel.getByRole('checkbox',{name:'设计部参与工时',exact:true}).check()
  await panel.getByRole('button',{name:'保存设置',exact:true}).click()
  await expect.poll(()=>writes.filter(x=>x.path.endsWith('/settings')).length).toBe(2)
  await page.reload()
  await expect(panel.getByRole('button',{name:'工时设置',exact:true})).toHaveClass(/active/)
  await expect(panel.getByRole('checkbox',{name:'设计部员工甲需要填报',exact:true})).toBeChecked()
  await page.screenshot({path:join(tmpdir(),'pdm-work-hours-settings.png'),fullPage:false})
  await panel.getByRole('button',{name:'汇总工时',exact:true}).click()
  const checks=panel.getByRole('region',{name:'人员填报完整性'})
  await expect(checks.locator('tbody')).toContainText('员工甲')
  await expect(checks.locator('tbody')).not.toContainText('测试账号')
  await expect(checks.locator('tbody')).toContainText('1天待处理')
  await panel.getByLabel('汇总所在地',{exact:true}).selectOption('厂外')
  await expect(checks.locator('tbody')).toContainText('1天待处理')
  await page.screenshot({path:join(tmpdir(),'pdm-work-hours-completeness.png'),fullPage:false})
  await checks.getByRole('button',{name:'逐日检查',exact:true}).click()
  const drawer=page.getByRole('dialog',{name:'员工甲 · 逐日填报检查'})
  await drawer.getByRole('button',{name:'调整应填／暂停提醒',exact:true}).click()
  const special=page.getByRole('dialog',{name:'特殊情况处理',exact:true})
  await special.getByLabel('特殊处理方式').selectOption('mute');await special.getByLabel('特殊处理原因').fill('特殊情况暂缓提醒')
  await special.getByRole('button',{name:'保存特殊处理'}).click()
  await expect(special).not.toBeVisible()
  await expect(checks.locator('tbody')).toContainText('已暂停提醒')
  await expect(drawer.locator('tbody')).toContainText('填写不完整')
  await drawer.getByRole('button',{name:'代更正实际工时'}).click()
  const correction=page.getByRole('dialog',{name:'员工甲 · 2026-10-20 · 代更正工时',exact:true})
  await correction.getByLabel('代更正小时').fill('8');await correction.getByLabel('代更正原因').fill('核对项目投入后补正')
  await correction.getByRole('button',{name:'保存代更正'}).click()
  await expect(correction).not.toBeVisible();await expect(drawer.locator('tbody')).toContainText('完整')
  expect(writes.find(x=>x.path.endsWith('/correction'))!.input).toMatchObject({username:'hours-worker',reason:'核对项目投入后补正',sheet:{versions:[{month:'2026-10',rowVersion:1}],entries:[{date:'2026-10-20',projectId:'hours-child',location:'厂内',hours:8}]}})
  await drawer.getByRole('button',{name:'代更正实际工时'}).click()
  await correction.locator('.wh-check-history>summary').click()
  await correction.locator('.wh-check-history details>summary').click()
  await expect(correction).toContainText('修改前');await expect(correction).toContainText('4小时');await expect(correction).toContainText('8小时')
  await page.screenshot({path:join(tmpdir(),'pdm-work-hours-correction-history.png'),fullPage:false})
  await correction.getByRole('button',{name:'关闭此对话框'}).click();await drawer.getByRole('button',{name:'关闭此对话框'}).click();await expect(drawer).not.toBeVisible()
  await page.setViewportSize({width:1000,height:900})
  await expect(checks).toBeVisible();await page.screenshot({path:join(tmpdir(),'pdm-work-hours-completeness-narrow.png'),fullPage:false})
  expect(await page.locator('vite-error-overlay').count()).toBe(0);expect(errors).toEqual([])
})

test('work hours assistant edits several department rosters together on one page',async({page})=>{
  await mockPlanCompanionPanels(page)
  await page.setViewportSize({width:1916,height:1080})
  const errors:string[]=[],saved:any[]=[]
  page.on('pageerror',error=>errors.push(error.message))
  page.on('console',message=>{if(message.type()==='error')errors.push(message.text())})
  const view:WorkHoursView={today:'2026-10-21',startDate:'2026-10-20',username:'engineer',displayName:'部门助理',scope:'授权部门',people:[],projects:[],sheets:[],canManage:false,canManageSettings:true}
  const settings:WorkHoursManagement={today:view.today,rowVersion:2,canAuthorize:false,scope:'授权部门',people:[],units:[],assistants:[],requirements:[],exceptions:[],grants:[],history:[],departmentGroups:[
    {id:'t2',name:'T2事业部',participating:true,people:[{username:'a',displayName:'员工甲',department:'T2机械组',isActive:true,required:true,canEdit:true},{username:'b',displayName:'员工乙',department:'T2电气组',isActive:true,required:false,canEdit:true}]},
    {id:'t3',name:'T3事业部',participating:true,people:[{username:'c',displayName:'员工丙',department:'T3项目组',isActive:true,required:false,canEdit:true}]},
    {id:'production',name:'生产部',participating:true,people:[{username:'d',displayName:'员工丁',department:'装配组',isActive:true,required:false,canEdit:false}]}
  ]}
  await page.route('**/api/work-hours**',async route=>{
    const path=new URL(route.request().url()).pathname
    if(path.endsWith('/settings')&&route.request().method()==='PUT'){
      const input=route.request().postDataJSON();saved.push(input);settings.rowVersion++
      for(const group of settings.departmentGroups!)for(const person of group.people){const change=input.people.find((x:any)=>x.username===person.username);if(change)person.required=change.required}
      return route.fulfill({json:settings})
    }
    return route.fulfill({json:path.endsWith('/settings')?settings:view})
  })
  await page.goto('/')
  const login=page.getByLabel('登录PLM')
  await login.getByRole('textbox',{name:'账号'}).fill('engineer');await login.getByRole('textbox',{name:'密码'}).fill('correct-password');await login.getByRole('button',{name:'登录',exact:true}).click()
  await page.locator('.pdm-sidebar').getByRole('button',{name:'工时管理',exact:true}).click()
  const panel=page.getByRole('region',{name:'工时管理',exact:true})
  await panel.getByRole('button',{name:'工时设置',exact:true}).click()
  await expect(panel.getByRole('checkbox',{name:'T2事业部参与工时',exact:true})).toBeDisabled()
  await expect(panel.getByRole('checkbox',{name:'生产部员工丁需要填报',exact:true})).toBeDisabled()
  await panel.getByRole('checkbox',{name:'T2事业部员工乙需要填报',exact:true}).check()
  await panel.getByRole('checkbox',{name:'T3事业部员工丙需要填报',exact:true}).check()
  await panel.getByRole('button',{name:'T2事业部',exact:true}).click()
  await expect(panel.getByRole('checkbox',{name:'T3事业部员工丙需要填报',exact:true})).toBeVisible()
  await panel.getByRole('button',{name:'汇总工时',exact:true}).click()
  await expect(panel.getByRole('button',{name:'工时设置',exact:true})).toHaveClass(/active/)
  await panel.getByRole('button',{name:'T2事业部',exact:true}).click()
  await panel.getByRole('button',{name:'保存设置',exact:true}).click()
  await expect.poll(()=>saved.length).toBe(1)
  expect(saved[0]).toEqual({companyId:'org-ks',rowVersion:2,departments:[],people:[{username:'b',required:true},{username:'c',required:true}]})
  await expect(panel.getByRole('button',{name:'保存设置',exact:true})).toBeDisabled()
  await page.screenshot({path:join(tmpdir(),'pdm-work-hours-groups-assistant.png'),fullPage:false})
  await page.setViewportSize({width:1000,height:900})
  await expect(panel.getByRole('checkbox',{name:'T2事业部员工乙需要填报',exact:true})).toBeVisible()
  await expect(panel.getByRole('checkbox',{name:'T3事业部员工丙需要填报',exact:true})).toBeVisible()
  await page.screenshot({path:join(tmpdir(),'pdm-work-hours-groups-narrow.png'),fullPage:false})
  expect(errors).toEqual([]);await expect(page.locator('vite-error-overlay')).toHaveCount(0)
})

test('refresh restores navigation, internal tabs and native and Element Plus column widths', async ({ page }) => {
  test.setTimeout(90_000)
  await mockPlanCompanionPanels(page)
  await page.setViewportSize({ width: 1916, height: 1080 })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  const hours: WorkHoursView = { today: '2026-10-20', startDate: '2026-10-20', username: 'admin', displayName: '系统管理员', scope: '当前公司', people: [{ username: 'admin', displayName: '系统管理员', department: '设计部', isActive: true }], projects: [], sheets: [] }
  await page.route('**/api/work-hours**', route => route.fulfill({ json: hours }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  const sidebar = page.locator('.pdm-sidebar')
  async function resize(header: ReturnType<Page['locator']>, delta = 45) {
    await header.scrollIntoViewIfNeeded()
    const before = (await header.boundingBox())!
    await page.mouse.move(before.x + before.width - 2, before.y + before.height / 2)
    await page.mouse.down()
    await page.mouse.move(before.x + before.width - 2 + delta, before.y + before.height / 2, { steps: 5 })
    await page.mouse.up()
    await expect.poll(async () => (await header.boundingBox())!.width - before.width).toBeGreaterThan(20)
    return (await header.boundingBox())!.width
  }
  async function sameWidth(header: ReturnType<Page['locator']>, width: number) {
    await expect.poll(async () => Math.abs((await header.boundingBox())!.width - width)).toBeLessThan(2)
  }
  await sidebar.getByRole('button', { name: '项目总览', exact: true }).click()
  await page.locator('.pdm-navigation-tabs').getByRole('tab', { name: '项目列表', exact: true }).click()
  const projectHeader = page.locator('[data-navigation-page="projects"] .pdm-project-table thead th').nth(1)
  const projectWidth = await resize(projectHeader)
  await page.reload()
  await expect(page.locator('[data-navigation-page="projects"]')).toBeVisible()
  await sameWidth(projectHeader, projectWidth)
  await page.screenshot({ path: join(tmpdir(),'pdm-refresh-project-columns.png'), fullPage: false })
  await sidebar.getByRole('button', { name: '工时管理', exact: true }).click()
  const panel = page.getByRole('region', { name: '工时管理', exact: true })
  await panel.getByRole('button', { name: '汇总工时', exact: true }).click()
  await panel.getByRole('button', { name: '填报明细', exact: true }).click()
  const hoursHeader = panel.locator('.wh-report-table thead th').nth(2)
  const hoursWidth = await resize(hoursHeader)
  await page.reload()
  await expect(panel.getByRole('button', { name: '汇总工时', exact: true })).toHaveClass(/active/)
  await expect(panel.getByRole('button', { name: '填报明细', exact: true })).toHaveClass(/active/)
  await sameWidth(hoursHeader, hoursWidth)
  await page.screenshot({ path: join(tmpdir(),'pdm-refresh-hours-columns.png'), fullPage: false })
  await sidebar.getByRole('button', { name: '料品管理', exact: true }).click()
  const materialHeader = page.locator('[data-navigation-page="materials"] .material-table .el-table__header th').nth(2)
  const materialWidth = await resize(materialHeader, 60)
  await page.reload()
  await expect(page.locator('[data-navigation-page="materials"]')).toBeVisible()
  await sameWidth(materialHeader, materialWidth)
  await page.screenshot({ path: join(tmpdir(),'pdm-refresh-material-columns.png'), fullPage: false })
  await sidebar.getByRole('button', { name: '项目中心', exact: true }).click()
  await page.locator('.pdm-project-tabs').getByRole('button', { name: 'BOM', exact: true }).click()
  await page.reload()
  await expect(page.locator('.pdm-project-tabs').getByRole('button', { name: 'BOM', exact: true })).toHaveClass(/is-active/)
  expect(await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('upton-pdm-column-widths:v1:')).length)).toBeGreaterThanOrEqual(3)
  expect(errors).toEqual([])
})

test('work hours calendar, weekly entry and reports preserve data and export on LAN HTTP', async ({ page }) => {
  const longProjectName = '上料单元及自动化装配检测设备现场安装调试项目名称'
  await mockPlanCompanionPanels(page)
  await page.clock.setFixedTime(new Date('2026-10-20T02:00:00Z'))
  await page.setViewportSize({ width: 1916, height: 1080 })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  const value: WorkHoursView = { today: '2026-10-20', startDate: '2026-10-12', username: 'admin', displayName: '系统管理员', scope: '当前公司',
    people: [{ username: 'admin', displayName: '系统管理员', department: '设计部', isActive: true }],
    projects: [{ id: 'hours-root', code: 'P700012', name: '自动化装配设备', rootProjectId: 'hours-root', rootProjectCode: 'P700012', rootProjectName: '自动化装配设备' }, { id: 'hours-child', code: 'P700012-1', name: longProjectName, rootProjectId: 'hours-root', rootProjectCode: 'P700012', rootProjectName: '自动化装配设备' }],
    sheets: [{ companyId: 'org-ks', username: 'admin', month: '2026-09', rowVersion: 0, history: [], entries: [] }, { companyId: 'org-ks', username: 'admin', month: '2026-10', rowVersion: 1, history: [], entries: [{ id: 'hours-existing', date: '2026-10-19', location: '厂内', hours: 8, projectId: 'hours-child', projectCode: 'P700012-1', projectName: '上料单元', rootProjectId: 'hours-root', rootProjectCode: 'P700012', rootProjectName: '自动化装配设备' }] }, { companyId: 'org-ks', username: 'admin', month: '2026-11', rowVersion: 0, history: [], entries: [] }] }
  await page.route('**/api/work-hours**', async route => {
    if (route.request().method() === 'PUT') {
      const input = route.request().postDataJSON() as SaveWorkHours
      for (const sheet of value.sheets) {
        const after = input.entries.filter(x => x.date.slice(0,7) === sheet.month).map((entry,index) => {
          const project = value.projects.find(x => x.id === entry.projectId)!
          return { ...entry, id: `hours-${sheet.month}-${index}`, projectCode: project.code, projectName: project.name, rootProjectId: project.rootProjectId, rootProjectCode: project.rootProjectCode, rootProjectName: project.rootProjectName }
        })
        if (input.versions.some(x => x.month === sheet.month)) { sheet.entries = sheet.entries.filter(x => x.date < input.from || x.date > input.to).concat(after); sheet.rowVersion++ }
      }
    }
    return route.fulfill({ json: value })
  })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.locator('.pdm-sidebar').getByRole('button', { name: '工时管理', exact: true }).click()
  const panel = page.getByRole('region', { name: '工时管理', exact: true })
  await expect(panel.locator('.wh-calendar')).toBeVisible()
  await expect(panel.getByRole('button', { name: '2026-10-17 0小时', exact: true })).not.toHaveClass(/\brest\b/)
  const sunday = panel.getByRole('button', { name: '2026-10-18 0小时', exact: true })
  await expect(sunday).toHaveClass(/\brest\b/)
  await expect(sunday).not.toContainText('休息日')
  await expect(panel.locator('.wh-calendar')).not.toContainText('休息日')
  await panel.getByRole('combobox', { name: '填报项目', exact: true }).click()
  await page.getByRole('option', { name: `P700012-1 · ${longProjectName}`, exact: true }).click()
  await expect(panel.getByLabel('自动带出项目名称')).toHaveValue(longProjectName)
  await panel.getByLabel('工时数', { exact: true }).fill('4.5')
  await panel.getByRole('button', { name: '保存并继续', exact: true }).click()
  await expect(panel.locator('.wh-entry')).toContainText('4.5 小时')
  const savedDay = panel.getByRole('button', { name: '2026-10-20 4.5小时', exact: true })
  await expect(savedDay.locator('.wh-day-project-code')).toHaveText('P700012-1')
  await expect(savedDay.locator('.wh-day-project-name')).toHaveText(longProjectName)
  await expect(savedDay.locator('.wh-day-record-hours')).toHaveText('4.5小时')
  await expect(savedDay.locator('.wh-day-status')).toHaveCount(0)
  expect(await savedDay.locator('.wh-day-project-name').evaluate(element => getComputedStyle(element).textOverflow)).toBe('ellipsis')
  expect(await savedDay.locator('.wh-day-project-name').evaluate(element => element.scrollWidth > element.clientWidth)).toBe(true)
  await expect(savedDay).not.toContainText('条记录')
  await page.screenshot({ path: join(tmpdir(), 'pdm-work-hours-calendar.png'), fullPage: false })
  await page.setViewportSize({ width: 800, height: 900 })
  await expect(savedDay.locator('.wh-day-record')).toBeVisible()
  await page.screenshot({ path: join(tmpdir(), 'pdm-work-hours-records-narrow.png'), fullPage: false })
  await page.setViewportSize({ width: 1916, height: 1080 })
  await panel.getByRole('button', { name: '周表', exact: true }).click()
  await panel.getByLabel('2026-10-20工时', { exact: true }).fill('6')
  await panel.getByRole('button', { name: '保存本周', exact: true }).click()
  await expect(panel.locator('.wh-week-table tfoot')).toContainText('14')
  await page.screenshot({ path: join(tmpdir(), 'pdm-work-hours-week.png'), fullPage: false })
  await panel.getByRole('button', { name: '汇总工时', exact: true }).click()
  await expect(panel.getByLabel('汇总结束日期')).toHaveValue('2026-10-19')
  await panel.getByLabel('汇总结束日期').fill('2026-10-20')
  await panel.getByRole('button', { name: '查询', exact: true }).click()
  await expect(panel.locator('.wh-report-summary')).toContainText('14 小时')
  await panel.getByRole('button', { name: '按项目', exact: true }).click()
  await expect(panel.locator('.wh-report-table tbody')).toContainText('P700012')
  await expect(panel.locator('.wh-report-table tbody')).toContainText('14小时')
  const download = page.waitForEvent('download')
  await panel.getByRole('button', { name: '导出Excel', exact: true }).click()
  expect((await download).suggestedFilename()).toMatch(/工时汇总.*\.xlsx$/)
  await panel.getByLabel('汇总所在地', { exact: true }).selectOption('厂外')
  await expect(panel.locator('.wh-report-summary')).toContainText('0 小时')
  await panel.getByLabel('汇总所在地', { exact: true }).selectOption('')
  await page.screenshot({ path: join(tmpdir(), 'pdm-work-hours-report.png'), fullPage: false })
  await page.setViewportSize({ width: 800, height: 900 })
  await expect(panel.locator('.wh-report-filters')).toBeVisible()
  await page.screenshot({ path: join(tmpdir(), 'pdm-work-hours-narrow.png'), fullPage: false })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true)
  expect(errors).toEqual([])
})

test('standard library shows nonempty asset icons and previews attachments before downloading', async ({ page }) => {
  await mockPlanCompanionPanels(page)
  const errors: string[] = [], downloads: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  page.on('download', download => downloads.push(download.suggestedFilename()))
  const material = { id: 'asset-material', materialCode: '01021000205', name: '真空发生器', specification: 'EZH10DLA-06-06-08N', brand: '亿太诺', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001', model3DAttachmentCount: 2, documentAttachmentCount: 3, referenceCount: 0 }
  await page.route('**/api/material-categories?*', route => route.fulfill({ json: [] }))
  await page.route('**/api/standard-library/categories*', route => route.fulfill({ json: [] }))
  await page.route('**/api/standard-library/materials?*', route => route.fulfill({ json: { items: [{ material, categories: [], coverImage: null }, { material: { ...material, id: 'empty-material', materialCode: '01021000206', name: '暂无附件物料', model3DAttachmentCount: 0, documentAttachmentCount: 0 }, categories: [], coverImage: null }], total: 2, page: 1, pageSize: 50 } }))
  const attachments = [{ id: 'manual', materialId: material.id, kind: 'Document', originalFileName: '产品说明.pdf', uploadedBy: '工程师' }, { id: 'photo', materialId: material.id, kind: 'Document', originalFileName: '产品外观.png', uploadedBy: '工程师' }, { id: 'word', materialId: material.id, kind: 'Document', originalFileName: '说明.docx', uploadedBy: '工程师' }]
  await page.route('**/api/materials/asset-material/attachments?*', route => route.fulfill({ json: attachments }))
  const pdfStream = 'BT /F1 20 Tf 50 250 Td (Product Manual / Preview) Tj ET\n50 80 400 100 re S\n'
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>', '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 350] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>', `<< /Length ${Buffer.byteLength(pdfStream)} >>\nstream\n${pdfStream}endstream`, '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>']
  let pdf = '%PDF-1.4\n'; const offsets = [0]
  objects.forEach((object, index) => { offsets.push(Buffer.byteLength(pdf)); pdf += `${index + 1} 0 obj\n${object}\nendobj\n` })
  const xref = Buffer.byteLength(pdf)
  pdf += `xref\n0 6\n0000000000 65535 f \n${offsets.slice(1).map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('')}trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF`
  await page.route('**/api/materials/asset-material/attachments/*/file', route => route.request().url().includes('/manual/') ? route.fulfill({ contentType: 'application/pdf', body: Buffer.from(pdf) }) : route.fulfill({ contentType: 'image/png', body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a1ioAAAAASUVORK5CYII=', 'base64') }))
  await page.goto('/', { waitUntil: 'domcontentloaded' }); await expect(page).toHaveTitle(/PLM|UPLM|PDM/)
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin'); await login.getByRole('textbox', { name: '密码' }).fill('correct-password'); await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.locator('.pdm-sidebar').getByRole('button', { name: '标准物料', exact: true }).click()
  const library = page.getByRole('region', { name: '标准库', exact: true }), rows = library.locator('.material-list-table .el-table__body tbody tr')
  await expect(rows).toHaveCount(2)
  await expect(rows.first().getByRole('button', { name: '01021000205 · 3D模型版本', exact: true })).toHaveText('未启用')
  await expect(rows.last().getByRole('button', { name: '01021000206 · 2D图纸版本', exact: true })).toHaveText('维护')
  await expect(rows.last().getByRole('button', { name: '01021000206 · 3D模型版本', exact: true })).toHaveText('维护')
  await expect(rows.last().getByRole('button', { name: /份资料/ })).toHaveCount(0)
  const screenshotRoot = process.env.PDM_STANDARD_LIBRARY_SCREENSHOTS || tmpdir()
  await page.screenshot({ path: join(screenshotRoot, 'standard-library-asset-icons.png'), fullPage: false })
  await library.getByRole('button', { name: '01021000205 · 3 份资料', exact: true }).click()
  const list = page.getByRole('dialog', { name: '01021000205 · 资料', exact: true }), pdfRow = list.getByRole('row').filter({ hasText: '产品说明.pdf' })
  await expect(list.getByRole('row').filter({ hasText: '说明.docx' }).getByRole('button', { name: '预览', exact: true })).toBeDisabled()
  await pdfRow.getByRole('button', { name: '预览', exact: true }).click()
  const preview = page.getByRole('dialog', { name: '产品说明.pdf', exact: true })
  await expect(preview.locator('canvas')).toBeVisible(); expect(downloads).toEqual([])
  await page.screenshot({ path: join(screenshotRoot, 'standard-library-attachment-pdf.png'), fullPage: false })
  await page.setViewportSize({ width: 1000, height: 900 }); await expect(preview.locator('canvas')).toBeVisible()
  const box = (await preview.boundingBox())!; expect(box.x + box.width).toBeLessThanOrEqual(1001)
  await preview.getByRole('button', { name: '关闭此对话框', exact: true }).click(); await expect(preview).toBeHidden()
  await list.getByRole('row').filter({ hasText: '产品外观.png' }).getByRole('button', { name: '预览', exact: true }).click()
  const imagePreview = page.getByRole('dialog', { name: '产品外观.png', exact: true }); await expect(imagePreview.getByRole('img', { name: '产品外观.png', exact: true })).toBeVisible()
  await expect.poll(() => imagePreview.getByRole('img', { name: '产品外观.png', exact: true }).evaluate(image => (image as HTMLImageElement).naturalWidth)).toBe(1)
  await imagePreview.getByRole('button', { name: '关闭此对话框', exact: true }).click()
  const download = page.waitForEvent('download'); await pdfRow.getByRole('button', { name: '下载', exact: true }).click(); expect((await download).suggestedFilename()).toBe('产品说明.pdf')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0); expect(errors).toEqual([])
})

test('standard structure lifecycle preserves whole snapshots and prevents deleting referenced structures', async ({ page }) => {
  await mockPlanCompanionPanels(page)
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message)); page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.route('**/api/standard-library/**', route => route.fulfill({ json: route.request().url().includes('/materials') ? { items: [], total: 0, page: 1, pageSize: 50 } : [] }))
  await page.route('**/api/browser-archives/capabilities*', route => route.fulfill({ json: { enabled: false, canArchive: false } }))
  const root = { id: 'root', drawingNumber: 'D-01', name: '历史总装', fileName: 'main.SLDASM', kind: 'Assembly', fileKey: 'native', previewFileKey: null, embedded: false, quantity: 1, children: [{ id: 'inner', drawingNumber: 'D-02', name: '嵌入零件', fileName: 'inner.SLDPRT', kind: 'Part', fileKey: null, previewFileKey: null, embedded: true, quantity: 1, children: [] }] }
  const revision = { id: 'rev-one', version: 1, archivedAt: '2026-10-20T01:00:00Z', archivedBy: 'std', root, files: [], bom: [{ id: 'line', kind: 'Standard', drawingNumber: 'P-01', name: '气缸', quantity: 1, unit: '个', revision: 'A' }] }
  const catalog = { companyId: 'org-ks', rowVersion: 1, storageRoot: 'D:/Standard', categories: [{ id: 'category', name: '批量分类', parentId: null, sortOrder: 1, isActive: true }], structures: [
    { id: 'unused', code: 'UPST-1-0001', name: '未引用总装', categoryId: 'category', isActive: true, deletedAt: null as string | null, revisions: [revision, { ...revision, id: 'rev-two', version: 2 }] },
    { id: 'used', code: 'UPST-1-0002', name: '已引用总装', categoryId: 'category', isActive: true, deletedAt: null as string | null, revisions: [{ ...revision, id: 'rev-used' }] },
  ] }
  const snapshots = JSON.stringify(catalog.structures.map(s => s.revisions)), actions: string[] = []
  await page.route('**/api/standard-structures**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/references')) return route.fulfill({ json: path.includes('/used/') ? [{ projectId, projectCode: 'P700016', revisionId: 'rev-used', version: 1, kind: 'Standard', quantity: 2, actor: '工程师', referencedAt: '2026-10-21T01:00:00Z', pending: false }] : [] })
    if (path.endsWith('/state')) {
      const input = route.request().postDataJSON(); expect(input.expectedRowVersion).toBe(catalog.rowVersion)
      const item = catalog.structures.find(s => path.includes(`/${s.id}/`))!; actions.push(input.action)
      if (input.action === 'Disable') item.isActive = false
      if (input.action === 'Enable') item.isActive = true
      if (input.action === 'Delete') item.deletedAt = '2026-10-23T09:00:00Z'
      if (input.action === 'Restore') item.deletedAt = null
      catalog.rowVersion++; return route.fulfill({ json: catalog })
    }
    if (route.request().method() === 'PUT') { catalog.categories[0]!.isActive = route.request().postDataJSON().isActive; catalog.rowVersion++; return route.fulfill({ json: catalog }) }
    return route.fulfill({ json: { catalog, canManage: true, canArchive: true, canConfigureStorage: true } })
  })
  await page.goto('/', { waitUntil: 'domcontentloaded' }); await expect(page).toHaveTitle(/PLM|UPLM|PDM/)
  const login = page.getByLabel('登录PLM'); await login.getByRole('textbox', { name: '账号' }).fill('admin'); await login.getByRole('textbox', { name: '密码' }).fill('correct-password'); await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.locator('.pdm-sidebar').getByRole('button', { name: '标准物料', exact: true }).click(); await page.locator('.pdm-navigation-tabs').getByRole('tab', { name: '标准结构', exact: true }).click()
  const workspace = page.getByRole('region', { name: '标准结构', exact: true }), filter = workspace.getByLabel('标准结构状态', { exact: true })
  await expect(workspace.locator('.standard-structure__properties')).toContainText('UPST-1-0001-V02')
  for (const [button, confirmation, state] of [['停用', '确认停用', 'inactive'], ['删除', '确认移入回收站', 'deleted'], ['恢复', '确认恢复', 'inactive'], ['启用', '确认启用', 'active']]) {
    await workspace.getByRole('button', { name: button, exact: true }).click(); await page.getByRole('button', { name: confirmation, exact: true }).click()
    await expect(filter).toHaveValue(state!)
    await expect(workspace.locator('.standard-structure__properties')).toContainText('UPST-1-0001-V02')
    await expect(workspace.getByLabel('标准结构版本', { exact: true })).toHaveValue('rev-two')
    expect(JSON.stringify(catalog.structures.map(s => s.revisions))).toBe(snapshots)
  }
  expect(actions).toEqual(['Disable', 'Delete', 'Restore', 'Enable'])
  await workspace.locator('.standard-structure__drawing-tree').getByText('UPST-1-0002-V01', { exact: true }).click()
  await workspace.getByRole('button', { name: '删除', exact: true }).click()
  const usage = page.getByRole('dialog', { name: 'UPST-1-0002 · 项目引用', exact: true }); await expect(usage).toContainText('P700016'); expect(actions).toHaveLength(4)
  await usage.getByRole('button', { name: '关闭此对话框', exact: true }).click()
  await workspace.locator('.standard-structure__categories').getByText('批量分类', { exact: true }).click()
  await workspace.getByRole('button', { name: '停用分类', exact: true }).click(); await page.getByRole('button', { name: '确认停用', exact: true }).click(); await expect(filter).toHaveValue('inactive')
  await workspace.getByRole('navigation', { name: '标准结构功能' }).getByRole('button', { name: 'BOM', exact: true }).click(); await expect(workspace.getByRole('button', { name: '引用到项目', exact: true })).toBeDisabled()
  const screenshotRoot = process.env.PDM_LIFECYCLE_SCREENSHOTS || tmpdir()
  await page.screenshot({ path: join(screenshotRoot, 'standard-structure-lifecycle.png'), fullPage: false })
  await page.setViewportSize({ width: 1000, height: 900 }); await expect(workspace).toBeVisible(); expect(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 2)).toBe(false)
  await workspace.getByRole('button', { name: '启用分类', exact: true }).click(); await page.getByRole('button', { name: '确认启用', exact: true }).click(); await expect(filter).toHaveValue('active')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0); expect(errors).toEqual([])
})

test('standard structure uses independent categories, readonly previews and pinned project BOM references', async ({ page }) => {
  test.setTimeout(90_000)
  await mockPlanCompanionPanels(page)
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.setViewportSize({ width: 1920, height: 1080 })
  await page.route('**/client-bootstrap.json*', route => route.fulfill({ json: { ConfigurationVersion: '2026.10.20.standard-structure-qa', Desktop: { Version: 'QA' }, SolidWorksAddin: { Version: 'QA' } } }))
  await page.route('**/api/standard-library/**', route => route.fulfill({ json: route.request().url().includes('/materials') ? { items: [], total: 0, page: 1, pageSize: 50 } : [] }))
  await page.route('**/api/browser-archives/capabilities*', route => route.fulfill({ json: { enabled: false, canArchive: false } }))
  await page.route('**/api/workbench**', route => route.fulfill({ json: { projects: [], notes: [], risks: [], scope: 'company', companyName: '测试公司' } }))
  const unclassified = { id: '55505354-0000-0000-0000-000000000002', name: '未分类', parentId: null, sortOrder: 0, isActive: true }
  const category = { id: 'cat-lift', name: 'UPST-1-0001-V01', parentId: unclassified.id, sortOrder: 1, isActive: true }
  const part = { id: 'part', drawingNumber: 'BZ-SJ-001-01', name: '升降底板', fileName: 'BZ-SJ-001-01.SLDPRT', kind: 'Part', fileKey: 'part', previewFileKey: null, embedded: false, quantity: 2, children: [] }
  const drawing = { ...part, id: 'drawing', drawingNumber: 'BZ-SJ-001工程图', fileName: 'BZ-SJ-001.SLDDRW', kind: 'Drawing', previewFileKey: 'drawing-pdf' }
  const root = { ...part, id: 'root', drawingNumber: 'BZ-SJ-001', name: '气缸升降组件', fileName: 'BZ-SJ-001.SLDASM', kind: 'Assembly', children: [part, { ...part, id: 'embedded', drawingNumber: '嵌入导向组件', name: '嵌入导向组件', fileKey: null, embedded: true }, drawing] }
  const catalog = { companyId: 'org-ks', rowVersion: 1, storageRoot: 'D:/PLM/StandardStructures',
    categories: [unclassified, { id: 'cat-handling', name: '搬运机构', parentId: null, sortOrder: 1, isActive: true }, category, { id: 'cat-position', name: '定位夹紧', parentId: null, sortOrder: 2, isActive: true }],
    structures: [{ id: 'structure', categoryId: category.id, code: 'UPST-1-0001', drawingType: 1, name: '气缸升降组件', revisions: [{ id: 'revision', version: 1, archivedAt: '2026-10-20T01:00:00Z', archivedBy: 'std', root,
      files: [{ key: 'drawing-pdf', fileName: 'BZ-SJ-001.pdf', length: 100, sha256: 'a' }],
      bom: [{ id: 'std', kind: 'Standard', drawingNumber: '010200001', name: '气缸', quantity: 1, unit: '个', revision: 'W1', brand: '示例品牌' }, { id: 'nonstd', kind: 'NonStandard', drawingNumber: 'BZ-SJ-001-01', name: '升降底板', quantity: 2, unit: '件', revision: 'W1', material: 'Q235B' }] }] }] }
  let referenced: Record<string, unknown> | undefined
  let classified: Record<string, unknown> | undefined
  const pdfStream = 'BT /F1 20 Tf 50 300 Td (BZ-SJ-001 / QA Preview) Tj ET\n50 80 400 180 re S\n70 100 m 430 240 l S\n'
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 350] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>',
    `<< /Length ${Buffer.byteLength(pdfStream)} >>\nstream\n${pdfStream}endstream`, '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>']
  let pdf = '%PDF-1.4\n'
  const offsets = [0]
  objects.forEach((object, index) => { offsets.push(Buffer.byteLength(pdf)); pdf += `${index + 1} 0 obj\n${object}\nendobj\n` })
  const xref = Buffer.byteLength(pdf)
  pdf += `xref\n0 6\n0000000000 65535 f \n${offsets.slice(1).map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('')}trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF`
  await page.route('**/api/standard-structures**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path.includes('/preview/')) return route.fulfill({ contentType: 'application/pdf', body: Buffer.from(pdf) })
    if (path.endsWith('/references')) { referenced = route.request().postDataJSON(); return route.fulfill({ json: { addedCount: 1, revisionId: 'revision', version: 1 } }) }
    if (path.endsWith('/classifications')) {
      classified = route.request().postDataJSON()
      catalog.structures[0]!.categoryId = classified!.categoryId as string
      catalog.rowVersion++
      return route.fulfill({ json: catalog })
    }
    return route.fulfill({ json: { catalog, canManage: true, canArchive: true, canConfigureStorage: true } })
  })
  await page.goto(process.env.PDM_STANDARD_STRUCTURE_LIVE_URL || '/', { waitUntil: 'domcontentloaded' })
  await expect(page).toHaveTitle(/PLM|UPLM|PDM/)
  expect(new URL(page.url()).protocol).toBe('http:')
  const login = page.getByLabel('登录PLM')
  await expect(login).toBeVisible({ timeout: 60_000 })
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.locator('.pdm-sidebar').getByRole('button', { name: '标准物料', exact: true }).click()
  await page.locator('.pdm-navigation-tabs').getByRole('tab', { name: '标准结构', exact: true }).click()
  const workspace = page.getByRole('region', { name: '标准结构', exact: true })
  await expect(workspace).toBeVisible()
  const tabs = await workspace.locator('.standard-structure__tabs').boundingBox()
  const readonly = await workspace.locator('.standard-structure__readonly').boundingBox()
  expect(readonly!.x).toBeGreaterThan(tabs!.x + tabs!.width * .7)
  await expect(workspace.getByRole('navigation', { name: '标准结构功能' }).getByRole('button', { name: '图档', exact: true })).toBeVisible()
  await expect(workspace).not.toContainText('审核')
  await expect(workspace).not.toContainText('批注')
  const categories = workspace.locator('.standard-structure__categories')
  await categories.getByText('未分类', { exact: true }).click()
  await expect(workspace.locator('.standard-structure__drawing-tree')).toContainText('UPST-1-0001-V01')
  const unclassifiedNode = categories.locator(`[data-key="${unclassified.id}"]`)
  if (await unclassifiedNode.getAttribute('aria-expanded') === 'false') await unclassifiedNode.locator(':scope > .el-tree-node__content .el-tree-node__expand-icon').click()
  await categories.getByText('UPST-1-0001-V01', { exact: true }).click()
  await expect(workspace.locator('.standard-structure__breadcrumb')).toContainText('未分类 › UPST-1-0001-V01')
  const screenshots = process.env.PDM_STANDARD_STRUCTURE_SCREENSHOTS || tmpdir()
  await page.screenshot({ path: join(screenshots, 'standard-structure-drawings-desktop.png'), fullPage: false })
  await workspace.locator('.standard-structure__drawing-tree').getByText('BZ-SJ-001工程图', { exact: true }).click()
  await workspace.getByRole('button', { name: '加载预览', exact: true }).last().click()
  await expect(workspace.locator('.pdf-drawing-viewer canvas')).toBeVisible()
  await expect(workspace.getByRole('status')).toHaveCount(0)
  await page.screenshot({ path: join(screenshots, 'standard-structure-preview-desktop.png'), fullPage: false })
  await workspace.getByRole('navigation', { name: '标准结构功能' }).getByRole('button', { name: 'BOM', exact: true }).click()
  await workspace.getByRole('button', { name: '非标件BOM', exact: true }).click()
  await expect(workspace.locator('.standard-structure__bom')).toContainText('升降底板')
  await workspace.getByRole('button', { name: '引用到项目', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '引用标准结构BOM到项目' })
  await dialog.getByRole('combobox').click()
  await page.locator('.el-select-dropdown:visible').getByRole('option').first().click()
  await dialog.getByRole('spinbutton').fill('3')
  await dialog.getByRole('button', { name: '确认引用', exact: true }).click()
  await expect(dialog).toBeHidden()
  expect(referenced).toMatchObject({ revisionId: 'revision', kind: 'NonStandard', quantity: 3 })
  expect(referenced?.requestId).toMatch(/^[0-9a-f-]{36}$/)
  await page.screenshot({ path: join(screenshots, 'standard-structure-bom-desktop.png'), fullPage: false })
  await workspace.getByRole('navigation', { name: '标准结构功能' }).getByRole('button', { name: '图档', exact: true }).click()
  await expect(workspace.locator('.pdf-drawing-viewer canvas')).toBeVisible()
  await expect(workspace.getByRole('status')).toHaveCount(0)
  await page.setViewportSize({ width: 1000, height: 1080 })
  await expect(workspace.locator('.standard-structure__preview')).toBeVisible()
  expect(await workspace.locator('.standard-structure__preview-toolbar > span').evaluate(element => element.getBoundingClientRect().height)).toBeLessThan(35)
  await page.screenshot({ path: join(screenshots, 'standard-structure-drawings-1000.png'), fullPage: false })
  await page.setViewportSize({ width: 1920, height: 1080 })
  await workspace.getByRole('button', { name: '调整分类', exact: true }).click()
  const moveDialog = page.getByRole('dialog', { name: '调整标准库分类' })
  await moveDialog.locator('.el-select').last().click()
  await page.locator('.el-select-dropdown:visible').getByText('定位夹紧', { exact: true }).click()
  await moveDialog.getByRole('button', { name: '确认调整', exact: true }).click()
  await expect(moveDialog).toBeHidden()
  expect(classified).toMatchObject({ structureIds: ['structure'], categoryId: 'cat-position', expectedRowVersion: 1 })
  await expect(workspace.locator('.standard-structure__breadcrumb')).toContainText('定位夹紧')
  await expect(workspace.locator('.standard-structure__properties')).toContainText('UPST-1-0001-V01')
  await page.setViewportSize({ width: 390, height: 844 })
  await expect(workspace.locator('.standard-structure__categories')).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 2)).toBe(false)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('special project application keeps null order date and complete submission approval history over HTTP', async ({ page }) => {
  await page.setViewportSize({ width: 1518, height: 855 })
  await page.route('**/api/workbench**', route => route.fulfill({ json: { projects: [], notes: [], risks: [], scope: 'company', companyName: '测试公司' } }))
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  let app: ProjectApplication | null = null
  const history: ProjectApplicationEvent[] = []
  const people = [{ username: 'sales', displayName: '销售甲' }, { username: 'sales2', displayName: '销售乙' }, { username: 'pre1', displayName: '售前甲' }, { username: 'pre2', displayName: '售前乙' }, { username: 'pre3', displayName: '售前丙' }]
  const customers = [{ id: 'customer-qa', code: 'C001', name: '立项验收客户', isActive: true, sourceSystem: 'u9c' }]
  const snapshot = (input: ProjectApplicationInput) => ({ input: structuredClone(input), salesName: people.find(x => x.username === input.salesUsername)!.displayName, presalesPeople: input.presalesUsernames.map(username => people.find(x => x.username === username)!) })
  await page.route('**/api/project-applications**', async route => {
    const path = new URL(route.request().url()).pathname
    const method = route.request().method()
    if (method === 'GET' && path === '/api/project-applications') return route.fulfill({ json: { applications: app ? [app] : [], canApply: true, canApprove: true, people, customers,
      numberingOptions: { organizations: [], projectTypes: [{ code: 'P', name: '标准项目', isActive: true }], equipmentTypes: [{ code: 2, name: '气密设备', isActive: true }] } } })
    if (method === 'GET') return route.fulfill({ json: { application: app, history, canEdit: app?.status === '已退回' || app?.status === '待审批', canWithdraw: app?.status === '待审批', canApprove: app?.status === '待审批' } })
    const input = route.request().postDataJSON()
    if (path.endsWith('/return')) {
      app = { ...app!, status: '已退回', rowVersion: app!.rowVersion + 1 }
      history.push({ id: `event-${history.length}`, action: '退回修改', revision: app.revision, actor: 'admin', actorName: '计划管理', at: app.updatedAt, data: structuredClone(app.data), comment: input.comment, approval: null, projectId: null, projectCode: null })
    } else if (path.endsWith('/approve')) {
      expect(input.expectedRowVersion).toBe(app!.rowVersion)
      expect(input.projectData.projectName).toBe('审批调整后的项目')
      expect(input.projectData.salesUsername).toBe('sales2')
      expect(input.projectData.presalesUsernames).toContain('pre3')
      app = { ...app!, status: '已批准', rowVersion: app!.rowVersion + 1, projectId: 'project-qa', projectCode: 'P700099' }
      history.push({ id: `event-${history.length}`, action: '批准并创建主项目', revision: app.revision, actor: 'admin', actorName: '计划管理', at: app.updatedAt, data: structuredClone(app.data), comment: input.comment, approval: input, approvedData: snapshot(input.projectData), projectId: app.projectId, projectCode: app.projectCode })
    } else {
      expect(input.data.orderDate).toBeNull()
      expect(input.data.isSpecial).toBe(true)
      expect(input.data.presalesUsernames).toEqual(['pre1', 'pre2'])
      const action = app?.status === '待审批' ? '修改并重新提交' : '提交申请'
      app = { id: 'app-qa', number: 'LX20261019-ACCEPTANCE', companyId: 'org-ks', applicant: 'admin', applicantName: '销售助理', status: '待审批', revision: (app?.revision ?? 0) + 1,
        data: snapshot(input.data), rowVersion: (app?.rowVersion ?? 0) + 1, createdAt: '2026-10-19T04:00:00Z', updatedAt: '2026-10-19T04:00:00Z', projectId: null, projectCode: null }
      history.push({ id: `event-${history.length}`, action, revision: app.revision, actor: 'admin', actorName: '销售助理', at: app.createdAt, data: structuredClone(app.data), comment: null, approval: null, projectId: null, projectCode: null })
    }
    return route.fulfill({ json: app })
  })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.locator('.pdm-sidebar').getByRole('button', { name: '项目总览', exact: true }).click()
  await page.getByRole('tab', { name: '立项申请', exact: true }).click()
  await page.getByRole('button', { name: '新建申请', exact: true }).click()
  let dialog = page.getByRole('dialog', { name: '新建立项申请', exact: true })
  await dialog.getByText('特殊立项（合同未签，提前启动）', { exact: true }).click()
  await dialog.getByRole('combobox', { name: '客户名称', exact: true }).click()
  await page.getByRole('option', { name: '立项验收客户（C001）', exact: true }).click()
  await dialog.getByRole('textbox', { name: '项目名称', exact: true }).fill('提前启动设备')
  await dialog.getByLabel('交货日期', { exact: true }).fill('2026-12-30')
  await dialog.getByRole('combobox', { name: '销售人员', exact: true }).click()
  await page.getByRole('option', { name: '销售甲', exact: true }).click()
  await dialog.getByText('可选择多位工程师', { exact: true }).click()
  const presalesListId = await dialog.getByRole('combobox', { name: '售前工程师', exact: true }).getAttribute('aria-controls')
  const presalesList = page.locator(`[id="${presalesListId}"]`)
  await presalesList.getByRole('option', { name: '售前甲', exact: true }).click()
  await presalesList.getByRole('option', { name: '售前乙', exact: true }).click()
  await dialog.getByRole('textbox', { name: '备注', exact: true }).click()
  await dialog.getByRole('textbox', { name: '备注', exact: true }).fill('合同待签，申请提前启动')
  await page.screenshot({ path: join(tmpdir(), 'pdm-application-special-form.png'), fullPage: false })
  await dialog.getByRole('button', { name: '提交申请', exact: true }).click()
  dialog = page.getByRole('dialog', { name: '立项申请详情', exact: true })
  await expect(dialog).toContainText('未填写')
  await expect(dialog).toContainText('售前甲、售前乙')
  await dialog.getByRole('button', { name: '修改申请', exact: true }).click()
  dialog = page.getByRole('dialog', { name: '修改立项申请', exact: true })
  await dialog.getByRole('textbox', { name: '项目名称', exact: true }).fill('申请人修改后的项目')
  await dialog.getByRole('button', { name: '保存修改并重新提交', exact: true }).click()
  dialog = page.getByRole('dialog', { name: '立项申请详情', exact: true })
  await expect(dialog).toContainText('第 2 次提交')
  await dialog.getByRole('textbox', { name: '退回原因', exact: true }).fill('请确认交货日期')
  await dialog.getByRole('button', { name: '退回修改', exact: true }).click()
  await expect(dialog).toContainText('已退回')
  await dialog.getByRole('button', { name: '修改申请', exact: true }).click()
  dialog = page.getByRole('dialog', { name: '修改立项申请', exact: true })
  await dialog.getByRole('textbox', { name: '项目名称', exact: true }).fill('确认交期后的设备')
  await dialog.getByRole('button', { name: '提交申请', exact: true }).click()
  dialog = page.getByRole('dialog', { name: '立项申请详情', exact: true })
  await expect(dialog).toContainText('第 3 次提交')
  await dialog.getByRole('button', { name: '引用申请并编辑建项资料', exact: true }).click()
  await dialog.getByRole('textbox', { name: '审批项目名称', exact: true }).fill('审批调整后的项目')
  await dialog.getByLabel('审批订单日期', { exact: true }).fill('2026-11-02')
  await dialog.getByLabel('审批交货日期', { exact: true }).fill('2027-01-20')
  await dialog.getByRole('combobox', { name: '审批销售人员', exact: true }).click()
  const salesListId = await dialog.getByRole('combobox', { name: '审批销售人员', exact: true }).getAttribute('aria-controls')
  await page.locator(`[id="${salesListId}"]`).getByRole('option', { name: '销售乙', exact: true }).click()
  await dialog.getByRole('combobox', { name: '审批售前工程师', exact: true }).fill('售前丙')
  const finalPresalesListId = await dialog.getByRole('combobox', { name: '审批售前工程师', exact: true }).getAttribute('aria-controls')
  await page.locator(`[id="${finalPresalesListId}"]`).getByRole('option', { name: '售前丙', exact: true }).click()
  await dialog.getByRole('textbox', { name: '审批备注', exact: true }).fill('审批调整人员和交期')
  await dialog.getByRole('textbox', { name: '审批意见', exact: true }).fill('同意提前立项')
  await page.screenshot({ path: join(tmpdir(), 'pdm-application-edit-approval.png'), fullPage: false })
  await dialog.getByRole('button', { name: '批准并创建主项目', exact: true }).click()
  await expect(dialog).toContainText('已批准')
  await expect(dialog).toContainText('P700099')
  await expect(dialog.getByLabel('最终建项资料', { exact: true })).toContainText('审批调整后的项目')
  await expect(dialog.getByLabel('最终建项资料', { exact: true })).toContainText('销售乙')
  await expect(dialog.getByLabel('最终建项资料', { exact: true })).toContainText('售前丙')
  await expect(dialog.locator('.application-history details')).toHaveCount(5)
  await dialog.locator('.application-history summary').first().click()
  await expect(dialog.locator('.application-history details').first()).toContainText('提前启动设备')
  await page.screenshot({ path: join(tmpdir(), 'pdm-application-approved-history.png'), fullPage: false })
  await dialog.getByRole('button', { name: '关闭', exact: true }).click()
  await expect(dialog).toBeHidden()
  const applicationCells = page.locator('.application-table th, .application-table td')
  expect(await applicationCells.evaluateAll(cells => [...new Set(cells.map(cell => getComputedStyle(cell).textAlign))])).toEqual(['center'])
  await page.screenshot({ path: join(tmpdir(), 'pdm-application-list-centered.png'), fullPage: false })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('archive states agree in structure and 2D list over HTTP', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  const documents = [
    { id: 'doc-root', projectId, drawingNumber: 'REAL-ASM-001', name: '真实总装配', fileName: 'REAL-ASM-001.SLDASM', kind: 0, revision: { display: 'W2' }, storedVersionCount: 2 },
    { id: 'doc-drawing', projectId, drawingNumber: 'REAL-ASM-001', name: '真实总装工程图', fileName: 'REAL-ASM-001.SLDDRW', kind: 2, revision: { display: 'W1' }, storedVersionCount: 0 },
  ]
  await page.route('**/api/projects/*/documents', route => route.fulfill({ json: documents }))
  await page.route('**/api/projects/*/folder-documents', route => route.fulfill({ json: documents }))
  await page.route('**/api/projects/*/reference-tree', route => route.fulfill({ json: {
    documentId: 'doc-root', nodeId: 'root', instancePath: 'root', fileName: 'REAL-ASM-001.SLDASM', displayName: '真实总装配', kind: 0, quantity: 1, children: [],
  } }))
  await page.goto('/')
  expect(new URL(page.url()).protocol).toBe('http:')
  await expect(page).toHaveTitle(/PLM|UPLM|PDM/)
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '图档', exact: true }).click()
  const tree = page.getByLabel('项目设计树')
  await expect(tree).toContainText('真实总装工程图')
  await expect(tree).toContainText('未存档')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  await page.screenshot({ path: join(tmpdir(), 'pdm-archive-state-structure.png'), fullPage: false })
  await page.getByRole('tab', { name: /^2D/ }).click()
  await expect(tree.locator('.pdm-drawing-list')).toContainText('未存档')
  await expect(tree.locator('.pdm-drawing-list')).not.toContainText('W1')
  await page.screenshot({ path: join(tmpdir(), 'pdm-archive-state-2d.png'), fullPage: false })
  expect(errors).toEqual([])
})

test('version restore retries reuse operation and baseline over HTTP', async ({ page }) => {
  const requests: Record<string, unknown>[] = []
  await page.route('**/api/documents/doc-root/versions', route => route.fulfill({ json: [...versions].reverse() }))
  await page.route('**/api/documents/doc-root/versions/*/restore', async route => {
    requests.push(route.request().postDataJSON())
    await route.fulfill({ status: requests.length === 1 ? 503 : 200, json: requests.length === 1 ? { message: '存档结果尚未确认，请重试核对。' } : {} })
  })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '图档', exact: true }).click()
  await page.locator('.pdm-tree-row').first().click({ button: 'right' })
  await page.getByRole('menuitem', { name: /查看\/对比历史版本/ }).click()
  const restore = page.getByRole('button', { name: '以左版为基线创建新工作版' })
  await restore.click()
  await expect(page.getByText('存档结果尚未确认，请重试核对。', { exact: true })).toBeVisible()
  await restore.click()
  await expect.poll(() => requests.length).toBe(2)
  expect(requests[0]).toEqual(requests[1])
  expect(requests[0]?.operationId).toMatch(/^[a-f0-9]{32}$/)
  expect(requests[0]?.expectedVersionId).toBe('version-w2')
  await expect(page.getByText('已从历史版本创建新的工作版本，当前文件未被覆盖', { exact: true })).toBeVisible()
})

test('client diagnostic logs filter and download with Chinese status', async ({ page }) => {
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message))
  let filtered = false
  let agentFiltered = false
  await page.route('**/api/client-logs?*', async route => {
    filtered = new URL(route.request().url()).searchParams.get('kind') === 'error'
    agentFiltered = new URL(route.request().url()).searchParams.get('component') === 'preview-agent'
    await route.fulfill({ json: { total: 1, pageSize: 50, runtimeDays: 30, errorDays: 90, items: [{ id: 'batch-1', deviceId: 'a'.repeat(32), machine: agentFiltered ? 'PC-07' : '设计部-PC01', username: 'engineer', component: agentFiltered ? 'preview-agent' : 'solidworks', kind: filtered ? 'error' : 'runtime', version: '2026.10.13', uiVersion: 'main-test.js', startedAt: '2026-10-13T08:00:00Z', endedAt: '2026-10-13T08:05:00Z', receivedAt: '2026-10-13T08:06:00Z', entryCount: 3, bytes: 1000 }] } })
  })
  await page.route('**/api/client-logs/download?*', route => route.fulfill({ status: 200, contentType: 'application/zip', body: Buffer.from('diagnostic download fixture') }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin'); await login.getByRole('textbox', { name: '密码' }).fill('correct-password'); await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '系统设置', exact: true }).click()
  await page.getByRole('button', { name: '客户端日志', exact: true }).click()
  const panel = page.getByRole('region', { name: '客户端日志' })
  await expect(panel).toContainText('设计部-PC01')
  await panel.getByLabel('日志类型', { exact: true }).selectOption('error')
  await panel.getByRole('button', { name: '查询日志' }).click()
  await expect.poll(() => filtered).toBe(true)
  await expect(panel.locator('tbody')).toContainText('错误日志')
  await panel.getByLabel('组件', { exact: true }).selectOption('preview-agent')
  await panel.getByRole('button', { name: '查询日志' }).click()
  await expect.poll(() => agentFiltered).toBe(true)
  await expect(panel.locator('tbody')).toContainText('PC-07')
  await expect(panel.locator('tbody')).toContainText('转图代理')
  const download = page.waitForEvent('download')
  await panel.getByRole('button', { name: '下载完整诊断包' }).click()
  expect((await download).suggestedFilename()).toContain('UPLM-客户端诊断')
  await expect(panel.getByRole('status')).toContainText('诊断包已下载')
  await page.screenshot({ path: join(tmpdir(), 'pdm-client-logs-desktop.png'), fullPage: false })
  await page.setViewportSize({ width: 780, height: 900 })
  await expect(panel.getByLabel('日志类型', { exact: true })).toBeVisible()
  await page.screenshot({ path: join(tmpdir(), 'pdm-client-logs-narrow.png'), fullPage: false })
  expect(errors).toEqual([])
})

test('DingTalk self-service application settings and shared project group binding', async ({ page }) => {
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message))
  const group = { id: 'shared-group', name: '设备项目共享群', projectCount: 1, registered: true, entryUrl: 'http://192.168.2.8:5173/?dingtalkOpl=1&companyId=org-ks&groupId=shared-group' }
  let saved = false, bound = false, entrySent = false, commandGenerated = false
  const settings = { notificationTemplate: '问题描述：{问题描述}\n负责人：{负责人}', defaultNotificationTemplate: '问题描述：{问题描述}\n负责人：{负责人}', notificationFields: ['问题描述','负责人','实施状态'], enabled: false, corpId: 'ding-test-corp', appKey: 'test-app-key', hasAppSecret: true, publicUrl: 'http://192.168.2.8:5173', companyId: 'org-ks', rowVersion: 2, users: [{ dingTalkUserId: 'ding-user', username: 'engineer' }], groups: [group], entryUrl: 'http://192.168.2.8:5173/?dingtalkOpl=1&companyId=org-ks' }
  await page.route('**/api/settings/dingtalk', async route => {
    if (route.request().method() === 'PUT') { expect(route.request().postDataJSON().appSecret).toBe(''); expect(route.request().postDataJSON().robotCode).toBe('ding-robot-test'); expect(route.request().postDataJSON().notificationTemplate).toBe('内容：{问题描述}'); saved = true }
    await route.fulfill({ json: settings })
  })
  await page.route('**/api/projects/*/dingtalk/entry', async route => {
    entrySent = true
    await route.fulfill({ json: { sent: true, message: '入口已发送' } })
  })
  const command = '绑定项目 PRJ-REAL-001 11223344AABBCCDD'
  await page.route('**/api/projects/*/dingtalk/binding-command', route => { commandGenerated = true; return route.fulfill({ json: { command, expiresAt: '2026-10-26T13:00:00+08:00' } }) })
  await page.route('**/api/projects/*/dingtalk', async route => {
    expect(route.request().method()).toBe('GET')
    await route.fulfill({ json: { rootProjectId: projectId, canManage: true, rowVersion: 2, groupId: bound ? group.id : null, groups: bound ? [group] : [], enabled: true, entryUrl: bound ? group.entryUrl : '' } })
  })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin'); await login.getByRole('textbox', { name: '密码' }).fill('correct-password'); await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '项目设置', exact: true }).click()
  const drawer = page.getByRole('dialog', { name: /项目设置/ })
  await drawer.getByRole('tab', { name: '钉钉群绑定', exact: true }).click()
  await expect(drawer.getByRole('combobox')).toHaveCount(0)
  await drawer.getByRole('button', { name: '生成并复制项目绑定码', exact: true }).click()
  await expect.poll(() => commandGenerated).toBe(true)
  await expect(drawer.getByLabel('项目绑定码')).toHaveValue(command)
  await expect(drawer.getByRole('button', { name: '保存群绑定' })).toHaveCount(0)
  bound = true // 模拟目标群收到专用指令后，服务端已完成绑定。
  await page.evaluate(() => window.dispatchEvent(new Event('focus')))
  await expect(drawer.getByLabel('快捷入口地址')).toHaveValue(group.entryUrl)
  const unlinkBinding = drawer.getByRole('button', { name: '解除群绑定' })
  await expect(unlinkBinding).toBeVisible()
  await expect(drawer.getByRole('button', { name: '重新检查范围' })).toHaveCount(0)
  expect((await unlinkBinding.boundingBox())!.y).toBeLessThan((await drawer.getByRole('heading', { name: '使用说明' }).boundingBox())!.y)
  await drawer.getByRole('button', { name: '重发提交入口' }).click()
  await expect.poll(() => entrySent).toBe(true)
  await expect(drawer.getByText('新增群机器人', { exact: true })).toHaveCount(0)
  await page.screenshot({ path: join(tmpdir(), 'dingtalk-shared-group-settings.png') })
  await drawer.getByRole('button', { name: '取消', exact: true }).click()
  await page.getByRole('button', { name: '系统设置', exact: true }).click()
  await page.getByRole('button', { name: '钉钉应用', exact: true }).click()
  const panel = page.getByLabel('钉钉应用设置')
  await expect(panel.getByLabel('统一应用首页地址')).toHaveValue(settings.entryUrl)
  await expect(panel.getByLabel('群聊酷应用编码', { exact: true })).toHaveCount(0)
  await expect(panel).toContainText('通知中显示负责人名称')
  await expect(panel).not.toContainText('qyapi_chat_read')
  await expect(panel.getByLabel('AppSecret', { exact: true })).toHaveValue('')
  await panel.getByLabel('企业机器人RobotCode', { exact: true }).fill('ding-robot-test')
  await expect(panel.locator('.ding-message-preview')).toContainText('问题描述：测试')
  await panel.getByLabel('群通知消息内容', { exact: true }).fill('内容：{问题描述}')
  await expect(panel.locator('.ding-message-preview')).toHaveText('内容：测试')
  await panel.getByRole('region', { name: '群通知消息模板' }).scrollIntoViewIfNeeded()
  await page.screenshot({ path: join(tmpdir(), 'dingtalk-template-desktop.png') })
  await page.setViewportSize({ width: 780, height: 900 })
  await panel.getByLabel('群通知消息内容', { exact: true }).scrollIntoViewIfNeeded()
  expect(await page.locator('body').evaluate(element => element.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: join(tmpdir(), 'dingtalk-template-narrow.png') })
  await page.setViewportSize({ width: 1280, height: 720 })
  await expect(panel.getByRole('button', { name: '新增账号绑定' })).toHaveCount(0)
  await expect(panel).toContainText('无需PLM账号')
  await panel.getByRole('button', { name: '保存配置', exact: true }).click(); await expect.poll(() => saved).toBe(true)
  await expect(panel).toContainText('配置已保存')
  await expect(panel.getByRole('button', { name: '编辑机器人' })).toHaveCount(0)
  await expect(panel).toContainText('Stream')
  await page.screenshot({ path: join(tmpdir(), 'dingtalk-application-settings.png') })
  expect(errors).toEqual([])
})

test('edit collaboration emergency takeover and policy switch work over LAN HTTP', async ({ page }, testInfo) => {
  await mockPlanCompanionPanels(page)
  await page.route('**/client-bootstrap.json*', route => route.fulfill({ json: { ConfigurationVersion: '2026.10.23.edit-collaboration-qa', Desktop: { Version: 'QA' }, SolidWorksAddin: { Version: 'QA' } } }))
  await page.route('**/api/standard-structures/storage', route => route.fulfill({ json: { storageRoot: 'D:/PDM/Standard', rowVersion: 1 } }))
  await page.route('**/api/browser-archives/capabilities', route => route.fulfill({ json: { enabled: false, canConfigure: false } }))
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  let takeover: Record<string, unknown> | undefined
  let settings = { vaultRoot: 'D:\\PDM\\Vault', releaseRoot: 'D:\\PDM\\Release', checkoutHeartbeatSeconds: 60, checkoutLeaseMinutes: 5, checkoutOfflineGraceMinutes: 30, checkoutReminderHours: 4, checkoutStrongReminderHours: 8, checkoutOverdueHours: 24, checkoutForceReleaseHours: 48, checkoutAutoReleaseEnabled: true, checkoutIdleReleaseMinutes: 30, checkoutReleaseCountdownSeconds: 60 }
  await page.route('**/api/system-settings', async route => {
    if (route.request().method() === 'PUT') settings = route.request().postDataJSON()
    await route.fulfill({ json: settings })
  })
  await page.route('**/api/edit-locks', route => route.fulfill({ json: takeover ? [] : [{
    documentId: 'edit-qa', projectId, projectCode: 'PRJ-REAL-001', projectName: '协作项目', drawingNumber: 'EDIT-001', documentName: '机架', fileName: 'EDIT-001.SLDPRT',
    checkedOutBy: 'designer', checkedOutAt: '2026-10-23T00:00:00Z', lastHeartbeatAt: '2026-10-23T00:01:00Z', leaseExpiresAt: '2026-10-23T00:06:00Z',
    connectionState: 'Active', attentionLevel: 'Normal', ownedByCurrentUser: false, canRequestRelease: true, canForceRelease: false, canEmergencyRelease: true, sessionId: 'original-session',
  }] }))
  await page.route('**/api/documents/edit-qa/emergency-release', async route => {
    takeover = route.request().postDataJSON(); await route.fulfill({ json: {} })
  })
  await page.goto('/')
  expect(page.url()).toMatch(/^http:\/\/(?:192\.168\.2\.8|127\.0\.0\.1):\d+\//)
  await expect(page).toHaveTitle('UPTON-PLM')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: /^消息/ }).click()
  await page.getByRole('button', { name: '紧急接管', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '紧急接管编辑权限' })
  await dialog.getByRole('button', { name: '确认释放' }).click()
  expect(takeover).toBeUndefined()
  await dialog.getByRole('textbox').fill('项目负责人确认交接')
  await page.screenshot({ path: testInfo.outputPath('emergency-takeover.png'), fullPage: false })
  await dialog.getByRole('button', { name: '确认释放' }).click()
  await expect(dialog).not.toBeVisible()
  expect(takeover).toEqual({ sessionId: 'original-session', checkedOutAt: '2026-10-23T00:00:00Z', reason: '项目负责人确认交接' })
  await page.keyboard.press('Escape')
  await expect(page.locator('.el-overlay.is-drawer:visible')).toHaveCount(0)
  await page.getByRole('button', { name: '系统设置', exact: true }).click()
  await page.getByRole('button', { name: '编号与存储', exact: true }).click()
  await page.getByRole('tab', { name: '编辑权限', exact: true }).click()
  const toggle = page.getByRole('checkbox', { name: /启用自动释放/ })
  await expect(toggle).toBeChecked()
  await expect(page.getByLabel(/持续离线回收/)).toHaveValue('48')
  await toggle.uncheck()
  await page.getByRole('button', { name: '保存编辑权限策略' }).click()
  await expect.poll(() => settings.checkoutAutoReleaseEnabled).toBe(false)
  await toggle.check()
  await page.getByRole('button', { name: '保存编辑权限策略' }).click()
  await expect.poll(() => settings.checkoutAutoReleaseEnabled).toBe(true)
  await page.screenshot({ path: testInfo.outputPath('edit-policy.png'), fullPage: false })
  await page.setViewportSize({ width: 780, height: 900 })
  await expect(toggle).toBeVisible()
  expect(await page.locator('body').evaluate(element => element.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: testInfo.outputPath('edit-policy-narrow.png'), fullPage: false })
  expect(errors).toEqual([])
})

test('editing request needs one click and no reason dialog over HTTP', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  let requests = 0
  await page.route('**/api/edit-locks', route => route.fulfill({ json: [{
    documentId: 'edit-qa', projectId, projectCode: 'PRJ-REAL-001', projectName: '真实装配项目', drawingNumber: 'EDIT-001', documentName: '机架', fileName: 'EDIT-001.SLDPRT',
    checkedOutBy: 'designer', checkedOutAt: '2026-10-13T00:00:00Z', lastHeartbeatAt: '2026-10-13T00:00:00Z', leaseExpiresAt: '2026-10-13T00:15:00Z',
    connectionState: 'Active', attentionLevel: 'Normal', ownedByCurrentUser: false, canRequestRelease: true, canForceRelease: false,
  }] }))
  await page.route('**/api/documents/edit-qa/request-release', route => {
    requests++
    expect(route.request().postDataJSON().reason).toBe('需要继续编辑该图档，请在完成后关闭图档。')
    return route.fulfill({ json: {} })
  })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: /^消息/ }).click()
  await page.getByRole('button', { name: '我需要编辑', exact: true }).click()
  await expect.poll(() => requests).toBe(1)
  await expect(page.getByText('已通知编辑人员，1 个图档结束编辑后会在消息中心通知您')).toBeVisible()
  await expect(page.locator('.el-message-box')).toHaveCount(0)
  await expect(page.getByLabel('消息中心')).toContainText('EDIT-001')
  expect(errors).toEqual([])
  await page.screenshot({ path: join(tmpdir(), 'pdm-edit-request-http.png') })
})

test('sidebar connection lights use neutral rectangles with status-colored bottom borders', async ({ page }, testInfo) => {
  await page.route('**/api/system-connections', route => route.fulfill({ json: {
    pdf: { state: 'connected', message: 'PDF：已连接转图服务器。' },
    u9c: { state: 'disconnected', message: 'U9C：认证失败，请检查配置。' },
  } }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  const lights = page.getByRole('group', { name: '系统连接状态' })
  await expect(lights.getByRole('status')).toHaveText(['SW', 'PDF', 'U9C'])
  await expect(lights.getByRole('status').nth(1)).toHaveAttribute('data-state', 'connected')
  await expect(lights.getByRole('status').nth(2)).toHaveAttribute('title', 'U9C：认证失败，请检查配置。')
  await expect(lights.getByRole('status').nth(1)).toHaveCSS('border-bottom-color', 'rgb(34, 197, 94)')
  await expect(lights.getByRole('status').nth(2)).toHaveCSS('border-bottom-color', 'rgb(239, 68, 68)')
  const geometry = await lights.evaluate(element => {
    const version = element.previousElementSibling!.getBoundingClientRect()
    const rect = element.getBoundingClientRect()
    const settings = element.nextElementSibling!.getBoundingClientRect()
    const items = Array.from(element.children).map(child => ({ height: child.getBoundingClientRect().height, background: getComputedStyle(child).backgroundColor }))
    return { belowVersion: rect.top >= version.bottom, aboveSettings: rect.bottom <= settings.top, items }
  })
  expect(geometry.belowVersion && geometry.aboveSettings).toBe(true)
  expect(geometry.items.every(item => item.height === 22 && item.background === geometry.items[0]!.background)).toBe(true)
  await page.screenshot({ path: testInfo.outputPath('connection-lights-expanded.png') })
  await page.getByRole('button', { name: '折叠主导航', exact: true }).click()
  await expect(lights).toHaveCSS('flex-direction', 'column')
  expect(await lights.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
  await page.screenshot({ path: testInfo.outputPath('connection-lights-collapsed.png') })
})

test('running project list text inset and typography match project center family selection', async ({ page }, testInfo) => {
  await mockPlanCompanionPanels(page)
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.route('**/*jelly*.html*', route => route.fulfill({ contentType: 'text/html', body: '<html><body></body></html>' }))
  const roots = [projectId, 'indent-second'].map((id, index) => ({ project: { id, owner: 'admin', vaultLocation: 'D:/PDM', releaseLocation: 'D:/Release', code: index ? 'QA-CONF-1012000559' : 'P700012', name: index ? '长名称项目用于验证缩进及省略显示效果' : '检测工作站', isActive: true, primaryProjectManager: 'admin', executionUnitName: 'T2事业部', canReadContent: true }, plan: null, planState: 'NotScheduled', oplOpenCount: 0, warnings: [], canRegisterRisk: false, canMaintainRecord: false }))
  await page.route('**/api/workbench**', route => route.fulfill({ json: { views: [{ key: 'manager', label: '项目经理' }], view: 'manager', scopes: ['personal', 'company'], projects: roots, risks: [], updatedAt: '' } }))
  await page.route('**/api/projects/*/opl*', route => route.fulfill({ json: { rows: [], people: [], subprojects: [] } }))
  await page.setViewportSize({ width: 1959, height: 1080 })
  await page.goto('/')
  await expect(page).toHaveTitle('UPTON-PLM')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  const family = page.locator('.pdm-project-family__list > button').first()
  await expect(family).toBeVisible()
  const measure = (element: Element, codeSelector: string) => {
    const row = getComputedStyle(element), list = getComputedStyle(element.parentElement!), code = getComputedStyle(element.querySelector(codeSelector)!)
    return { padding: row.padding, minHeight: row.minHeight, border: row.borderLeftWidth, listPadding: list.padding, textInset: element.querySelector(codeSelector)!.getBoundingClientRect().left - element.parentElement!.getBoundingClientRect().left, fontSize: code.fontSize, weight: code.fontWeight, lineHeight: code.lineHeight }
  }
  const reference = await family.evaluate(measure, '.pdm-project-family__identity strong')
  await page.locator('.pdm-sidebar').getByRole('button', { name: '项目总览', exact: true }).click()
  const list = page.getByLabel('运行中项目列表', { exact: true })
  await expect(list.locator('.wr-project')).toHaveCount(2)
  await expect(list).not.toHaveAttribute('data-pdm-control-layout')
  expect(await list.locator('.wr-project').first().evaluate(measure, '.wr-project-first b')).toEqual(reference)
  await list.getByRole('button', { name: '选择项目 QA-CONF-1012000559', exact: true }).click()
  await expect(list.locator('.wr-project.selected')).toHaveAttribute('aria-label', '选择项目 QA-CONF-1012000559')
  expect(await list.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
  await page.mouse.move(900, 70)
  await page.screenshot({ path: testInfo.outputPath('running-project-list-inset.png'), fullPage: false })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('project review workbench follows OPL and records across dozens of projects', async ({ page }) => {
  test.setTimeout(60000)
  const initialOplRequests: string[] = []
  page.on('request', request=>{if(request.method()==='GET' && /\/opl(?:\?|$)/.test(new URL(request.url()).pathname+new URL(request.url()).search))initialOplRequests.push(request.url())})
  let releaseDetails!:()=>void
  const detailGate=new Promise<void>(resolve=>{releaseDetails=resolve})
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  const rootProject={id:projectId,code:'P700012',name:'检测工作站',owner:'admin',vaultLocation:'D:/PDM',releaseLocation:'D:/Release',isActive:true,primaryProjectManager:'admin',customerName:'华东制造',collaborativeProjectManagers:[],designers:[],responsibleUsers:[],serialNumbers:[],quantity:1}
  const reviewStages=['Design','MaterialPreparation','Assembly','Commissioning','Acceptance','Delivery'].map((code,i)=>({code,name:['设计','备料','装配','调试','验收','交付'][i],participatesInDelivery:true}))
  const projects = Array.from({ length: 46 }, (_, i) => ({ project: { ...rootProject, id: i ? 'review-' + i : projectId, code: 'P' + (700012 + i), name: i ? '例会项目' + i : '检测工作站', primaryProjectManager:'admin', designLead:'engineer', executionUnitName:'T2事业部', canReadContent:true }, plan: { id:'plan-'+i, currentStage:'Assembly', approvalStatus:'Approved', plannedFinish:'2026-11-20', tasks:reviewStages.map((stage,k)=>({id:'task-'+i+'-'+k,name:stage.name+'完成',stage:stage.code,assignee:'admin',plannedStart:'2026-10-10',plannedFinish:'2026-10-20',status:k<2?'Completed':k===2?'InProgress':'NotStarted',isMilestone:true,predecessorTaskIds:[]})),stages:reviewStages }, planState:'Approved', completionPercent:62, oplOpenCount:1, warnings:[],canRegisterRisk:true,canMaintainRecord:true,budget:null,procurement:null,quality:null }))
  await page.route(/\/api\/workbench(?:\?.*)?$/, async route=>{if(new URL(route.request().url()).searchParams.has('projectId'))await detailGate;await route.fulfill({json:{views:[{key:'manager',label:'项目经理'}],view:'manager',scopes:['personal','division','company','sales'],projects,risks:[],updatedAt:new Date().toISOString()}})})
  const issue={id:'issue-1',projectId,number:1,description:'关键传感器未到货',priority:'紧急',status:'处理中',owner:'admin',dueDate:'2026-10-10',solution:'联系供应商确认到货',createdBy:'admin',updatedBy:'admin',createdAt:'',updatedAt:'',completedAt:null,rowVersion:1,attachments:[],relatedProjectId:null}
  await page.route('**/api/projects/*/opl*',route=>route.fulfill({json:{rows:[{issue,canEdit:true,canComplete:true}],people:[{username:'admin',displayName:'系统管理员'}],subprojects:[]}}))
  await page.route('**/api/projects/*/opl/*',async route=>{issue.solution='已协调，明日到货';await route.fulfill({json:issue})})
  const records: any[]=[]
  await page.route('**/api/projects/*/audit*',route=>route.fulfill({json:records.filter(record=>route.request().url().includes('/'+record.entityId+'/'))}))
  await page.route('**/api/projects/*/manager-notes',async route=>{const body=route.request().postDataJSON();const entry={id:'note',occurredAt:new Date().toISOString(),actor:'admin',action:'project.manager-note',entityType:'Project',entityId:projectId,detail:body.content};records.push(entry);await route.fulfill({json:entry})})
  await page.setViewportSize({width:1518,height:855})
  await page.goto('/')
  const login=page.getByLabel('登录PLM')
  await login.getByRole('textbox',{name:'账号'}).fill('admin')
  await login.getByRole('textbox',{name:'密码'}).fill('correct-password')
  await login.getByRole('button',{name:'登录',exact:true}).click()
  await page.locator('.pdm-project-workspace, .pdm-project-manager').first().waitFor()
  initialOplRequests.length=0
  await page.locator('.pdm-sidebar__nav').getByRole('button',{name:/^(项目工作台|项目总览)$/}).click()
  const panel=page.locator('.review-workbench')
  await expect(panel.locator('.wr-project')).toHaveCount(46)
  await expect(page.locator('.pdm-sidebar')).toHaveCSS('background-color','rgb(245, 245, 247)')
  await expect(page.locator('.pdm-sidebar__nav .pdm-nav-item.is-active')).toHaveCSS('background-color','rgb(255, 255, 255)')
  await expect(panel.locator('.wr-project.selected')).toHaveCSS('background-color','rgb(245, 245, 247)')
  for(const tab of await panel.locator('.pdm-tabs button[aria-pressed="true"]').all()) await expect(tab).toHaveCSS('background-color','rgb(245, 245, 247)')
  await expect(panel.locator('.wr-project-second').first()).toContainText('装配')
  await panel.getByLabel('筛选阶段',{exact:true}).selectOption('装配')
  await panel.getByLabel('筛选项目经理',{exact:true}).selectOption('admin')
  await panel.getByLabel('筛选事业部',{exact:true}).selectOption('T2事业部')
  await expect(panel.locator('.wr-project')).toHaveCount(46)

  await expect(panel.locator('.wr-phases i')).toHaveCount(6)
  await expect(panel.locator('.wr-phases [aria-current=step]')).toHaveText('装配')
  await expect(panel.locator('.wr-opl tbody tr').first()).toContainText('关键传感器未到货')
  // 项目中心可能仍有一个延后请求，只统计总览发起的轻量OPL读取。
  expect(initialOplRequests.filter(url=>url.includes('includePeople=false'))).toHaveLength(1)
  await expect(panel.locator('.wr-tasks')).toContainText('详情加载中')
  releaseDetails()
  await panel.locator('.wr-opl tbody tr').first().getByRole('button',{name:'跟进',exact:true}).click()
  const dialog=page.getByRole('dialog',{name:'OPL问题跟进'})
  await dialog.getByLabel('解决措施',{exact:true}).fill('已协调，明日到货')
  await dialog.getByRole('button',{name:'保存跟进',exact:true}).click()
  await expect(dialog).not.toBeVisible()
  await expect(panel.locator('.wr-opl')).toContainText('已协调，明日到货')
  await panel.getByRole('button',{name:'记录跟进',exact:true}).click()
  const note=page.getByRole('dialog',{name:'记录项目跟进'})
  await note.getByLabel('本次结论',{exact:true}).fill('本周确认到货安排')
  await note.getByLabel('下一步行动',{exact:true}).fill('确认到货时间')
  await note.locator('.wr-form-grid select').selectOption('admin')
  // 点击输入框文字区域，验证不必点右侧日历图标。
  await note.getByLabel('完成期限',{exact:true}).click({position:{x:12,y:12}})
  const calendar=page.locator('.el-picker-panel:visible')
  await expect(calendar).toBeVisible()
  await calendar.locator('td.available:not(.prev-month):not(.next-month)').filter({hasText:/^20$/}).click()
  await expect(note.getByLabel('完成期限',{exact:true})).toHaveValue(/\d{4}-\d{2}-20/)
  const dateBox=await note.getByLabel('完成期限',{exact:true}).boundingBox()
  await note.getByLabel('完成期限',{exact:true}).click({position:{x:dateBox!.width-10,y:dateBox!.height/2}})
  await expect(calendar).toBeVisible()
  await note.getByLabel('本次结论',{exact:true}).click()
  await expect(calendar).not.toBeVisible()

  await note.getByRole('button',{name:'保存记录',exact:true}).click()
  await expect(note).not.toBeVisible()
  await expect(panel.getByLabel('跟进记录',{exact:true})).toContainText('本周确认到货安排')
  for (const size of [{width:1959,height:1080},{width:1518,height:855},{width:1000,height:760}]) {
    await page.setViewportSize(size)
    const layout = await panel.evaluate(element => {
      const rect = (selector: string) => element.querySelector(selector)!.getBoundingClientRect().toJSON()
      return {opl:rect('.wr-opl'),tasks:rect('.wr-tasks'),records:rect('.wr-bottom')}
    })
    expect(layout.opl.bottom).toBeLessThanOrEqual(layout.tasks.top)
    if (size.width > 1000) {
      expect(Math.abs(layout.tasks.width-layout.records.width)).toBeLessThanOrEqual(1)
      expect(Math.abs(layout.tasks.top-layout.records.top)).toBeLessThanOrEqual(1)
      expect(Math.abs(layout.tasks.bottom-layout.records.bottom)).toBeLessThanOrEqual(1)
      expect(layout.tasks.right).toBeLessThan(layout.records.left)
    } else {
      expect(layout.tasks.bottom).toBeLessThanOrEqual(layout.records.top)
    }
    if(size.width===1959)await page.screenshot({path:join(tmpdir(),'pdm-workbench-opl-above.png'),animations:'disabled'})
  }
  await panel.getByRole('button',{name:'下一项目',exact:true}).click()
  await expect(panel.locator('.wr-identity')).toContainText('P700013')
  await expect(panel.locator('.wr-project.selected')).toHaveCSS('background-color','rgb(245, 245, 247)')
  for(const size of [{width:1518,height:855},{width:1000,height:760}]){await page.setViewportSize(size);expect(await panel.evaluate(el=>el.scrollHeight<=el.clientHeight+2 && el.scrollWidth<=el.clientWidth+2)).toBe(true)}
  await page.setViewportSize({width:1518,height:855})
  expect((await panel.locator('.wr-projects').boundingBox())!.width).toBe(200)
  await expect(panel.locator('.wr-project-first time')).toHaveCount(0)
  await expect(panel.locator('.wr-project-second time').first()).toBeVisible()
  const statusWidths=await panel.locator('.wr-status').evaluate(el=>Array.from(el.children).map(child=>child.getBoundingClientRect().width))
  for(const [index,ratio] of [5,1,1,1,2].entries()) expect(Math.abs(statusWidths[index]!/statusWidths[1]!-ratio)).toBeLessThan(.03)
  await page.screenshot({path:join(tmpdir(),'pdm-workbench-review.png')})
  const colors=[]
  for (const label of ['松石青','琥珀橙']) {
    await page.locator('.pdm-theme-trigger').click()
    await page.getByRole('menuitem').filter({hasText:label}).click()
    colors.push(await panel.locator('.wr-project.selected').evaluate(el=>getComputedStyle(el).backgroundColor))
  }
  expect(colors).toEqual(['rgb(245, 245, 247)','rgb(245, 245, 247)'])
  await page.screenshot({path:join(tmpdir(),'workbench-review-theme.png')})
  await panel.getByRole('button',{name:'最大化看板',exact:true}).click()
  await expect(panel).toHaveClass(/is-maximized/)
  await page.keyboard.press('Escape')
  await expect(panel).not.toHaveClass(/is-maximized/)
  expect(errors).toEqual([])
})

test('project budget compares costs and saves manual amounts', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  let assessmentGroups: AssessmentGroup[] = []
  let assessmentSaved = false
  let assessmentVersion = 1
  const assessmentNotes: Array<{ id: string; category: string; content: string; createdBy: string; createdAt: string }> = []
  await page.route(`**/api/projects/${projectId}/budget/assessments`, async route => {
    if (route.request().method() === 'PUT') {
      const body = route.request().postDataJSON()
      expect(body.expectedRowVersion).toBe(assessmentVersion)
      assessmentVersion++
      expect(body.groups[0].category).toBe('Standard')
      expect(body.groups[1].category).toBe('Labor')
      expect(body.groups[1].items[0].laborCategory).toBe('MechanicalDesign')
      assessmentGroups = body.groups
      assessmentSaved = true
    }
    const amounts = Object.fromEntries(['Standard', 'MechanicalDesign'].map(category => [category, assessmentSum(assessmentGroups.flatMap(group => group.items.filter(item => (group.category === 'Labor' ? item.laborCategory : group.category) === category).map(assessmentItemTotal)))]))
    await route.fulfill({ json: { laborRates: { MechanicalDesign: 100 }, canEditLaborRates: false, sheets: [{ projectId, projectCode: 'PRJ-REAL-001', projectName: '真实装配项目', designLead: 'engineer', canEdit: true, rowVersion: assessmentVersion, notes: assessmentNotes, groups: assessmentGroups, total: assessmentSum(assessmentGroups.flatMap(group => group.items.map(assessmentItemTotal))), updatedAt: null, updatedBy: null }], amounts } })
  })
  let saved = false
  const rows = budgetCategories.filter(category => category.key !== 'RiskReserve').map(category => calculateBudgetRow({ category: category.key,
    budgetAmount: 10000, actualAmount: 1000, remainingAmount: 0, plannedHours: 100, hourlyRate: 100,
    actualHours: 10, actualHourlyRate: 100, remainingHours: 0, note: null }, null))
  await page.route(`**/api/projects/${projectId}/budget`, async route => {
    if (route.request().method() === 'PUT') {
      const body = route.request().postDataJSON()
      expect(body.lines.some((line: { category: string }) => line.category === 'RiskReserve')).toBe(false)
      expect(body.lines.find((line: { category: string }) => line.category === 'Standard').budgetAmount).toBe(12000)
      expect(body.lines.find((line: { category: string }) => line.category === 'MechanicalDesign').actualAmount).toBe(777)
      expect(body.lines.find((line: { category: string }) => line.category === 'MechanicalDesign').actualHours).toBe(10)
      saved = true
    }
    await route.fulfill({ json: { projectId, rows, orders: [], canEdit: true, canViewReserve: false,
      rowVersion: saved ? 2 : 1, updatedAt: null, updatedBy: null } })
  })
  await page.route(`**/api/projects/${projectId}/budget/notes`, async route => {
    const body = route.request().postDataJSON()
    expect(body.category).toBe('Assessment')
    expect(body.expectedRowVersion).toBe(assessmentVersion)
    assessmentVersion++
    assessmentNotes.push({ id: 'assessment-note', category: 'Assessment', content: body.content, createdBy: 'engineer', createdAt: '2026-10-02T02:00:00Z' })
    await route.fulfill({ json: { projectId, rows, notes: assessmentNotes, orders: [], canEdit: true, canViewReserve: false, rowVersion: assessmentVersion, updatedBy: null, updatedAt: null } })
  })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('navigation', { name: '项目功能' }).getByRole('button', { name: '预算', exact: true }).click()
  const budget = page.locator('.pdm-budget')
  await expect(budget.locator('.pdm-budget__cards article')).toHaveCount(5)
  await expect(budget).not.toContainText('风险预留')
  await expect(budget).not.toContainText('现场电气工程师调试')
  await expect(budget.locator('thead tr')).toHaveCount(2)
  await expect(budget.locator('thead th')).toHaveCount(14)
  await expect(budget.locator('tfoot tr')).toHaveCount(2)
  await expect(budget.locator('tfoot').first()).toContainText('50,000.00')
  await expect(budget.locator('.pdm-budget__cards article').nth(2)).toContainText('10.0%')
  await expect(budget.locator('.pdm-budget__cards article').nth(3)).toContainText('50.0%')
  await expect(budget.getByLabel('计划工时', { exact: true })).toHaveCount(0)
  expect(await budget.locator('th, td').evaluateAll(cells => cells.every(cell => getComputedStyle(cell).textAlign === 'center'))).toBe(true)
  const widths = await budget.locator('.pdm-budget__costs thead').first().locator('th').evaluateAll(cells => cells.map(cell => cell.getBoundingClientRect().width))
  const laborWidths = await budget.locator('.pdm-budget__costs thead').nth(1).locator('th').evaluateAll(cells => cells.map(cell => cell.getBoundingClientRect().width))
  expect(laborWidths).toEqual(widths)
  await budget.getByLabel('预算金额', { exact: true }).first().fill('999999999')
  await budget.getByLabel('预算金额', { exact: true }).first().blur()
  expect(await budget.locator('.pdm-budget__costs thead').first().locator('th').evaluateAll(cells => cells.map(cell => cell.getBoundingClientRect().width))).toEqual(widths)
  await budget.getByLabel('预算金额', { exact: true }).first().fill('12000')
  await budget.getByLabel('预算金额', { exact: true }).first().blur()
  await budget.getByLabel('实际金额', { exact: true }).nth(5).fill('777')
  await budget.getByLabel('实际金额', { exact: true }).nth(5).blur()
  await budget.getByRole('button', { name: '保存预算' }).click()
  await expect.poll(() => saved).toBe(true)
  await expect(budget.getByRole('button', { name: '保存预算' })).toBeDisabled()
  await expect(budget.locator('.pdm-budget__cards article').nth(1)).toContainText('壹拾万元整')
  const amountFont = await budget.locator('.pdm-budget__card-value').nth(3).locator('strong').first().evaluate(element => getComputedStyle(element).fontSize)
  expect(await budget.locator('.pdm-budget__ratio').nth(1).evaluate(element => getComputedStyle(element).fontSize)).toBe(amountFont)
  const actualRatio = budget.locator('.pdm-budget__ratio').nth(1)
  expect(await actualRatio.evaluate(node => Math.abs(node.getBoundingClientRect().right - node.parentElement!.getBoundingClientRect().right))).toBeLessThan(1)
  await actualRatio.hover()
  await expect(page.getByRole('tooltip')).toHaveCount(0)
  expect(await actualRatio.evaluate(node => node.parentElement!.classList.contains('pdm-budget__card-value'))).toBe(true)
  await page.mouse.move(0, 0)
  await budget.getByRole('tab', { name: '预算评估', exact: true }).click()
  const assessmentList = budget.locator('.pdm-budget-assessment__list')
  await expect(assessmentList.locator('thead th').first()).toHaveText('项目号')
  for (const width of [1518, 1024]) {
    await page.setViewportSize({ width, height: 855 })
    const columns = await assessmentList.locator('thead th').evaluateAll(cells => cells.map(cell => cell.getBoundingClientRect().width))
    expect(Math.abs(columns[1]! / columns[0]! - 2)).toBeLessThan(.02)
    expect(await assessmentList.evaluate(table => table.getBoundingClientRect().right <= window.innerWidth && table.parentElement!.scrollWidth <= table.parentElement!.clientWidth)).toBe(true)
  }
  await page.setViewportSize({ width: 1280, height: 720 })
  await assessmentList.locator('tbody tr td').nth(1).click()
  await budget.getByRole('button', { name: '添加分组' }).click()
  await budget.getByLabel('分组名称').first().fill('标准件评估')
  await budget.getByRole('button', { name: '添加明细' }).first().click()
  await budget.getByLabel('明细名称').first().fill('气缸')
  await budget.getByLabel('评估数量').fill('2')
  await budget.getByLabel('评估单价').first().fill('125')
  await budget.getByLabel('评估单价').first().blur()
  await expect(budget.locator('.pdm-assessment-group tfoot')).toContainText('250.00')
  await budget.getByRole('button', { name: '添加分组' }).click()
  await budget.getByLabel('分组名称').nth(1).fill('机械设计评估')
  await budget.locator('.pdm-assessment-group').nth(1).locator('.el-select__wrapper').first().click()
  await page.getByRole('option', { name: '人工成本', exact: true }).click()
  await budget.getByRole('button', { name: '添加明细' }).nth(1).click()
  await expect(budget.locator('.pdm-assessment-group').nth(1).locator('header .el-select')).toHaveCount(1)
  await budget.locator('.pdm-assessment-group').nth(1).locator('tbody .el-select__wrapper').click()
  await page.getByRole('option', { name: '机械设计', exact: true }).click()
  await budget.getByLabel('明细名称').nth(1).fill('设计工时')
  await budget.getByLabel('评估工时').fill('3')
  await expect(budget.getByLabel('评估单价')).toHaveCount(1)
  await expect(budget.getByRole('button', { name: '工时单价设置' })).toHaveCount(0)
  await expect(budget.locator('.pdm-budget-assessment > footer')).toContainText('550.00')
  await page.screenshot({ path: testInfo.outputPath('project-budget-assessment.png'), fullPage: false })
  await budget.locator('.pdm-assessment-title').getByRole('button', { name: 'PRJ-REAL-001评估备注', exact: true }).click()
  const assessmentNotesDialog = page.getByRole('dialog', { name: 'PRJ-REAL-001评估备注', exact: true })
  await assessmentNotesDialog.getByLabel('新备注', { exact: true }).fill('保留未保存的评估明细')
  await assessmentNotesDialog.getByRole('button', { name: '添加备注', exact: true }).click()
  await expect(assessmentNotesDialog.locator('article')).toContainText('保留未保存的评估明细')
  await assessmentNotesDialog.getByRole('button', { name: '关闭', exact: true }).click()
  await expect(budget.getByLabel('分组名称').first()).toHaveValue('标准件评估')
  await expect(budget.getByLabel('评估工时')).toHaveValue('3.00')
  await budget.getByRole('button', { name: '保存评估' }).click()
  await expect.poll(() => assessmentSaved).toBe(true)
  await budget.getByRole('tab', { name: '预算评估', exact: true }).click()
  await expect(assessmentList.locator('thead th')).toHaveText(['项目号', '项目名称', '主设', '物料成本', '人工成本', '备注', '更新时间', '操作'])
  await expect(assessmentList.locator('tbody tr').first().locator('td').nth(3)).toContainText('250.00')
  await expect(assessmentList.locator('tbody tr').first().locator('td').nth(4)).toContainText('300.00')
  await expect(assessmentList.locator('tfoot td').nth(0)).toContainText('250.00')
  await expect(assessmentList.locator('tfoot td').nth(1)).toContainText('300.00')
  await expect(assessmentList.locator('tfoot td').nth(2)).toContainText('550.00')
  await page.screenshot({ path: testInfo.outputPath('project-budget-assessment-list.png'), fullPage: false })
  await budget.getByRole('tab', { name: '预算汇总', exact: true }).click()
  await expect(budget.locator('.pdm-budget__costs tbody').first().locator('tr').first().locator('td').nth(1)).toContainText('250.00')
  await expect(budget.locator('.pdm-budget__costs tbody').nth(1).locator('tr').first().locator('td').nth(1)).toContainText('300.00')
  await page.screenshot({ path: testInfo.outputPath('project-budget-desktop.png'), fullPage: false })
  await page.setViewportSize({ width: 1024, height: 768 })
  await expect(budget.locator('.pdm-budget__cards article')).toHaveCount(5)
  await page.screenshot({ path: testInfo.outputPath('project-budget-compact.png'), fullPage: false })
  expect(errors).toEqual([])
})

test('budget viewers append notes and inspect history without editing amounts', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  const notes = [{ id: 'legacy', category: 'Standard', content: '已有备注', createdBy: 'planner', createdAt: '2026-10-01T12:00:00Z' }]
  let version = 1
  const rows = budgetCategories.filter(category => category.key !== 'RiskReserve').map(category => calculateBudgetRow({ category: category.key, budgetAmount: 1000, actualAmount: 100, remainingAmount: null, plannedHours: null, hourlyRate: null, actualHours: null, actualHourlyRate: null, remainingHours: null, note: null }, null))
  const view = () => ({ projectId, rows, notes, orders: [], canEdit: false, canViewReserve: false, rowVersion: version, updatedAt: null, updatedBy: null })
  await page.route(`**/api/projects/${projectId}/budget`, route => route.fulfill({ json: view() }))
  await page.route(`**/api/projects/${projectId}/budget/assessments`, route => route.fulfill({ json: { sheets: [{ projectId, projectCode: 'PRJ-REAL-001', projectName: '真实装配项目', designLead: 'other', canEdit: false, rowVersion: version, groups: [], total: null, updatedAt: null, updatedBy: null, notes: notes.filter(note => note.category === 'Assessment') }], amounts: {} } }))
  await page.route(`**/api/projects/${projectId}/budget/notes`, async route => {
    const body = route.request().postDataJSON()
    expect(['Standard', 'Assessment']).toContain(body.category)
    expect(body.expectedRowVersion).toBe(version)
    notes.push({ id: `note-${++version}`, category: body.category, content: body.content.trim(), createdBy: 'engineer', createdAt: '2026-10-02T01:30:00Z' })
    await route.fulfill({ json: view() })
  })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('navigation', { name: '项目功能' }).getByRole('button', { name: '预算', exact: true }).click()
  const budget = page.locator('.pdm-budget')
  await expect(budget.getByRole('button', { name: '保存预算' })).toHaveCount(0)
  await expect(budget.getByLabel('预算金额', { exact: true }).first()).toBeDisabled()
  await expect(budget.getByRole('button', { name: '标准件备注', exact: true })).toHaveText('已有备注')
  await budget.getByRole('button', { name: '标准件备注', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '标准件备注', exact: true })
  await expect(dialog).toContainText('已有备注')
  for (const content of ['第一条新备注', '最新备注']) {
    await dialog.getByLabel('新备注', { exact: true }).fill(content)
    await dialog.getByRole('button', { name: '添加备注', exact: true }).click()
    await expect(dialog.locator('.pdm-budget__note-history article').first()).toContainText(content)
  }
  await expect(dialog.locator('article')).toHaveCount(3)
  await expect(dialog).toContainText('真实工程师')
  await expect(dialog).toContainText('2026/10/2')
  await page.screenshot({ path: testInfo.outputPath('budget-note-history.png'), fullPage: false })
  await dialog.getByRole('button', { name: '关闭', exact: true }).click()
  await expect(budget.getByRole('button', { name: '标准件备注', exact: true })).toHaveText('最新备注')
  await budget.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(budget.getByRole('button', { name: '标准件备注', exact: true })).toHaveText('最新备注')
  await budget.getByRole('tab', { name: '预算评估', exact: true }).click()
  const list = budget.locator('.pdm-budget-assessment__list')
  await expect(list.getByRole('columnheader', { name: '备注', exact: true })).toBeVisible()
  await list.getByRole('button', { name: 'PRJ-REAL-001评估备注', exact: true }).click()
  const assessmentDialog = page.getByRole('dialog', { name: 'PRJ-REAL-001评估备注', exact: true })
  await assessmentDialog.getByLabel('新备注', { exact: true }).fill('评估第一条')
  await assessmentDialog.getByRole('button', { name: '添加备注', exact: true }).click()
  await expect(assessmentDialog.locator('article')).toContainText('评估第一条')
  await assessmentDialog.getByRole('button', { name: '关闭', exact: true }).click()
  await expect(list.getByRole('button', { name: 'PRJ-REAL-001评估备注', exact: true })).toHaveText('评估第一条')
  await expect(list).toBeVisible()
  await list.locator('tbody td').nth(1).click()
  const headingNote = budget.locator('.pdm-assessment-title').getByRole('button', { name: 'PRJ-REAL-001评估备注', exact: true })
  await expect(headingNote).toHaveText('评估第一条')
  await headingNote.click()
  await assessmentDialog.getByLabel('新备注', { exact: true }).fill('评估最新备注')
  await assessmentDialog.getByRole('button', { name: '添加备注', exact: true }).click()
  await expect(assessmentDialog.locator('article')).toHaveCount(2)
  await expect(assessmentDialog.locator('article').first()).toContainText('评估最新备注')
  await page.screenshot({ path: testInfo.outputPath('assessment-note-history.png'), fullPage: false })
  await assessmentDialog.getByRole('button', { name: '关闭', exact: true }).click()
  await expect(headingNote).toHaveText('评估最新备注')
  await budget.getByRole('button', { name: '返回列表', exact: true }).click()
  await expect(list.getByRole('button', { name: 'PRJ-REAL-001评估备注', exact: true })).toHaveText('评估最新备注')
  await page.screenshot({ path: testInfo.outputPath('assessment-note-list.png'), fullPage: false })
  expect(errors).toEqual([])
})

test('settlement details sum multiple lines and planning sets the bonus percentage', async ({ page }, testInfo) => {
  let version = 1
  let lines: Array<{ content: string; unit: string | null; quantity: number | null; unitPrice: number | null; note: string | null }> = []
  let rate = .005
  let editing = true
  let viewing = true
  const view = () => {
    const settlement = lines.length ? lines.reduce((sum, line) => sum + (line.quantity ?? 0) * (line.unitPrice ?? 0), 0) : null
    return { projectId, rows: [], notes: [], orders: [], canEdit: false, canViewReserve: false, rowVersion: version, updatedAt: null, updatedBy: null, settlementLines: viewing ? lines : [], settlementAmount: viewing ? settlement : null, bonusRate: editing ? rate : null, bonusAmount: settlement == null ? null : settlement < 200000 ? 0 : Math.round(settlement * rate * 100) / 100, canViewSettlement: viewing, canEditSettlement: editing, canEditBonus: editing }
  }
  await page.route(`**/api/projects/${projectId}/budget`, route => route.fulfill({ json: view() }))
  await page.route(`**/api/projects/${projectId}/budget/assessments`, route => route.fulfill({ json: { sheets: [], amounts: {} } }))
  await page.route(`**/api/projects/${projectId}/budget/settlement`, async route => {
    const body = route.request().postDataJSON()
    expect(body.expectedRowVersion).toBe(version++)
    lines = body.lines.filter((line: { content: string }) => !!line.content)
    expect(lines).toHaveLength(2)
    await route.fulfill({ json: view() })
  })
  await page.route(`**/api/projects/${projectId}/budget/bonus-rate`, async route => {
    const body = route.request().postDataJSON()
    expect(body.expectedRowVersion).toBe(version++)
    expect(body.rate).toBe(.008)
    rate = body.rate
    await route.fulfill({ json: view() })
  })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('navigation', { name: '项目功能' }).getByRole('button', { name: '预算', exact: true }).click()
  const budget = page.locator('.pdm-budget')
  await expect(budget.locator('.pdm-budget__cards article')).toHaveCount(6)
  await expect(budget.locator('.pdm-budget__cards article').nth(1)).toContainText('0.5%')
  await budget.getByRole('button', { name: '结算金额明细', exact: true }).click()
  const settlementDialog = page.getByRole('dialog', { name: '结算金额明细', exact: true })
  for (const item of [{ name: '电磁阀装配线项目', quantity: '1', price: '1620000' }, { name: '追加委托工作', quantity: '2', price: '1000' }]) {
    await settlementDialog.getByRole('button', { name: '添加明细' }).click()
    await settlementDialog.getByLabel('委托工作内容', { exact: true }).last().fill(item.name)
    await settlementDialog.getByLabel('结算单位', { exact: true }).last().fill('套')
    await settlementDialog.getByLabel('结算数量', { exact: true }).last().fill(item.quantity)
    await settlementDialog.getByLabel('结算单价', { exact: true }).last().fill(item.price)
    await settlementDialog.getByLabel('结算单价', { exact: true }).last().blur()
  }
  await settlementDialog.getByRole('button', { name: '添加明细' }).click()
  await expect(settlementDialog.locator('tfoot')).toContainText('1,622,000.00')
  await page.screenshot({ path: testInfo.outputPath('settlement-details.png'), fullPage: false })
  await settlementDialog.getByRole('button', { name: '保存', exact: true }).click()
  await expect(settlementDialog).toBeHidden()
  await expect(budget.locator('.pdm-budget__cards article').first()).toContainText('1,622,000.00')
  await expect(budget.locator('.pdm-budget__cards article').nth(1)).toContainText('8,110.00')
  await budget.getByRole('button', { name: '项目奖金比例', exact: true }).click()
  const bonusDialog = page.getByRole('dialog', { name: '项目奖金', exact: true })
  await bonusDialog.getByLabel('奖金比例', { exact: true }).fill('1.1')
  await bonusDialog.getByLabel('奖金比例', { exact: true }).blur()
  await expect(bonusDialog.getByLabel('奖金比例', { exact: true })).toHaveValue('1.0000')
  await bonusDialog.getByLabel('奖金比例', { exact: true }).fill('0.8')
  await bonusDialog.getByRole('button', { name: '保存', exact: true }).click()
  await expect(bonusDialog).toBeHidden()
  await expect(budget.locator('.pdm-budget__cards article').nth(1)).toContainText('12,976.00')
  await page.setViewportSize({ width: 1518, height: 855 })
  await page.screenshot({ path: testInfo.outputPath('settlement-summary.png'), fullPage: false })
  for (const width of [1518, 1024]) {
    await page.setViewportSize({ width, height: 855 })
    expect(await budget.locator('.pdm-budget__cards').evaluate(node => node.scrollWidth <= node.clientWidth)).toBe(true)
  }
  const originalLines = lines
  lines = [{ content: '小额结算', unit: '项', quantity: 1, unitPrice: 10000, note: null }]
  await budget.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(budget.locator('.pdm-budget__cards article').nth(1)).toContainText('¥ 0.00')
  await budget.getByRole('button', { name: '项目奖金比例', exact: true }).click()
  await expect(bonusDialog).toContainText('项目奖金：¥ 0.00')
  await bonusDialog.getByRole('button', { name: '关闭', exact: true }).click()
  lines = originalLines
  editing = false
  await budget.getByRole('button', { name: '刷新', exact: true }).click()
  await budget.getByRole('button', { name: '结算金额明细', exact: true }).click()
  await expect(settlementDialog.getByLabel('委托工作内容', { exact: true }).first()).toBeDisabled()
  await expect(settlementDialog.getByRole('button', { name: '保存', exact: true })).toHaveCount(0)
  await settlementDialog.getByRole('button', { name: '关闭', exact: true }).click()
  await expect(budget.getByRole('button', { name: '项目奖金比例', exact: true })).toHaveCount(0)
  await expect(budget.locator('.pdm-budget__cards > article').nth(1)).toContainText('12,976.00')
  await expect(budget.locator('.pdm-budget__cards > article').nth(1)).not.toContainText('%')
  await expect(page.locator('.el-overlay:visible')).toHaveCount(0)
  await page.setViewportSize({ width: 1518, height: 855 })
  await page.screenshot({ path: testInfo.outputPath('bonus-viewer.png'), fullPage: false })
  viewing = false
  await budget.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(budget.getByRole('button', { name: '结算金额明细', exact: true })).toHaveCount(0)
  await expect(budget.locator('.pdm-budget__cards article')).toHaveCount(5)
  await expect(budget.locator('.pdm-budget__cards')).not.toContainText('结算金额')
  await expect(budget.locator('.pdm-budget__cards article').first()).toContainText('12,976.00')
  await page.screenshot({ path: testInfo.outputPath('settlement-hidden.png'), fullPage: false })
})

test('assessment labor rates are configured separately from design lead estimates', async ({ page }) => {
  let rates: Record<string, number | null> = {}
  let rateVersion = 0
  const directory = () => ({ ratesProjectId: projectId, ratesRowVersion: rateVersion, canEditLaborRates: true, laborRates: rates, amounts: {}, sheets: [{ projectId, projectCode: 'PRJ-REAL-001', projectName: '真实装配项目', designLead: 'other', canEdit: false, rowVersion: rateVersion, groups: [], total: null, updatedAt: null, updatedBy: null }] })
  await page.route(`**/api/projects/${projectId}/budget/assessments/labor-rates`, async route => {
    const body = route.request().postDataJSON()
    expect(body.expectedRowVersion).toBe(0)
    expect(body.rates.MechanicalDesign).toBe(150)
    rates = body.rates; rateVersion++
    await route.fulfill({ json: directory() })
  })
  await page.route(`**/api/projects/${projectId}/budget/assessments`, route => route.fulfill({ json: directory() }))
  await page.route(`**/api/projects/${projectId}/budget`, route => route.fulfill({ json: { projectId, rows: [], orders: [], canEdit: false, canViewReserve: false, rowVersion: rateVersion } }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('navigation', { name: '项目功能' }).getByRole('button', { name: '预算', exact: true }).click()
  await page.getByRole('tab', { name: '预算评估', exact: true }).click()
  await page.getByRole('button', { name: '工时单价设置' }).click()
  const dialog = page.getByRole('dialog', { name: '工时单价设置' })
  await expect(dialog.locator('tbody tr')).toHaveCount(5)
  await dialog.getByLabel('机械设计工时单价', { exact: true }).fill('150')
  await dialog.getByRole('button', { name: '保存单价' }).click()
  await expect.poll(() => rateVersion).toBe(1)
  await expect(dialog).not.toBeVisible()
  await page.getByRole('button', { name: '工时单价设置' }).click()
  await expect(dialog.getByLabel('机械设计工时单价', { exact: true })).toHaveValue('150.00')
})

const projectId = '11111111-1111-1111-1111-111111111111'

test('resubmitted approval appears as a new unread message without reloading the reviewer page', async ({ page }) => {
  await mockPlanCompanionPanels(page)
  await page.clock.install()
  await page.route('**/assets/melon-jelly-*.html?embed=login', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>登录背景测试占位</title>' }))
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  const uiRoot = join(process.cwd(), 'dist')
  await page.route('http://192.168.2.8:5173/**', async route => {
    const url = new URL(route.request().url())
    if (/\/(?:melon|pirate)-jelly(?:-[^/]*)?\.html$/.test(url.pathname)) return route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>登录背景测试占位</title>' })
    if (url.pathname.startsWith('/api/') || url.pathname === '/health') return route.fallback()
    if (url.pathname === '/client-bootstrap.json') return route.fulfill({ json: { ConfigurationVersion: 'approval-resubmission-qa' } })
    return route.fulfill({ path: join(uiRoot, url.pathname === '/' ? 'index.html' : decodeURIComponent(url.pathname)) })
  })
  await page.route('**/api/browser-archives/capabilities**', route => route.fulfill({ json: { enabled: false, canArchive: false, canConfigure: false } }))
  let tasks: object[] = []
  let messages: object[] = []
  await page.route('**/api/approval-tasks/mine', route => route.fulfill({ json: tasks }))
  await page.route('**/api/notifications/mine**', route => route.fulfill({ json: messages }))
  await page.route('**/api/notifications/read-all', route => {
    messages = messages.map(message => ({ ...message, readAt: '2026-10-28T09:00:00Z' }))
    return route.fulfill({ status: 204 })
  })
  await page.goto('http://192.168.2.8:5173/')
  await expect(page).toHaveTitle('UPTON-PLM')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.locator('.pdm-sidebar').getByRole('button', { name: /^我的待办/ }).click()
  const task = { id: 'reused-approval-task', projectId, projectCode: 'PRJ-REAL-001', projectName: '真实装配项目', releasePackageId: 'resubmitted-package', releasePackageNumber: 'RP-RETRY', stage: 20, packageState: 1, createdAt: '2026-10-27T08:20:00Z' }
  const notice = { id: 'round-1-notice', category: 'ReleaseApprovalPending', title: 'BOM发布已重新提交审批', content: 'RP-RETRY 待您审批', projectId, releasePackageId: task.releasePackageId, sourceKey: 'approval-round-1', createdAt: task.createdAt, readAt: null as string | null }
  tasks = [task]
  messages = [notice]
  await page.clock.fastForward(15_001)
  const notices = page.getByRole('row').filter({ hasText: notice.title })
  await expect(notices).toHaveCount(1)
  await expect(notices.first()).toContainText('未读')
  await expect(page.getByRole('tab', { name: /^审批任务/ })).toHaveText('审批任务（2）')
  tasks = []
  messages = [{ ...notice, readAt: '2026-10-27T08:21:00Z' }]
  await page.clock.fastForward(15_001)
  await expect(page.getByRole('tab', { name: /^审批任务/ })).toContainText('（0）')
  tasks = [task]
  messages.push({ ...notice, id: 'round-2-notice', sourceKey: 'approval-round-2', createdAt: '2026-10-27T08:22:00Z' })
  await page.evaluate(() => window.dispatchEvent(new Event('focus')))
  await expect(notices).toHaveCount(2)
  await expect(notices.filter({ hasText: '未读' })).toHaveCount(1)
  await expect(notices.filter({ hasText: '已读' })).toHaveCount(1)
  await expect(page.getByRole('tab', { name: /^审批任务/ })).toContainText('（2）')
  messages.push({ ...notice, id: 'system-notice', category: 'work-hours', title: '工时提醒', sourceKey: 'work-hours-reminder' })
  await page.evaluate(() => window.dispatchEvent(new Event('focus')))
  await expect(page.getByRole('tab', { name: /^全部待办/ })).toHaveText('全部待办（3）')
  await expect(page.getByRole('tab', { name: /^系统消息/ })).toHaveText('系统消息（1）')
  await page.getByRole('button', { name: '全部已读', exact: true }).click()
  await expect(page.getByRole('tab', { name: /^全部待办/ })).toHaveText('全部待办（1）')
  await expect(page.getByRole('tab', { name: /^审批任务/ })).toHaveText('审批任务（1）')
  await expect(page.getByRole('tab', { name: /^系统消息/ })).toHaveText('系统消息（0）')
  tasks = []
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.getByRole('tab', { name: /^全部待办/ })).toHaveText('全部待办（0）')
  await expect(page.getByRole('tab', { name: /^审批任务/ })).toHaveText('审批任务（0）')
  await expect(notices).toHaveCount(2)
  await expect(notices.filter({ hasText: '已读' })).toHaveCount(2)
  await page.screenshot({ path: join(tmpdir(), 'pdm-task-counts-zero-page.png'), fullPage: false })
  await page.getByRole('button', { name: /^消息\s*0$/ }).click()
  const drawer = page.getByRole('dialog', { name: '消息', exact: true })
  await expect(drawer.getByRole('tab', { name: /^全部待办/ })).toHaveText('全部待办（0）')
  await expect(drawer.getByRole('tab', { name: /^审批任务/ })).toHaveText('审批任务（0）')
  await expect(drawer.getByRole('tab', { name: /^系统消息/ })).toHaveText('系统消息（0）')
  await drawer.getByRole('tab', { name: /^系统消息/ }).click()
  await expect(drawer.getByRole('row').filter({ hasText: '工时提醒' })).toContainText('已读')
  await page.screenshot({ path: join(tmpdir(), 'pdm-task-counts-zero-drawer.png'), fullPage: false })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  await page.screenshot({ path: join(tmpdir(), 'pdm-approval-resubmission-unread.png'), fullPage: false })
  expect(errors).toEqual([])
})

test('project switcher supports inline search while its magnifier keeps the project drawer', async ({ page }) => {
  test.setTimeout(60_000)
  await mockPlanCompanionPanels(page)
  const errors: string[] = []
  page.on('pageerror',error=>errors.push(error.message))
  const base = {owner:'admin',vaultLocation:'D:/PDM',releaseLocation:'D:/Release',isActive:true,quantity:1,serialNumbers:[],designers:[],collaborativeProjectManagers:[],primaryProjectManager:'admin',canReadContent:true,effectiveProjectPermissions:['project.edit','bom.mechanical.edit']}
  const first={...base,id:projectId,code:'P700014',name:'装配源项目'}
  const second={...base,id:'22222222-2222-2222-2222-222222222222',code:'P700015',name:'检测目标设备',customerName:'索引客户',deviceModel:'QA-INDEX',serialNumbers:['80000015']}
  await page.route('**/api/projects**',async route=>{
    const url=new URL(route.request().url())
    if(url.pathname==='/api/projects')return route.fulfill({json:[first,second]})
    if(url.pathname===`/api/projects/${first.id}`)return route.fulfill({json:first})
    if(url.pathname===`/api/projects/${second.id}`)return route.fulfill({json:second})
    if(url.pathname.startsWith(`/api/projects/${second.id}/`))return route.fallback({url:url.toString().replace(second.id,first.id)})
    return route.fallback()
  })
  await page.setViewportSize({width:1959,height:1080})
  await page.goto('/')
  await expect(page).toHaveTitle('UPTON-PLM')
  const login=page.getByLabel('登录PLM')
  await login.getByRole('textbox',{name:'账号'}).fill('admin')
  await login.getByRole('textbox',{name:'密码'}).fill('correct-password')
  await login.getByRole('button',{name:'登录',exact:true}).click()
  await enterProject(page)
  const input=page.getByRole('textbox',{name:'快速搜索项目'})
  await expect(input).toBeVisible()
  const original=(await input.getAttribute('placeholder'))!.startsWith(first.code)?first:second
  const destination=original.id===first.id?second:first
  await input.click()
  await expect(page.getByRole('dialog',{name:'浏览项目',exact:true})).toHaveCount(0)
  await input.fill(destination.name)
  const suggestion=page.locator('.pdm-project-quick-search').getByRole('option').filter({hasText:destination.code})
  await expect(suggestion).toBeVisible()
  await expect(input).toHaveAttribute('placeholder',`${original.code} · ${original.name}`)
  await page.screenshot({path:join(tmpdir(),'pdm-project-quick-search.png'),animations:'disabled'})
  await suggestion.click()
  await expect(input).toHaveAttribute('placeholder',`${destination.code} · ${destination.name}`)
  await expect(input).toBeEnabled()
  await expect(page.getByRole('dialog',{name:'浏览项目',exact:true})).toHaveCount(0)
  await input.fill(original.code.toLowerCase())
  await expect(page.locator('.pdm-project-quick-search').getByRole('option').filter({hasText:original.code})).toBeVisible()
  await input.press('Enter')
  await expect(input).toHaveAttribute('placeholder',`${original.code} · ${original.name}`)
  await expect(input).toBeEnabled()
  await input.fill('没有匹配的项目')
  await input.press('Escape')
  await expect(input).toHaveValue('')
  await expect(input).toHaveAttribute('placeholder',`${original.code} · ${original.name}`)
  await page.getByRole('button',{name:'浏览项目',exact:true}).click()
  const drawer=page.getByRole('dialog',{name:'浏览项目',exact:true})
  await expect(drawer).toBeVisible()
  expect((await drawer.boundingBox())!.x).toBeGreaterThan(900)
  await drawer.getByRole('searchbox',{name:'搜索项目',exact:true}).fill(destination.code)
  await drawer.getByRole('option',{name:`选择浏览项目 ${destination.code}`,exact:true}).click()
  await expect(drawer).toHaveCount(0)
  await expect(input).toHaveAttribute('placeholder',`${destination.code} · ${destination.name}`)
  await expect(input).toBeEnabled()
  for(const width of [1959,1000]){
    await page.setViewportSize({width,height:1080})
    const field=await input.boundingBox(),icon=await page.getByRole('button',{name:'浏览项目',exact:true}).boundingBox()
    expect(field!.width).toBeGreaterThan(40)
    expect(field!.x+field!.width).toBeLessThanOrEqual(icon!.x)
  }
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('document tree uses 50px two-line rows and highlights only stale drawing versions', async ({ page }) => {
  await mockPlanCompanionPanels(page)
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  const common = { projectId, folderId: 'folder-main-mechanical', storedVersionCount: 4, drawingVersionNumber: 4, publishedVersion: 'A', checkedOutBy: null }
  const documents = [
    { ...common, id: 'doc-root', drawingNumber: '700022.00', name: '四立柱机构', fileName: '700022.00.SLDASM', kind: 0, revision: { display: 'A' } },
    { ...common, id: 'doc-part', drawingNumber: '700022.00-01', name: '顶板-30', fileName: '700022.00-01.SLDPRT', kind: 1, revision: { display: 'A-W1' } },
    { ...common, id: 'doc-drawing', drawingNumber: '700022.00-01', name: '顶板工程图', fileName: '700022.00-01.SLDDRW', kind: 2, revision: { display: 'A-W1' } },
    ...Array.from({length:5},(_,index)=>({...common,id:`sub-${index}`,drawingNumber:`SUB-0${index+1}`,name:`第${index+1}层装配`,fileName:`SUB-0${index+1}.SLDASM`,kind:0,revision:{display:'A'}})),
    {...common,id:'deep-part',drawingNumber:'DEEP-PRT-001',name:'深层名称显示优化样本',fileName:'DEEP-PRT-001.SLDPRT',kind:1,revision:{display:'A'}},
  ]
  let deepBranch: Record<string,unknown> = {nodeId:'deep-part',documentId:'deep-part',instancePath:'root/deep/part',fileName:'DEEP-PRT-001.SLDPRT',kind:1,quantity:1,revision:{display:'A'},children:[]}
  for(let index=4;index>=0;index--)deepBranch={nodeId:`sub-${index}`,documentId:`sub-${index}`,instancePath:`root/deep/${index}`,fileName:`SUB-0${index+1}.SLDASM`,kind:0,quantity:1,revision:{display:'A'},children:[deepBranch]}
  await page.route('**/api/projects/*/documents', route => route.fulfill({ json: documents }))
  await page.route('**/api/projects/*/folder-documents', route => route.fulfill({ json: documents }))
  await page.route('**/api/projects/*/reference-tree', route => route.fulfill({ json: { nodeId: 'root', documentId: 'doc-root', instancePath: 'root', fileName: documents[0].fileName, kind: 0, quantity: 1, revision: { display: 'A' }, children: [
    { nodeId: 'stale-part', documentId: 'doc-part', instancePath: 'root/part', fileName: documents[1].fileName, kind: 1, quantity: 1, revision: { display: 'A' }, children: [] },
    deepBranch,
  ] } }))
  await page.route('**/api/projects/*/drawing-review-candidates', route => route.fulfill({ json: [{ candidateId: 'approved-part', modelDocumentId: 'doc-part', drawingDocumentId: 'doc-drawing', drawingNumber: '700022.00-01', bomKinds: ['NonStandard'], state: 'ApprovedCurrent', selectable: false }] }))
  await page.route('**/api/projects/*/drawing-reviews', route => route.fulfill({ json: [] }))
  await page.setViewportSize({ width: 1959, height: 1080 })
  await page.goto('/')
  await expect(page).toHaveTitle('UPTON-PLM')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', {name:'账号'}).fill('admin')
  await login.getByRole('textbox', {name:'密码'}).fill('correct-password')
  await login.getByRole('button', {name:'登录',exact:true}).click()
  await enterProject(page)
  await page.getByRole('navigation', {name:'项目功能'}).getByRole('button', {name:'图档',exact:true}).click()
  const tree = page.getByLabel('项目设计树')
  const stale = tree.locator('.pdm-tree-row.has-version-warning').filter({hasText:'顶板-30'})
  await expect(stale).toHaveCSS('height','50px')
  await expect(stale.locator('.pdm-tree-row__states')).toHaveText('已批准')
  await expect(stale.locator('.pdm-tree-drawing-version')).toHaveText('V4')
  await expect(stale.locator('.pdm-tree-published-version')).toHaveText('A')
  await expect(stale.locator('.pdm-tree-drawing-version')).toHaveCSS('font-weight','700')
  await expect(stale).not.toContainText('引用版本较旧')
  await expect(stale.locator('.pdm-tree-drawing-version')).toHaveAttribute('title',/引用版本较旧/)
  for(let index=0;index<5;index++)await tree.locator('.pdm-tree-row').filter({has:page.getByText(`SUB-0${index+1}`,{exact:true})}).locator('.pdm-tree-row__toggle').click()
  const deepRow=tree.locator('.pdm-tree-row').filter({has:page.getByText('DEEP-PRT-001',{exact:true})})
  await expect(deepRow).toBeVisible()
  await expect(deepRow).toHaveCSS('padding-left','53px')
  await expect(tree.locator('.pdm-tree-row').filter({has:page.getByText('SUB-05',{exact:true})}).locator('.pdm-tree-row__label strong')).toHaveCSS('font-weight','700')
  await expect(deepRow.locator('.pdm-tree-row__label strong')).toHaveCSS('font-weight','500')
  await expect(deepRow.locator('.pdm-tree-row__content')).toHaveCSS('gap','2px')
  await expect(deepRow.locator('.pdm-tree-row__label')).toHaveAttribute('title',/深层名称显示优化样本/)
  const guide=await deepRow.evaluate(element=>{const style=getComputedStyle(element.parentElement!,'::before');return{content:style.content,width:style.borderLeftWidth}})
  expect(guide.content).not.toBe('none')
  expect(guide.width).toBe('1px')
  for (const width of [1959,1000]) {
    await page.setViewportSize({width,height:1080})
    expect((await tree.boundingBox())!.width).toBe(width === 1959 ? 250 : 190)
    expect(await tree.locator('.pdm-tree-row').evaluateAll(rows=>rows.every(row=>row.getBoundingClientRect().height===50))).toBe(true)
    const versionBounds=await tree.locator('.pdm-tree-row').evaluateAll(rows=>rows.map(row=>{const versions=row.querySelector('.pdm-tree-row__versions')!;const rect=versions.getBoundingClientRect();return{text:versions.textContent?.replace(/\s/g,''),width:rect.width,inside:rect.right<=row.getBoundingClientRect().right}}))
    expect(versionBounds.every(item=>item.text==='V4A' && item.width>20 && item.inside),JSON.stringify(versionBounds)).toBe(true)
    const layout = await stale.evaluate(element => {
      const rect = (selector:string) => element.querySelector(selector)!.getBoundingClientRect().toJSON()
      const version = element.querySelector('.pdm-tree-drawing-version')!
      return {row:element.getBoundingClientRect().toJSON(),number:rect('.pdm-tree-row__label strong'),name:rect('.pdm-tree-row__label small'),state:rect('.pdm-tree-row__states'),versions:rect('.pdm-tree-row__versions'),orange:getComputedStyle(element).getPropertyValue('--pdm-orange').trim(),color:getComputedStyle(version).color,publishedColor:getComputedStyle(element.querySelector('.pdm-tree-published-version')!).color}
    })
    expect(layout.row.height).toBe(50)
    expect(Math.abs(layout.number.top-layout.state.top)).toBeLessThanOrEqual(1)
    expect(Math.abs(layout.name.top-layout.versions.top)).toBeLessThanOrEqual(1)
    expect(layout.name.bottom).toBeLessThanOrEqual(layout.row.bottom)
    expect(layout.versions.right).toBeLessThanOrEqual(layout.row.right)
    expect(layout.color).not.toBe(layout.publishedColor)
    await expect(stale.locator('.pdm-tree-drawing-version')).toHaveCSS('color',await stale.evaluate(element=>{const probe=document.createElement('span');probe.style.color='var(--pdm-orange)';element.append(probe);const color=getComputedStyle(probe).color;probe.remove();return color}))
    if(width===1959)await tree.locator('.pdm-tree > .pdm-tree-item').screenshot({path:join(tmpdir(),'pdm-tree-hierarchy-guides.png'),animations:'disabled'})
  }
  await tree.getByRole('tab',{name:/^2D/}).click()
  await expect(tree.locator('.pdm-drawing-list .pdm-tree-row')).toHaveCSS('height','50px')
  await expect(tree.locator('.pdm-drawing-list .pdm-tree-drawing-version')).toHaveText('V4')
  await expect(tree.locator('.pdm-drawing-list .pdm-tree-published-version')).toHaveText('A')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('drawing V versions and published letters remain separate over LAN HTTP', async ({ page }) => {
  await mockPlanCompanionPanels(page)
  await page.route('http://192.168.2.8:5173/**', async route => {
    const url = new URL(route.request().url())
    if (url.pathname.startsWith('/api/') || url.pathname === '/health') return route.fallback()
    if (url.pathname === '/client-bootstrap.json') return route.fulfill({ json: { ConfigurationVersion: 'business-versions-qa' } })
    const response = await route.fetch({ url: `http://127.0.0.1:5193${url.pathname}${url.search}` })
    return route.fulfill({ response })
  })
  await page.setViewportSize({ width: 1600, height: 1000 })
  const errors: string[] = []
  const failedRequests: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('response', response => { if (response.status() >= 400 && new URL(response.url()).pathname.startsWith('/api/')) failedRequests.push(new URL(response.url()).pathname) })
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.route('**/api/browser-archives/capabilities**', route => route.fulfill({ json: { enabled: false, canArchive: false, canConfigure: false } }))
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [] }))
  let number = 3
  const documents = () => [
    { id: 'doc-root', projectId, folderId: 'folder-main-mechanical', drawingNumber: 'REAL-ASM-001', name: '双版本总装', fileName: 'REAL-ASM-001.SLDASM', kind: 0, revision: { display: number === 3 ? 'A' : 'A-W1' }, drawingVersionNumber: number, drawingVersion: `V${number}`, publishedVersion: 'A', storedVersionCount: number + 1 },
    { id: 'doc-drawing', projectId, folderId: 'folder-main-mechanical', drawingNumber: 'REAL-ASM-001', name: '双版本工程图', fileName: 'REAL-ASM-001.SLDDRW', kind: 2, revision: { display: 'A-W1' }, drawingVersionNumber: 4, drawingVersion: 'V4', publishedVersion: 'A', storedVersionCount: 5 },
  ]
  await page.route('**/api/projects/*/documents', route => route.fulfill({ json: documents() }))
  await page.route('**/api/projects/*/folder-documents', route => route.fulfill({ json: documents() }))
  await page.route('**/api/projects/*/reference-tree', route => route.fulfill({ json: { nodeId: 'root', documentId: 'doc-root', instancePath: 'root', fileName: 'REAL-ASM-001.SLDASM', displayName: '双版本总装', kind: 0, quantity: 1, children: [] } }))
  await page.route('**/api/projects/*/boms/Standard**', route => route.fulfill({ json: [{ id: 'bom-version-qa', projectId, kind: 'Standard', sequence: 1, drawingNumber: 'REAL-ASM-001', name: '双版本总装', quantity: 1, unit: '个', revision: 'A-W1', displayRevision: 'A', isComplete: true, sourceDocumentId: 'doc-root' }] }))
  await page.goto('http://192.168.2.8:5173/')
  await expect(page).toHaveTitle('UPTON-PLM')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: '图档', exact: true }).click()
  const tree = page.getByLabel('项目设计树')
  await expect(tree.locator('.pdm-tree-drawing-version').first()).toHaveText('V3')
  await expect(tree.locator('.pdm-tree-published-version').first()).toHaveText('A')
  number = 4
  await tree.getByRole('button', { name: '刷新设计树', exact: true }).click()
  await expect(tree.locator('.pdm-tree-drawing-version').first()).toHaveText('V4')
  await expect(tree.locator('.pdm-tree-published-version').first()).toHaveText('A')
  await page.screenshot({ path: join(tmpdir(), 'pdm-business-versions-desktop.png') })
  await page.getByRole('tab', { name: /^2D/ }).click()
  await expect(tree.locator('.pdm-drawing-list .pdm-tree-drawing-version')).toHaveText('V4')
  await expect(tree.locator('.pdm-drawing-list .pdm-tree-published-version')).toHaveText('A')
  await page.setViewportSize({ width: 780, height: 900 })
  await page.screenshot({ path: join(tmpdir(), 'pdm-business-versions-narrow.png') })
  await page.setViewportSize({ width: 1600, height: 1000 })
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: /^标准件BOM/ }).click()
  await expect(page.locator('.pdm-bom-table thead')).toContainText('发布版本')
  await expect(page.locator('.pdm-bom-table tbody')).toContainText('双版本总装')
  await expect(page.locator('.pdm-bom-table tbody')).not.toContainText('A-W1')
  await expect(page.locator('.pdm-bom-table tbody')).not.toContainText('B-D')
  await page.screenshot({ path: join(tmpdir(), 'pdm-business-versions-bom.png') })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
  expect(failedRequests).toEqual([])
})
test('project list separates project types while preserving child hierarchy and search over LAN HTTP', async ({ page }) => {
  await page.setViewportSize({ width: 1916, height: 1080 })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  const common = { owner: 'admin', vaultLocation: 'D:/PDM/Vault', releaseLocation: 'D:/PDM/Release', isActive: true, quantity: 1, serialNumbers: [], organizationId: 'org-ks', designers: [], collaborativeProjectManagers: [], canReadContent: true, effectiveProjectPermissions: ['project.edit', 'project.child.create'] }
  const researchId = '22222222-2222-2222-2222-222222222222'
  const records = [
    { ...common, id: projectId, rootProjectId: projectId, code: 'P700101', name: '标准项目样本', projectTypeCode: 'P', signedDate: '2026-10-19' },
    { ...common, id: researchId, rootProjectId: researchId, code: 'R700101', name: '研发主项目样本', projectTypeCode: 'R' },
    { ...common, id: '33333333-3333-3333-3333-333333333333', rootProjectId: researchId, parentProjectId: researchId, childSequence: 1, code: 'R700101-1', name: '研发子项目样本' },
    { ...common, id: '44444444-4444-4444-4444-444444444444', code: 'W700101', name: '外发项目样本', projectTypeCode: 'W' },
    { ...common, id: '55555555-5555-5555-5555-555555555555', code: 'LEGACY-001', name: '旧项目样本' },
  ]
  await page.route('**/api/projects', route => route.fulfill({ json: records }))
  await page.route(`**/api/projects/${projectId}`, route => route.fulfill({ json: records[0] }))
  await page.route('**/api/project-numbering/options', route => route.fulfill({ json: { organizations: [organizationDirectory.organizations[0]], projectTypes: [
    { code: 'P', name: '标准项目', isActive: true }, { code: 'R', name: '研发项目', isActive: true }, { code: 'W', name: '外发项目', isActive: true }, { code: 'S', name: '售后项目', isActive: true },
  ], equipmentTypes: [{ code: 2, name: '类型02', isActive: true }] } }))
  await page.route('**/api/workbench**', route => route.fulfill({ json: { projects: [], notes: [], risks: [], scope: 'company', companyName: '测试公司' } }))
  await page.goto('/')
  await expect(page).toHaveTitle(/PLM|UPLM|PDM/)
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号', exact: true }).fill('admin')
  await login.getByLabel('密码', { exact: true }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.locator('.pdm-sidebar__nav').getByRole('button', { name: '项目总览', exact: true }).click()
  await page.locator('.pdm-navigation-tabs').getByRole('tab', { name: '项目列表', exact: true }).click()
  const panel = page.getByRole('region', { name: '项目列表', exact: true })
  const tabs = panel.locator('.pdm-project-type-tabs')
  await expect(tabs.getByRole('tab')).toHaveText(['全部项目', '标准项目', '研发项目', '外发项目', '售后项目', '未分类'])
  await tabs.getByRole('tab', { name: '研发项目', exact: true }).click()
  await expect(panel.locator('tbody tr')).toHaveCount(1)
  await expect(panel.locator('tbody')).toContainText('R700101')
  await panel.getByRole('button', { name: '展开全部项目明细', exact: true }).click()
  await expect(panel.locator('tbody tr')).toHaveCount(2)
  await expect(panel.locator('tbody')).toContainText('研发子项目样本')
  await panel.getByRole('searchbox', { name: '搜索项目', exact: true }).fill('研发子项目')
  await panel.getByRole('button', { name: '搜索', exact: true }).click()
  await expect(panel.locator('tbody')).toContainText('R700101')
  await tabs.getByRole('tab', { name: '标准项目', exact: true }).click()
  await expect(panel.getByRole('heading', { name: '未找到匹配项目', exact: true })).toBeVisible()
  await panel.getByRole('button', { name: '清除筛选', exact: true }).click()
  await expect(tabs.getByRole('tab', { name: '全部项目', exact: true })).toHaveAttribute('aria-selected', 'true')
  await tabs.getByRole('tab', { name: '未分类', exact: true }).click()
  await expect(panel.locator('tbody tr')).toHaveCount(1)
  await expect(panel.locator('tbody')).toContainText('LEGACY-001')
  await tabs.getByRole('tab', { name: '研发项目', exact: true }).click()
  await panel.getByRole('button', { name: '创建主项目', exact: true }).click()
  const create = page.getByRole('dialog', { name: '创建主项目', exact: true })
  await expect(create.locator('[name="projectTypeCode"]')).toHaveValue('R')
  await create.getByRole('button', { name: '取消', exact: true }).click()
  await expect(create).toBeHidden()
  await page.screenshot({ path: join(tmpdir(), 'pdm-project-type-research.png'), fullPage: false, animations: 'disabled' })
  await tabs.getByRole('tab', { name: '全部项目', exact: true }).click()
  await expect(tabs.getByRole('tab', { name: '全部项目', exact: true })).toHaveAttribute('aria-selected', 'true')
  await expect(panel.locator('tbody tr')).toHaveCount(4)
  await expect(page.getByText('部分数据刷新失败', { exact: false })).toHaveCount(0)
  await page.mouse.move(30, 30)
  await page.screenshot({ path: join(tmpdir(), 'pdm-project-type-tabs.png'), fullPage: false, animations: 'disabled' })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})
let materialCodeApplications: Array<Record<string, unknown>> = []

async function enterProject(page: Page) {
  const projectNavigation = page.getByRole('navigation', { name: '项目功能' })
  const projectEntry = page.locator('button.pdm-project-code-link').first()
  await expect.poll(async () => await projectNavigation.isVisible() || await projectEntry.isVisible()).toBe(true)
  if (await projectNavigation.isVisible()) return
  await projectEntry.evaluate(button => (button as HTMLButtonElement).click())
  await expect(projectNavigation).toBeVisible()
}

const versions = [
  { id: 'version-w1', documentId: 'doc-root', revision: { display: 'W1' }, status: 0, fileLength: 1024, sha256: 'A'.repeat(64), createdBy: 'engineer', createdAt: '2026-08-10T01:00:00Z', changeNote: '首次存档' },
  { id: 'version-w2', documentId: 'doc-root', revision: { display: 'W2' }, status: 0, fileLength: 2048, sha256: 'B'.repeat(64), createdBy: 'engineer', createdAt: '2026-08-11T01:00:00Z', changeNote: '完善结构' },
]
const organizationDirectory = {
  organizations: [
    { id: 'org-ks', name: '昆山阿普顿自动化系统有限公司', projectCompanyCode: '7', modelCompanyCode: 'AK', crmCompanyName: '昆山阿普顿自动化系统有限公司', isActive: true, currentProjectSequence: 1, currentSerialSequence: 1 },
    { id: 'org-gz', name: '广州阿普顿自动化系统有限公司', projectCompanyCode: '3', modelCompanyCode: 'AG', crmCompanyName: '广州阿普顿自动化系统有限公司', isActive: true, currentProjectSequence: 1, currentSerialSequence: 1 },
  ],
  units: [
    { id: 'ks-division', organizationId: 'org-ks', code: 'KS-AUTO', name: '昆山自动化事业部', kind: 'BusinessDivision', isActive: true, sortOrder: 1 },
    { id: 'ks-department', organizationId: 'org-ks', parentUnitId: 'ks-division', code: 'KS-AUTO-DESIGN', name: '昆山设计部', kind: 'Department', isActive: true, sortOrder: 1 },
    { id: 'ks-other', organizationId: 'org-ks', code: 'KS-OTHER', name: '昆山其他部门', kind: 'BusinessDivision', isActive: true, sortOrder: 2 },
    { id: 'gz-division', organizationId: 'org-gz', code: 'GZ-AUTO', name: '广州自动化事业部', kind: 'BusinessDivision', isActive: true, sortOrder: 1 },
  ],
  memberships: [
    { unitId: 'ks-department', username: 'engineer', isPrimary: true },
    { unitId: 'gz-division', username: 'gz-user', isPrimary: true },
  ],
  managers: [{ unitId: 'ks-division', primaryManager: 'engineer', collaborativeManagers: [] }],
  users: [
    { username: 'admin', displayName: '系统管理员', role: 'Administrator', isActive: true },
    { username: 'engineer', displayName: '真实工程师', role: 'Engineer', isActive: true },
    { username: 'gz-user', displayName: '广州设计员', role: 'Engineer', isActive: true },
    { username: 'new-user', displayName: '待分配人员', role: 'Engineer', isActive: true },
  ],
}

const referenceChildren = Array.from({ length: 40 }, (_, index) => ({
  nodeId: `node-part-${index + 1}`,
  documentId: index === 0 ? 'doc-part' : null,
  instancePath: `REAL-ASM-001/REAL-PRT-${String(index + 1).padStart(3, '0')}`,
  fileName: `REAL-PRT-${String(index + 1).padStart(3, '0')}.SLDPRT`,
  displayName: `真实零件 ${index + 1}`,
  kind: 1,
  configuration: '默认',
  quantity: index + 1,
  status: 0,
  revision: null,
  checkedOutBy: null,
  children: [],
}))

test.beforeEach(async ({ page }) => {
  materialCodeApplications = []
  let currentUsername = 'engineer'
  const tablePreferences = new Map<string, Record<string, string>>()
  await page.route(/^http:\/\/(?:127\.0\.0\.1:\d+|192\.168\.2\.8:\d+)\/(?:api(?:\/.*)?|health)(?:\?.*)?$/, async (route) => {
    const path = new URL(route.request().url()).pathname
    const fulfill = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
    if (path === '/health') return fulfill({ status: 'ok' })
    if (path === '/api/materials/notification-permission') return fulfill({ enabled: false })
    if (path === '/api/material-stock-plans') return fulfill({ enterpriseCode: '01', organizationCode: '7', items: [] })
    if (path === '/api/me/table-preferences') {
      if (route.request().method() === 'PUT') {
        const input = route.request().postDataJSON() as { key: string; value: string }
        tablePreferences.set(currentUsername, { ...tablePreferences.get(currentUsername), [input.key]: input.value })
        return route.fulfill({ status: 204 })
      }
      return fulfill({ preferences: tablePreferences.get(currentUsername) ?? {} })
    }
    if (path === '/api/auth/logout') return fulfill(true)
    if (path === '/api/auth/login') {
      const credentials = route.request().postDataJSON() as { username?: string }
      const administrator = credentials.username === 'admin'
      currentUsername = administrator ? 'admin' : 'engineer'
      return fulfill({ accessToken: 'e2e-token', expiresAt: '2099-01-01T00:00:00Z', resumeToken: 'e2e-resume-token', username: currentUsername, displayName: administrator ? '系统管理员' : '真实工程师', role: administrator ? 'Administrator' : 'Engineer', permissions: administrator ? ['project.view', 'project.create', 'project.child.create', 'project.content.view', 'project.content.reset', 'document.edit', 'document.recycle', 'bom.edit', 'release.manage', 'material.view', 'material.manage', 'settings.customer.manage', 'settings.organization.manage', 'settings.folder.manage', 'settings.storage.manage', 'system.role.view', 'system.role.edit', 'system.project-permission.view', 'system.project-permission.edit', 'audit.view'] : ['project.view', 'project.create', 'project.child.create', 'project.content.view', 'document.edit', 'bom.edit', 'bom.mechanical.edit', 'bom.electrical.edit', 'material.view', 'release.manage'], primaryCompanyId: 'org-ks', activeCompanyId: 'org-ks', activeCompanyName: '昆山阿普顿自动化系统有限公司', crossCompanyView: administrator, accessibleCompanies: administrator ? [{ id: 'org-ks', name: '昆山阿普顿自动化系统有限公司', code: '7' }, { id: 'org-gz', name: '广州阿普顿自动化系统有限公司', code: '3' }] : [{ id: 'org-ks', name: '昆山阿普顿自动化系统有限公司', code: '7' }] })
    }
    if (path === '/api/auth/me') return fulfill({ username: currentUsername, displayName: currentUsername === 'admin' ? '系统管理员' : '真实工程师', nickname: null, gender: 'unspecified', landline: null, mobilePhone: null, email: null })
    if (path === '/api/system-connections') return fulfill({ pdf: { state: 'unconfigured', message: 'PDF：测试环境未配置。' }, u9c: { state: 'unconfigured', message: 'U9C：测试环境未配置。' } })
    if (path === '/api/auth/profile') return fulfill({ username: currentUsername, displayName: currentUsername === 'admin' ? '系统管理员' : '真实工程师', nickname: null, gender: 'unspecified', landline: null, mobilePhone: null, email: null })
    if (path === '/api/password-reset-requests') return fulfill([])
    if (path === '/api/approval-tasks/mine') return fulfill([])
    if (path === '/api/validation-plan-approval-tasks/mine') return fulfill([])
    if (path === '/api/quality/validation-plan-approval-tasks/mine') return fulfill([])
    if (path === '/api/notifications/mine') return fulfill([])
    if (/^\/api\/projects\/[^/]+\/cad-property-writeback-versions$/.test(path)) return fulfill([])
    if (/^\/api\/projects\/[^/]+\/drawing-reviewers$/.test(path)) return fulfill([])
    if (path === '/api/bom-validation-rules') return fulfill({ standard: ['drawingNumber', 'name', 'unit', 'specification', 'quantity', 'revision'], nonStandard: ['drawingNumber', 'name', 'unit', 'material', 'quantity', 'revision'], electrical: ['drawingNumber', 'name', 'unit', 'quantity', 'revision'] })
    if (path === '/api/material-code/applications') return fulfill(materialCodeApplications)
    if (path === '/api/materials/pending-approval') return fulfill([])
    if (path === '/api/material-sync-tasks') return fulfill([])
    if (path === '/api/material-sync-batches') return fulfill([])
    if (path === '/api/materials' || path === '/api/materials/matches') {
      const parameters = new URL(route.request().url()).searchParams
      const query = parameters.get('query') ?? parameters.get('value') ?? ''
      return fulfill(query.toLocaleLowerCase().includes('ph602') ? [
        { id: 'material-ph602-a', materialCode: '01020000601', name: 'PH602候选A', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001', specification: 'PH602', brand: 'AIRTAC', approvalStatus: 'Approved', syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-09-01T01:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-03T01:00:00Z', rowVersion: 1, isArchived: false },
        { id: 'material-ph602-b', materialCode: '01020000602', name: 'PH602候选B', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001', specification: 'PH602', brand: 'FESTO', approvalStatus: 'Approved', syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-09-01T01:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-03T01:00:00Z', rowVersion: 1, isArchived: false },
      ] : [])
    }
    if (path === '/api/materials/page') {
      const includeArchived = new URL(route.request().url()).searchParams.get('includeArchived') === 'true'
      const items = includeArchived ? [{
        id: 'material-archived', materialCode: '01021000001', name: '测试停用料品', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001',
        specification: 'TEST-001', material: null, remark: null, brand: 'UPTON', surfaceTreatment: null, weight: null, weightUnit: null,
        approvalStatus: 'Approved', approvedBy: 'admin', approvedAt: '2026-09-01T01:00:00Z', categoryCode: '0102', u9CategoryCode: '0102',
        u9ItemId: null, u9ItemCode: null, syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-09-01T01:00:00Z',
        updatedBy: 'admin', updatedAt: '2026-09-03T01:00:00Z', rowVersion: 3, isArchived: true, archivedBy: 'admin',
        archivedAt: '2026-09-03T01:00:00Z', u9SyncConfirmed: true, sourceSystem: 'Pdm', masterOwner: 'Pdm', referenceCount: 0,
      }] : []
      return fulfill({ items, total: items.length, page: 1, pageSize: 50 })
    }
    if (path === '/api/material-categories') return fulfill([{ code: '0102', name: '机械外购件', parentCode: null, pdmKind: 'Standard', defaultSupplyMode: 'Purchase', allowCreate: true, isVisible: true, isActive: true, numberPrefix: '0102', sequenceLength: 7, counterScope: '0102', sortOrder: 1, updatedBy: 'admin', updatedAt: '2026-09-01T01:00:00Z', rowVersion: 1 }])
    if (path === '/api/material-numbering-settings') return fulfill({ startSequence: 1000000, sequenceLength: 7 })
    if (path === '/api/material-approval-rules') return fulfill([{ categoryCode: '0102', requiresApproval: true }])
    if (path === '/api/material-duplicate-rules') return fulfill([{ categoryCode: '0102', fields: ['Specification', 'Brand'] }])
    if (/^\/api\/projects\/[^/]+\/quality-upload-access$/.test(path)) return fulfill(['quality'])
    if (path === '/api/material-inventory') return fulfill({
      items: [], total: 0, page: 1, pageSize: 50,
      warehouseNames: [], brandNames: [], projectCodes: [], projectSubprojects: [],
      lastSuccessfulRefreshAt: null,
    })
    if (path === '/api/material-relations/templates') return fulfill([])
    if (/^\/api\/material-relations\/projects\/[^/]+\/completeness$/.test(path)) return fulfill({
      projectId: path.split('/')[4], isComplete: true, mainMaterialCount: 0, incompleteGroupCount: 0, mainMaterials: [],
    })
    if (path === '/api/program-templates/tasks/mine') return fulfill([])
    if (path === '/api/customers') return fulfill([{ id: 'customer-1', code: 'C00465', name: '中山比亚迪电子有限公司', isActive: true }])
    if (path === '/api/organization-directory') return fulfill(organizationDirectory)
    if (path === '/api/crm-integration') return fulfill({ baseUrl: '', username: '', passwordConfigured: false, autoSyncEnabled: false, autoSyncIntervalMinutes: 60, lastSyncAt: null, lastSyncCount: 0, lastAutoSyncAttemptAt: null, lastAutoSyncError: null })
    if (path === '/api/u9-material-integration') return fulfill({ baseUrl: '', enterpriseCode: '', organizationCode: '', userCode: '', clientId: '', clientSecretConfigured: false, itemCreatePath: '', itemQueryPath: '', itemModifyPath: '', itemDeletePath: '', unitCodeMappings: {}, writeEnabled: false, updatedBy: null, updatedAt: null })
    if (path === '/api/u9-material-full-sync/status') return fulfill({ scheduleTime: '02:00', checkIntervalMinutes: 30, categories: [], latestRun: null })
    if (path === '/api/u9-inventory-sync/status') return fulfill({ settings: { autoSyncEnabled: false, syncIntervalMinutes: 60, queryPath: '' }, latestRun: null })
    if (path === '/api/u9-procurement-sync/status') return fulfill({ settings: { autoSyncEnabled: false, syncIntervalMinutes: 60, queryPath: '' }, latestRun: null })
    if (path === '/api/role-permissions') return fulfill({ permissions: [], roles: [] })
    if (path === '/api/system-settings') return fulfill({ vaultRoot: 'D:\\PDM\\Vault', releaseRoot: 'D:\\PDM\\Release', checkoutHeartbeatSeconds: 180, checkoutLeaseMinutes: 15, checkoutOfflineGraceMinutes: 60, checkoutReminderHours: 4, checkoutStrongReminderHours: 8, checkoutOverdueHours: 24, checkoutForceReleaseHours: 48 })
    if (path === '/api/system-settings/equipment-types') return fulfill([])
    if (path === '/api/folder-template') return fulfill([])
    if (path === '/api/edit-locks') return fulfill([])
    if (path === '/api/project-numbering/options') return fulfill({ organizations: [{ id: '70000000-0000-0000-0000-000000000001', name: '昆山阿普顿自动化系统有限公司', projectCompanyCode: '7', modelCompanyCode: 'AK', crmCompanyName: '昆山阿普顿自动化系统有限公司' }], projectTypes: [{ code: 'P', name: '标准项目' }], equipmentTypes: [{ code: 2, name: '类型02' }] })
    if (path === '/api/projects') return fulfill([{ id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: '真实工程师', vaultLocation: 'D:\\PDM\\PRJ-REAL-001', releaseLocation: 'D:\\Release\\PRJ-REAL-001', isActive: true, quantity: 1, serialNumbers: ['70000001'], executionUnitName: '自动化事业部', primaryProjectManager: 'project-manager', collaborativeProjectManagers: ['project-manager-2'], designLead: 'design-lead', designers: [], canReadContent: true, effectiveProjectPermissions: ['project.edit', 'project.plan.edit', 'bom.mechanical.edit', 'bom.electrical.edit', 'validation-plan.edit'] }])
    if (/^\/api\/projects\/[^/]+\/content-reset\/readiness$/.test(path)) {
      const resetProjectId = path.split('/')[3]
      return fulfill({ project: { id: resetProjectId, code: resetProjectId === projectId ? 'PRJ-REAL-001' : 'P700002', name: '测试项目' }, includeChildren: false, includedProjects: [{ id: resetProjectId, code: resetProjectId === projectId ? 'PRJ-REAL-001' : 'P700002', name: '测试项目' }], canReset: true, blockers: [], counts: {}, restorableSnapshots: [] })
    }
    if (path === `/api/projects/${projectId}`) return fulfill({ id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: '真实工程师', vaultLocation: 'D:\\PDM\\PRJ-REAL-001', releaseLocation: 'D:\\Release\\PRJ-REAL-001', isActive: true, quantity: 1, serialNumbers: ['70000001'], executionUnitName: '自动化事业部', primaryProjectManager: 'project-manager', collaborativeProjectManagers: ['project-manager-2'], designLead: 'design-lead', designers: [], canReadContent: true, effectiveProjectPermissions: ['project.edit', 'project.plan.edit', 'bom.mechanical.edit', 'bom.electrical.edit', 'validation-plan.edit'] })
    if (path === `/api/projects/${projectId}/versions`) return fulfill(versions.map(version => ({ ...version, drawingNumber: 'REAL-ASM-001', documentName: '真实总装配', fileName: 'REAL-ASM-001.SLDASM' })))
    if (path === `/api/projects/${projectId}/audit`) return fulfill([])
    if (route.request().method() === 'GET' && path.startsWith('/api/projects/') && (path.endsWith('/audit') || path.endsWith('/files'))) return fulfill([])
    if (path === `/api/projects/${projectId}/drawing-reviews`) return fulfill([])
    if (path === `/api/projects/${projectId}/drawing-review-candidates`) return fulfill([])
    if (path === `/api/projects/${projectId}/plan/portfolio`) return fulfill({ projects: [], completionPercent: 0, plannedFinish: null })
    if (path === `/api/projects/${projectId}/validation-plan`) return fulfill(null)
    if (path === `/api/projects/${projectId}/procurement-tracking`) return fulfill({ items: [] })
    if (path === `/api/projects/${projectId}/files`) return fulfill([])
    if (path === `/api/projects/${projectId}/folders`) return fulfill([
      { id: 'folder-root', rootProjectId: projectId, parentFolderId: null, targetProjectId: projectId, folderKey: 'root', templateKey: 'root', name: 'PRJ-REAL-001', purpose: 0, sortOrder: 0, isSystem: true, inheritPermissions: true, effectiveAccess: 127, permissions: [] },
      { id: 'folder-mechanical', rootProjectId: projectId, parentFolderId: 'folder-root', targetProjectId: null, folderKey: 'mechanical', templateKey: 'mechanical', name: '机械图纸', purpose: 1, sortOrder: 10, isSystem: true, inheritPermissions: true, effectiveAccess: 15, permissions: [] },
      { id: 'folder-main-mechanical', rootProjectId: projectId, parentFolderId: 'folder-mechanical', targetProjectId: projectId, folderKey: 'mechanical.project:main', templateKey: 'mechanical.project', name: 'PRJ-REAL-001-0', purpose: 3, sortOrder: 10, isSystem: true, inheritPermissions: true, effectiveAccess: 15, permissions: [] },
      { id: 'folder-electrical', rootProjectId: projectId, parentFolderId: 'folder-root', targetProjectId: null, folderKey: 'electrical', templateKey: 'electrical', name: '电气图纸', purpose: 2, sortOrder: 20, isSystem: true, inheritPermissions: true, effectiveAccess: 15, permissions: [] },
      { id: 'folder-main-electrical', rootProjectId: projectId, parentFolderId: 'folder-electrical', targetProjectId: projectId, folderKey: 'electrical.project:main', templateKey: 'electrical.project', name: 'PRJ-REAL-001-0', purpose: 3, sortOrder: 10, isSystem: true, inheritPermissions: true, effectiveAccess: 15, permissions: [] },
    ])
    if (path === `/api/projects/${projectId}/document-relations`) return fulfill([{ modelDocumentId: 'doc-root', drawingDocumentId: 'doc-drawing' }])
    if (path === `/api/projects/${projectId}/folder-documents`) return fulfill([{ id: 'doc-root', projectId, folderId: 'folder-main-mechanical', drawingNumber: 'REAL-ASM-001', name: '真实总装配', fileName: 'REAL-ASM-001.SLDASM', kind: 0, lifecycleState: 0, revision: { display: 'W2' }, checkedOutBy: 'engineer' }, { id: 'doc-part', projectId, folderId: 'folder-main-mechanical', drawingNumber: 'REAL-PRT-001', name: '真实底板', fileName: 'REAL-PRT-001.SLDPRT', kind: 1, lifecycleState: 0, revision: { display: 'A' }, checkedOutBy: null }, { id: 'doc-drawing', projectId, folderId: 'folder-main-mechanical', drawingNumber: 'REAL-ASM-001', name: '真实总装工程图', fileName: 'REAL-ASM-001.SLDDRW', kind: 2, lifecycleState: 0, revision: { display: 'W1' }, checkedOutBy: null }])
    if (path.endsWith('/documents')) return fulfill([{ id: 'doc-root', drawingNumber: 'REAL-ASM-001', name: '真实总装配', fileName: 'REAL-ASM-001.SLDASM', kind: 0, revision: { display: 'W2' }, checkedOutBy: 'engineer' }, { id: 'doc-part', drawingNumber: 'REAL-PRT-001', name: '真实底板', fileName: 'REAL-PRT-001.SLDPRT', kind: 1, revision: { display: 'A' }, checkedOutBy: null }, { id: 'doc-drawing', drawingNumber: 'REAL-ASM-001', name: '真实总装工程图', fileName: 'REAL-ASM-001.SLDDRW', kind: 2, revision: { display: 'W1' }, checkedOutBy: null }])
    if (path.endsWith('/reference-tree')) return fulfill({ nodeId: 'node-root', documentId: 'doc-root', instancePath: 'REAL-ASM-001', fileName: 'REAL-ASM-001.SLDASM', displayName: '真实总装配', kind: 0, configuration: '默认', quantity: 1, status: 0, revision: null, checkedOutBy: 'engineer', children: referenceChildren })
    if (route.request().method() === 'GET' && path.endsWith('/plan')) return fulfill(null)
    if (route.request().method() === 'GET' && path.endsWith('/opl')) return fulfill({ rows: [], people: [] })
    if (route.request().method() === 'GET' && path.endsWith('/budget')) return fulfill({ projectId: path.split('/')[3], rows: [], orders: [], notes: [], budgetAmount: null, actualAmount: null, estimatedAmount: null, overrunAmount: null, canEdit: false, canViewReserve: false, canViewSettlement: false, rowVersion: 0, updatedBy: null, updatedAt: null })
    if (path.endsWith('/boms/Standard')) return fulfill([
      { id: 'bom-standard-1', kind: 'Standard', sequence: 1, drawingNumber: 'REAL-STD-001', name: '标准紧固件', quantity: 4, unit: '件', material: null, specification: 'M8', brand: 'FESTO', revision: 'A', isComplete: true, source: 'Auto', isWearPart: true, isManuallyOverridden: false, isPendingRemoval: false },
      { id: 'bom-standard-2', kind: 'Standard', sequence: 2, drawingNumber: 'REAL-STD-001', name: '标准紧固件', quantity: 6, unit: '件', material: null, specification: 'M8', brand: 'FESTO', revision: 'A', isComplete: true, source: 'Auto', isWearPart: true, isManuallyOverridden: false, isPendingRemoval: false },
    ])
    if (path.endsWith('/boms/NonStandard')) return fulfill([{ id: 'bom-non-standard-1', kind: 'NonStandard', sequence: 1, drawingNumber: 'REAL-PRT-001', name: '真实底板', quantity: 2, unit: '件', material: 'Q235B', specification: '10mm', revision: 'A', isComplete: true, source: 'Auto', isWearPart: true, isManuallyOverridden: false, isPendingRemoval: false }])
    if (path.endsWith('/boms/Unclassified')) return fulfill([])
    if (path.endsWith('/boms/Electrical')) return fulfill([{ id: 'bom-electrical-1', kind: 'Electrical', sequence: 1, drawingNumber: 'REAL-EL-001', name: '真实传感器', quantity: 1, unit: '件', material: null, specification: 'PNP', revision: 'A', isComplete: false }])
    if (path.endsWith('/bom-source-data')) return fulfill([])
    if (path.endsWith('/bom-versions')) return fulfill([])
    if (path.endsWith('/bom-baselines')) return fulfill([])
    if (path.endsWith('/bom-headers')) return fulfill([])
    if (path.endsWith('/boms/empty-declarations')) return fulfill([])
    if (path.endsWith('/release-packages')) return fulfill([{ id: 'package-1', number: 'RP-REAL-001', state: 2, approvalTasks: [{ stage: 1, assignee: '工艺工程师', decisionBy: '工艺工程师', decision: 0, decidedAt: '2026-08-11T01:00:00Z' }, { stage: 2, assignee: '批准人', decisionBy: null, decision: null, decidedAt: null }], publishedAt: null }])
    if (path === '/api/documents/doc-root/versions') return fulfill(versions)
    if (path === '/api/documents/doc-root/where-used') return fulfill([{ documentId: 'doc-root', parentDocumentId: 'doc-parent', projectId, projectCode: 'PRJ-REAL-001', projectName: '真实装配项目', parentDrawingNumber: 'REAL-TOP-001', parentName: '上层总装', parentFileName: 'REAL-TOP-001.SLDASM', parentKind: 0, parentState: 0, parentRevision: { display: 'W3' }, instancePath: 'REAL-TOP-001/REAL-ASM-001-1', configuration: '默认', quantity: 1 }])
    if (path === '/api/documents/doc-root/versions/compare') return fulfill({ documentId: 'doc-root', left: versions[0], right: versions[1], propertyChanges: [], referenceChanges: [], bomChanges: [] })
    if (path === '/api/audit') return fulfill([{ id: 'audit-1', occurredAt: '2026-08-11T01:00:00Z', actor: 'engineer', action: 'VersionViewed', entityType: 'Document', entityId: 'doc-root', detail: '查看 W2' }])
    if (path.endsWith('/storage-status')) return fulfill({ vaultAvailable: true, releaseAvailable: true })
    return fulfill({ title: `Unexpected route: ${path}` }, 404)
  })
})

test('program template owner can delete a saved draft after confirmation', async ({ page }, testInfo) => {
  let drafts = [{
    id: 'template-draft-1', code: 'PT-PLC-0001', assetType: 'PlcProgram', originCompanyId: 'org-ks', originCompanyName: '昆山阿普顿自动化系统有限公司',
    currentPublishedRevisionId: null, isArchived: false, createdBy: 'engineer', createdAt: '2026-09-17T01:00:00Z',
    revisions: [{
      id: 'revision-draft-1', version: 'v1.0.0', attemptNumber: 1, state: 'Draft', name: '待删除程序草稿', category: '控制', description: '浏览器删除验证',
      vendor: 'Siemens', platform: 'TIA Portal', softwareVersion: 'V19', applicableSeries: 'S7-1500', tags: [], changeNote: '',
      packageFileName: 'draft.rar', packageFileLength: 128, packageSha256: 'A'.repeat(64), evidenceFileName: null, evidenceFileLength: null, evidenceSha256: null,
      createdBy: 'engineer', createdAt: '2026-09-17T01:00:00Z', submittedAt: null, publishedAt: null, rowVersion: 3, parameters: [],
    }],
  }]
  await page.route('**/api/auth/login', route => route.fulfill({ json: {
    accessToken: 'e2e-token', expiresAt: '2099-01-01T00:00:00Z', resumeToken: 'e2e-resume-token', username: 'engineer', displayName: '真实工程师', role: 'Engineer',
    permissions: ['project.view', 'project.create', 'project.child.create', 'project.content.view', 'document.edit', 'bom.edit', 'bom.mechanical.edit', 'bom.electrical.edit', 'material.view', 'release.manage', 'program-template.view', 'program-template.submit'],
    primaryCompanyId: 'org-ks', activeCompanyId: 'org-ks', activeCompanyName: '昆山阿普顿自动化系统有限公司', crossCompanyView: false,
    accessibleCompanies: [{ id: 'org-ks', name: '昆山阿普顿自动化系统有限公司', code: '7' }],
  } }))
  await page.route(/^http:\/\/(?:127\.0\.0\.1:\d+|192\.168\.2\.8:5173)\/api\/program-templates(?:\/.*)?(?:\?.*)?$/, route => {
    const url = new URL(route.request().url())
    if (url.pathname === '/api/program-templates/tasks/mine') return route.fulfill({ json: [] })
    if (url.pathname === '/api/program-templates/options') return route.fulfill({ json: { categories: [], vendors: [], platforms: [] } })
    if (url.pathname === '/api/program-templates' && route.request().method() === 'GET')
      return route.fulfill({ json: url.searchParams.get('mine') === 'true' ? drafts : [] })
    if (url.pathname === '/api/program-templates/template-draft-1') return route.fulfill({ json: drafts[0] })
    if (url.pathname === '/api/program-templates/revisions/revision-draft-1' && route.request().method() === 'DELETE') {
      expect(url.searchParams.get('expectedRowVersion')).toBe('3')
      drafts = []
      return route.fulfill({ status: 204 })
    }
    return route.fulfill({ status: 404, json: { title: `Unexpected program template route: ${url.pathname}` } })
  })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.addInitScript(() => { window.setInterval = (() => 0) as unknown as typeof window.setInterval })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.locator('.pdm-sidebar').getByRole('button', { name: '标准物料', exact: true }).click()
  await page.locator('.pdm-navigation-tabs').getByRole('tab', { name: '程序模板', exact: true }).click()
  const library = page.getByLabel('程序模板库')
  await expect(library).toContainText('待删除程序草稿')
  await library.getByRole('button', { name: '查看' }).click()
  await expect(page.getByRole('button', { name: '删除草稿' })).toBeVisible()
  await page.getByRole('button', { name: '删除草稿' }).click()
  const confirmation = page.getByRole('dialog', { name: '删除程序模板草稿' })
  await expect(confirmation).toContainText('无法恢复')
  await page.screenshot({ path: testInfo.outputPath('program-template-delete-confirmation.png') })
  await confirmation.getByRole('button', { name: '确认删除' }).click()
  await expect(library).not.toContainText('待删除程序草稿')
  expect(errors).toEqual([])
})

test('standard release item version uses its frozen BOM publication version', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => { if (!error.message.includes('浏览器禁止使用 WebGPU')) errors.push(error.message) })
  page.on('console', message => { if (message.type() === 'error' && !message.text().includes('浏览器禁止使用 WebGPU')) errors.push(`${message.text()} ${message.location().url}`) })
  if (!process.env.STANDARD_VERSION_QA_BASE_URL || new URL(process.env.STANDARD_VERSION_QA_BASE_URL).hostname === '127.0.0.1') await page.route('**/client-bootstrap.json*', route => route.fulfill({ json: { ConfigurationVersion: '2026.10.30.standard-version-qa', Desktop: { Version: 'QA' }, SolidWorksAddin: { Version: 'QA' } } }))
  await page.route('**/api/projects/*/contact-information', route => route.fulfill({ json: { information: { entries: [], salesPeople: '', presalesPeople: '', rowVersion: 0 }, customerName: '', canEdit: false } }))
  await page.route('**/api/projects/*/nameplate', route => route.fulfill({ json: { nameplate: { enabled: false }, canEdit: false } }))
  await page.route('**/api/projects/*/budget/assessments', route => route.fulfill({ json: [] }))
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [] }))
  const item = { id: 'standard-release-item', projectId, kind: 'Standard', sequence: 1, drawingNumber: '01021000223', name: '折弯钣金', specification: '7000-001', brand: '国优', revision: 'W1', displayRevision: '未发布', unit: '001', quantity: 1, isComplete: true, sourceDocumentId: 'standard-model' }
  const older = { id: 'standard-release-v03', projectId, number: 'RP-STANDARD-V03', state: 'Published', scope: 'StandardFormal', standardBomRevision: 'S-P700016-2-B03', standardBomVersionId: 'standard-v03', standardBomSnapshot: [item], nonStandardBomSnapshot: [], electricalBomSnapshot: [], versionBindings: [], approvalTasks: [], locksDocuments: false, createsManufacturingBaseline: false, createdAt: '2026-10-29T01:00:00Z', publishedAt: '2026-10-29T02:00:00Z' }
  const latest = { ...older, id: 'standard-release-v04', number: 'RP-STANDARD-V04', scope: 'StandardSupplement', standardBomRevision: 'S-P700016-2-B04', standardBomVersionId: 'standard-v04', standardBomSnapshot: [{ ...item, quantity: 2 }], selectedBomItemIds: [item.id], createdAt: '2026-10-30T01:00:00Z', publishedAt: '2026-10-30T02:00:00Z' }
  await page.route('**/api/projects/*/release-packages', route => route.fulfill({ json: [latest, older] }))
  await page.route('**/api/projects/*/release-preview-items*', route => route.fulfill({ json: [] }))
  await page.route('**/api/release-packages/*/item-comments', route => route.fulfill({ json: [] }))
  await page.route('**/api/release-packages/*/execution', route => route.fulfill({ json: { id: 'execution', state: 'Published', ready: true, acknowledgementIds: [], confirmedRemovedBomItemIds: [], workflowNotifications: [], progress: { u9: { Standard: { state: 'Succeeded' } }, previewRequired: false, previewState: 'None', notificationRules: [], departments: [], deliveries: [] } } }))
  await page.setViewportSize({ width: 1700, height: 1000 })
  await page.goto(process.env.STANDARD_VERSION_QA_BASE_URL || '/')
  await expect(page).toHaveTitle('UPTON-PLM')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('navigation', { name: '项目功能' }).getByRole('button', { name: '发布', exact: true }).click()
  await page.getByRole('region', { name: '发布总览' }).getByRole('button', { name: /RP-STANDARD-V04/ }).click()
  const drawer = page.locator('.pdm-bom-release-drawer')
  await expect(drawer).toBeVisible()
  const versionCell = () => drawer.locator('.pdm-release-frozen-table tbody tr').filter({ hasText: '折弯钣金' }).locator('td').nth(4)
  await expect(versionCell()).toHaveText('S-P700016-2-V04')
  await expect(versionCell()).toHaveAttribute('title', '标准件BOM发布版本：S-P700016-2-V04；取自本次发布包冻结记录')
  const information = drawer.locator('details[aria-label="发布包信息"]')
  await information.locator('summary').click()
  await expect(information).toContainText('标准件：S-P700016-2-V04')
  await information.locator('summary').click()
  await drawer.getByRole('button', { name: /RP-STANDARD-V03/ }).click()
  await expect(versionCell()).toHaveText('S-P700016-2-V03')
  await drawer.getByRole('button', { name: /RP-STANDARD-V04/ }).click()
  await expect(versionCell()).toHaveText('S-P700016-2-V04')
  await page.screenshot({ path: join(process.env.NAMEPLATE_QA_OUTPUT_DIR || tmpdir(), 'standard-bom-published-version.png') })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

for (const enabled of [false, true]) test(`material notice routing respects notification permission (${enabled ? 'standardization' : 'project manager'})`, async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => { if (!error.message.includes('浏览器禁止使用 WebGPU')) errors.push(error.message) })
  page.on('console', message => { if (message.type() === 'error' && !message.text().includes('浏览器禁止使用 WebGPU')) errors.push(`${message.text()} ${message.location().url}`) })
  await page.route('**/api/projects/*/contact-information', route => route.fulfill({ json: { information: { entries: [], salesPeople: '', presalesPeople: '', rowVersion: 0 }, customerName: '', canEdit: false } }))
  await page.route('**/api/projects/*/nameplate', route => route.fulfill({ json: { nameplate: { enabled: false }, canEdit: false } }))
  await page.route('**/api/projects/*/budget/assessments', route => route.fulfill({ json: [] }))
  if (!process.env.MATERIAL_NOTICE_QA_BASE_URL || new URL(process.env.MATERIAL_NOTICE_QA_BASE_URL).hostname === '127.0.0.1') await page.route('**/client-bootstrap.json*', route => route.fulfill({ json: { ConfigurationVersion: '2026.10.30.material-notice-qa', Desktop: { Version: 'QA' }, SolidWorksAddin: { Version: 'QA' } } }))
  await page.route('**/api/materials/notification-permission', route => route.fulfill({ json: { enabled } }))
  await page.route('**/api/auth/login', route => route.fulfill({ json: { accessToken: 'e2e-material-notice', expiresAt: '2099-01-01T00:00:00Z', resumeToken: 'e2e-material-resume', username: 'engineer', displayName: enabled ? '标准化工程师' : '项目经理', role: enabled ? 'ProcessReviewer' : 'ProjectManager', roles: enabled ? ['ProcessReviewer'] : ['ProjectManager'], permissions: ['project.view', 'project.content.view', 'material.view', ...(enabled ? ['material.notify'] : [])], primaryCompanyId: 'org-ks', activeCompanyId: 'org-ks', activeCompanyName: '昆山阿普顿自动化系统有限公司', crossCompanyView: false, accessibleCompanies: [{ id: 'org-ks', name: '昆山阿普顿自动化系统有限公司', code: '7' }] } }))
  await page.route('**/api/material-code/applications*', route => route.fulfill({ json: [] }))
  await page.route('**/api/material-sync-tasks*', route => route.fulfill({ json: [{ id: 'material-notice-sync', materialId: 'notice-material', materialCode: '02030000313', materialName: '消息验证料品', specification: 'TEST', brand: 'UPTON', status: 'PreviewReady', createdAt: '2026-10-30T01:00:00Z', updatedAt: '2026-10-30T01:00:00Z', diffLines: [] }] }))
  await page.setViewportSize({ width: 1600, height: 1000 })
  await page.goto(process.env.MATERIAL_NOTICE_QA_BASE_URL || '/')
  await expect(page).toHaveTitle('UPTON-PLM')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  const entry = page.locator('.pdm-sidebar__nav button').filter({ hasText: '料品管理' })
  await expect(entry).toBeVisible()
  await expect(entry.locator('em')).toHaveCount(enabled ? 1 : 0)
  await entry.click()
  const approvals = page.getByRole('tab').filter({ hasText: '料号审批' })
  await approvals.click()
  await expect(approvals.locator('em')).toHaveCount(enabled ? 1 : 0)
  await expect(entry.locator('em')).toHaveCount(enabled ? 1 : 0)
  await expect(page.locator('.material-code-workflow-table.material-code-approval-table--pending')).toContainText('消息验证料品')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
  await page.screenshot({ path: join(process.env.NAMEPLATE_QA_OUTPUT_DIR || tmpdir(), `material-notices-${enabled ? 'standardization' : 'project-manager'}.png`) })
})

test('material notice permission is independently configurable in role settings', async ({ page }) => {
  let saved: string[] = []
  await page.route('**/api/projects/*/contact-information', route => route.fulfill({ json: { information: { entries: [], salesPeople: '', presalesPeople: '', rowVersion: 0 }, customerName: '', canEdit: false } }))
  await page.route('**/api/projects/*/nameplate', route => route.fulfill({ json: { nameplate: { enabled: false }, canEdit: false } }))
  await page.route('**/api/projects/*/budget/assessments', route => route.fulfill({ json: [] }))
  const directory = { permissions: [
    { code: 'material.view', name: '查看料品管理', module: '料品管理', action: 'view', sensitive: false, dependencies: [] },
    { code: 'material.notify', name: '接收料品管理消息提示', module: '料品管理', action: 'notify', sensitive: false, dependencies: ['material.view'] },
  ], roles: [{ role: 'ProcessReviewer', name: '标准化工程师', description: '标准化部门业务角色', baseRole: 'ProcessReviewer', permissions: ['material.view'], isSystem: true, isSystemAdministrator: false, userCount: 1 }] }
  if (!process.env.MATERIAL_NOTICE_QA_BASE_URL || new URL(process.env.MATERIAL_NOTICE_QA_BASE_URL).hostname === '127.0.0.1') await page.route('**/client-bootstrap.json*', route => route.fulfill({ json: { ConfigurationVersion: '2026.10.30.material-notice-qa', Desktop: { Version: 'QA' }, SolidWorksAddin: { Version: 'QA' } } }))
  await page.route('**/api/role-permissions', route => route.fulfill({ json: directory }))
  await page.route('**/api/role-permissions/ProcessReviewer', route => { saved = route.request().postDataJSON().permissions; directory.roles[0].permissions = saved; return route.fulfill({ json: directory }) })
  await page.setViewportSize({ width: 1600, height: 1000 })
  await page.goto(process.env.MATERIAL_NOTICE_QA_BASE_URL || '/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '系统设置', exact: true }).click()
  await page.getByRole('region', { name: '系统管理', exact: true }).getByRole('button', { name: '用户设置', exact: true }).click()
  await page.getByLabel('用户设置功能').getByRole('button', { name: '角色权限', exact: true }).click()
  await page.getByLabel('角色列表').getByRole('button', { name: '权限设置', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '角色权限', exact: true })
  await expect(dialog.getByRole('columnheader', { name: '消息提示', exact: true })).toBeVisible()
  const checkbox = dialog.getByRole('checkbox', { name: /接收料品管理消息提示/ })
  await expect(checkbox).not.toBeChecked()
  await checkbox.check()
  await dialog.getByRole('button', { name: '保存并立即生效', exact: true }).click()
  await expect.poll(() => saved).toContain('material.notify')
  expect(saved).toContain('material.view')
  await expect(dialog).toContainText('角色权限已保存并立即生效')
  await page.screenshot({ path: join(process.env.NAMEPLATE_QA_OUTPUT_DIR || tmpdir(), 'material-notification-permission.png') })
})

test('nameplate split entry and hardware confirmation feed content-only manufacturing', async ({ page }) => {
  const errors: string[] = []
  const loginAnimationWarnings: string[] = []
  function captureError(message: string) {
    if (message.includes('普通 HTTP') && message.includes('浏览器禁止使用 WebGPU')) loginAnimationWarnings.push(message)
    else errors.push(message)
  }
  page.on('pageerror', error => captureError(error.message))
  page.on('console', message => { if (message.type() === 'error') captureError(message.text()) })
  if (!process.env.NAMEPLATE_QA_BASE_URL || new URL(process.env.NAMEPLATE_QA_BASE_URL).hostname === '127.0.0.1') await page.route('**/client-bootstrap.json*', route => route.fulfill({ json: { ConfigurationVersion: '2026.10.29.nameplate-qa', Desktop: { Version: 'QA' }, SolidWorksAddin: { Version: 'QA' } } }))
  await page.route('**/api/projects/*/contact-information', route => route.fulfill({ json: { information: { entries: [], salesPeople: '', presalesPeople: '', rowVersion: 0 }, customerName: '', canEdit: false } }))
  await page.route('**/api/projects/*/budget/assessments', route => route.fulfill({ json: [] }))
  const project = { id: projectId, code: 'MP-QA-001', name: '铭牌流程验收', owner: 'engineer', stage: '进行中', vaultLocation: 'D:/PDM', releaseLocation: 'D:/Release', quantity: 1, deviceModel: 'AK-QA-001', serialNumbers: ['70000001'], organizationId: 'org-ks', executionUnitName: '自动化事业部', primaryProjectManager: 'admin', designers: ['engineer'], phaseOwners: { ElectricalHardware: 'hardware' }, canReadContent: true, effectiveProjectPermissions: ['project.edit'] }
  await page.route('**/api/projects', route => route.fulfill({ json: [project] }))
  await page.route(`**/api/projects/${projectId}`, route => route.fulfill({ json: project }))
  let hardware = false, confirmations = 0
  let plate = { projectId, enabled: false, powerSupply: '', dimensions: '', airPressure: '', weight: '', factoryDate: null as string | null, rowVersion: 0, executionSavedAt: null as string | null, hardwareSavedAt: null as string | null, executionSavedBy: null as string | null, releasePackageId: null as string | null, releasePackageNumber: null as string | null, confirmedBy: null as string | null }
  const view = () => ({ nameplate: plate, name: project.name, model: project.deviceModel, serialNumbers: project.serialNumbers, template: { title: '设备铭牌', labels: {}, rowVersion: 0 }, canEdit: !plate.releasePackageId, canEditExecution: !hardware, canEditHardware: hardware, canConfirm: hardware && !plate.releasePackageId, canManageTemplate: false })
  await page.route('**/api/projects/*/nameplate', route => {
    if (route.request().method() === 'PUT') {
      const input = route.request().postDataJSON()
      expect(input.expectedRowVersion).toBe(plate.rowVersion)
      if (hardware) expect(input.dimensions).toBe(plate.dimensions)
      else expect(input.powerSupply).toBe('')
      plate = { ...plate, ...input, rowVersion: plate.rowVersion + 1, executionSavedAt: hardware ? plate.executionSavedAt : '2026-10-29T01:00:00Z', hardwareSavedAt: hardware ? '2026-10-29T01:01:00Z' : plate.hardwareSavedAt, executionSavedBy: 'engineer' }
    }
    return route.fulfill({ json: view() })
  })
  await page.route('**/api/projects/*/nameplate/confirm', route => {
    expect(hardware).toBe(true)
    expect(route.request().postDataJSON().expectedRowVersion).toBe(plate.rowVersion)
    confirmations++
    plate = { ...plate, rowVersion: plate.rowVersion + 1, releasePackageId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', releasePackageNumber: 'MP-QA-001-MP-001', confirmedBy: 'hardware' }
    return route.fulfill({ json: view() })
  })
  const content = { name: project.name, model: project.deviceModel, serialNumber: '70000001', powerSupply: '380V 50Hz / 5A', ratedPower: 1.9, dimensions: '1500mm x 1800mm x 2200mm', airPressure: '0.5~0.7 MPa', weight: '12000 kg', factoryDate: '2026-10-01', confirmedBy: 'hardware', confirmedAt: '2026-10-29T01:00:00Z' }
  const state = { releasePackageId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', projectId, companyId: 'org-ks', projectCode: project.code, projectName: project.name, packageNumber: 'MP-QA-001-MP-001', scope: 'Nameplate', publishedAt: content.confirmedAt, flowMode: 'DepartmentHandover', lines: [{ id: 'nameplate-line', materialKey: 'MP|001', sourceBomItemIds: ['nameplate-item'], drawingNumber: '70000001', materialCode: 'MP-70000001', name: '设备铭牌', revision: 'A', unit: '001', requiredQuantity: 1, priority: 'Normal', nameplateContent: content }], lots: [], movements: [] }
  await page.route('**/api/production-tracking/projects*', route => route.fulfill({ json: [{ id: projectId, code: project.code, name: project.name, packages: [{ releasePackageId: state.releasePackageId, projectId, projectCode: project.code, projectName: project.name, packageNumber: state.packageNumber, scope: 'Nameplate', publishedAt: state.publishedAt, status: '待接收', totalLines: 1, qualifiedLines: 0, issuedLines: 0, urgentLines: 0, priorityLines: 0, atRiskLines: 0, unplannedLines: 1, totalQuantity: 1 }] }] }))
  await page.route(`**/api/production-tracking/releases/${state.releasePackageId}`, route => route.fulfill({ json: { state, rowVersion: 1 } }))
  await page.route(`**/api/production-tracking/releases/${state.releasePackageId}/external-suppliers`, route => route.fulfill({ json: { companyId: 'org-ks', categories: [], suppliers: [] } }))
  await page.route(`**/api/production-tracking/releases/${state.releasePackageId}/actions`, route => route.fulfill({ json: { actions: [], hint: '等待接收铭牌生产内容' } }))
  await page.setViewportSize({ width: 1440, height: 1000 })
  await page.goto(process.env.NAMEPLATE_QA_BASE_URL || '/')
  await expect(page).toHaveTitle('UPTON-PLM')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('navigation', { name: '项目功能' }).getByRole('button', { name: '信息', exact: true }).click()
  await page.getByRole('button', { name: '创建铭牌 MP-QA-001', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '铭牌 · MP-QA-001', exact: true })
  await dialog.getByRole('button', { name: '编辑铭牌', exact: true }).click()
  await dialog.getByText('启用铭牌', { exact: true }).click()
  await expect(dialog.getByRole('spinbutton', { name: '电压', exact: true })).toBeDisabled()
  for (const [name, value] of [['长度', '1500'], ['宽度', '1800'], ['高度', '2200'], ['重量', '12000']]) await dialog.getByRole('spinbutton', { name, exact: true }).fill(value)
  await dialog.getByRole('textbox', { name: '气压', exact: true }).fill('0.5~0.7')
  await dialog.locator('.el-date-editor input').fill('2026-10')
  await dialog.locator('.el-date-editor input').press('Enter')
  await dialog.getByRole('button', { name: '保存执行信息', exact: true }).click()
  await expect(dialog.getByLabel('铭牌填写分工')).toContainText('已填写')
  await dialog.getByRole('button', { name: '关闭', exact: true }).click()
  const entry = page.getByRole('button', { name: '查看铭牌 MP-QA-001', exact: true })
  await expect(entry).toHaveText('执行已填写')
  const editableColor = await entry.evaluate(element => getComputedStyle(element).color)
  hardware = true
  await page.getByRole('button', { name: '查看铭牌 MP-QA-001', exact: true }).click()
  await dialog.getByRole('button', { name: '编辑铭牌', exact: true }).click()
  await expect(dialog.getByRole('spinbutton', { name: '长度', exact: true })).toBeDisabled()
  for (const [name, value] of [['电压', '380'], ['频率', '50'], ['电流', '5']]) await dialog.getByRole('spinbutton', { name, exact: true }).fill(value)
  await expect(dialog.getByLabel('额定功率', { exact: true })).toHaveText('1.9 kW')
  await page.screenshot({ path: join(process.env.NAMEPLATE_QA_OUTPUT_DIR || tmpdir(), 'nameplate-hardware-entry-state.png') })
  await dialog.getByRole('button', { name: '保存硬件信息', exact: true }).click()
  await expect(dialog.getByLabel('铭牌状态')).toContainText('待硬件确认')
  await dialog.getByRole('button', { name: '确认并发布', exact: true }).click()
  await expect(dialog).toContainText(state.packageNumber)
  expect(confirmations).toBe(1)
  await expect(dialog.getByRole('button', { name: '编辑铭牌', exact: true })).toHaveCount(0)
  await expect(dialog.getByLabel('铭牌状态')).toHaveText('铭牌已发布')
  const publishedMarkup = await dialog.getByLabel('铭牌信息', { exact: true }).innerHTML()
  await dialog.getByRole('button', { name: '关闭', exact: true }).click()
  await expect(entry).toHaveText('已发布')
  expect(await entry.evaluate(element => getComputedStyle(element).color)).not.toBe(editableColor)
  await page.screenshot({ path: join(process.env.NAMEPLATE_QA_OUTPUT_DIR || tmpdir(), 'nameplate-published-status.png') })
  await page.getByRole('button', { name: '制造中心', exact: true }).click()
  const packageButton = page.locator('.production-workbench__package').filter({ hasText: state.packageNumber })
  const projectToggle = page.locator(`[aria-controls="production-project-packages-${projectId}"]`)
  await expect(projectToggle).toBeVisible()
  if (await projectToggle.getAttribute('aria-expanded') !== 'true') await projectToggle.click()
  await expect(packageButton).toBeVisible()
  await packageButton.click()
  await page.getByRole('button', { name: '查看铭牌内容 70000001', exact: true }).click()
  await expect(page.getByLabel('定版铭牌内容')).toContainText(content.dimensions)
  await expect(page.getByLabel('定版铭牌内容')).toContainText('70000001')
  await expect(page.getByLabel('定版铭牌内容')).toContainText('公司统一铭牌标准')
  const productionPreview = page.getByLabel('定版铭牌内容').getByLabel('铭牌信息', { exact: true })
  await expect(productionPreview).toBeVisible()
  expect(await productionPreview.innerHTML()).toBe(publishedMarkup)
  const serialQr = page.getByLabel('定版铭牌内容').getByRole('img', { name: '铭牌序列号二维码', exact: true })
  await expect(serialQr).toBeVisible()
  await expect(serialQr).toHaveAttribute('title', content.serialNumber)
  await expect.poll(() => serialQr.evaluate(element => (element as HTMLImageElement).complete && (element as HTMLImageElement).naturalWidth > 0)).toBe(true)
  const pixels = await serialQr.evaluate(element => {
    const image = element as HTMLImageElement, canvas = document.createElement('canvas')
    canvas.width = image.naturalWidth; canvas.height = image.naturalHeight
    const context = canvas.getContext('2d')!
    context.fillStyle = '#fff'; context.fillRect(0, 0, canvas.width, canvas.height)
    context.drawImage(image, 0, 0)
    return { width: canvas.width, height: canvas.height, data: [...context.getImageData(0, 0, canvas.width, canvas.height).data] }
  })
  expect(jsQR(new Uint8ClampedArray(pixels.data), pixels.width, pixels.height)?.data).toBe(content.serialNumber)
  await expect(page.getByRole('button', { name: '批量下载 PDF', exact: true })).toHaveCount(0)
  await page.screenshot({ path: join(process.env.NAMEPLATE_QA_OUTPUT_DIR || tmpdir(), 'nameplate-serial-qr.png') })
  await page.setViewportSize({ width: 390, height: 844 })
  const panel = page.getByLabel('定版铭牌内容')
  expect(await panel.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
  if (loginAnimationWarnings.length) console.info('已知环境提示：内网 HTTP 下登录背景动画无法使用 WebGPU；铭牌填写、发布及制造内容查看不受影响。')
})

test('project information shows family members and persists independent nameplates', async ({ page }, testInfo) => {
  let contactCanEdit = true
  let failContactSave = false
  let contactInformation = { entries: [] as Array<{ contact: string; phone: string; address: string; isShippingAddress: boolean; position?: string; remarks?: string }>, salesPeople: '', presalesPeople: '', projectId, customerContact: '', customerPhone: '', shippingAddress: '', shippingContact: '', shippingPhone: '', rowVersion: 0 }
  await page.route('**/api/projects/*/contact-information', route => {
    if (route.request().method() === 'PUT') {
      if (failContactSave) return route.fulfill({ status: 409, json: { message: '客户及发货信息已被修改，请刷新后重试。' } })
      const input = route.request().postDataJSON()
      expect(input.expectedRowVersion).toBe(contactInformation.rowVersion)
      expect(input.customerName).toBeUndefined()
      contactInformation = { ...contactInformation, ...input, rowVersion: contactInformation.rowVersion + 1 }
    }
    return route.fulfill({ json: { information: contactInformation, customerName: '自动带出客户公司', canEdit: contactCanEdit } })
  })
  const childId = '22222222-2222-2222-2222-222222222222'
  const root = { id: projectId, code: 'PRJ-REAL-001', canManageMainStaffing: true, canAssignDesigners: true, executionUnitId: 'ks-division', name: '真实装配项目', owner: 'engineer', stage: '进行中', vaultLocation: 'D:/PDM', releaseLocation: 'D:/Release', quantity: 1, deviceModel: 'AK-ROOT-001', serialNumbers: ['70000001'], organizationId: 'org-ks', executionUnitName: '自动化事业部', primaryProjectManager: 'admin', collaborativeProjectManagers: [], designLeads: ['engineer'], designers: [], phaseOwners: { StandardProcurement: 'engineer' }, canReadContent: true, effectiveProjectPermissions: ['project.edit', 'project.plan.edit'] }
  const child = { ...root, id: childId, code: 'PRJ-REAL-001-1', name: '子项目设备', parentProjectId: projectId, rootProjectId: projectId, childSequence: 1, deviceModel: 'AK-CHILD-001', serialNumbers: ['70000002'], designers: ['engineer'] }
  await page.route('**/api/projects', route => route.fulfill({ json: [root, child] }))
  await page.route(`**/api/projects/${projectId}`, route => route.fulfill({ json: root }))
  await page.route(`**/api/projects/${childId}`, route => route.fulfill({ json: child }))
  let staffingWrites = 0
  let phaseWrites = 0
  await page.route('**/api/projects/*/staffing', route => {
    staffingWrites++
    Object.assign(root, route.request().postDataJSON())
    return route.fulfill({ json: root })
  })
  await page.route('**/api/projects/*/phase-owners', route => {
    phaseWrites++
    Object.assign(root, route.request().postDataJSON())
    return route.fulfill({ json: root })
  })
  await page.route('**/api/projects/*/designers', route => {
    const project = route.request().url().includes(childId) ? child : root
    Object.assign(project, route.request().postDataJSON())
    return route.fulfill({ json: project })
  })
  await page.route(`**/api/projects/${childId}/**`, route => route.fallback({ url: route.request().url().replace(childId, projectId) }))
  await page.route(`**/api/projects/${projectId}/release-preview-items`, route => route.fulfill({ json: [] }))
  await page.route('**/api/quality/validation-plan-approval-tasks/mine', route => route.fulfill({ json: [] }))
  const template = { title: '设备铭牌', labels: { name: '名称', model: '型号', serial: '序列号', powerSupply: '电源', dimensions: '尺寸', airPressure: '气压', weight: '重量', factoryDate: '出厂日期' }, rowVersion: 0 }
  let canEditNameplate = true
  const records = new Map([root, child].map(project => [project.id, { projectId: project.id, enabled: false, powerSupply: '', dimensions: '', airPressure: '', weight: '', factoryDate: null as string | null, rowVersion: 0 }]))
  await page.route('**/api/projects/*/nameplate', route => {
    const id = new URL(route.request().url()).pathname.split('/')[3]!
    const project = id === projectId ? root : child
    if (route.request().method() === 'PUT') {
      const input = route.request().postDataJSON()
      expect(input.expectedRowVersion).toBe(records.get(id)!.rowVersion)
      records.set(id, { ...records.get(id)!, ...input, rowVersion: input.expectedRowVersion + 1 })
    }
    return route.fulfill({ json: { nameplate: records.get(id), name: project.name, model: project.deviceModel, serialNumbers: project.serialNumbers, template, canEdit: canEditNameplate, canManageTemplate: true, canEditExecution: canEditNameplate, canEditHardware: canEditNameplate, canConfirm: false } })
  })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(`${message.text()} ${message.location().url}`) })
  await page.setViewportSize({ width: 1518, height: 855 })
  await page.route('**/api/organization-directory', route => route.fulfill({ json: { ...organizationDirectory, users: organizationDirectory.users.map(user => user.username === 'engineer' ? { ...user, roles: ['Engineer', 'HardwareEngineer'] } : user), memberships: [...organizationDirectory.memberships, { unitId: 'ks-department', username: 'new-user', isPrimary: true }] } }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('navigation', { name: '项目功能' }).getByRole('button', { name: '信息', exact: true }).click()
  const info = page.getByLabel('项目信息', { exact: true })
  await expect(info.getByLabel('项目基本信息列表').locator('tbody tr')).toHaveCount(2)
  const contacts = info.getByRole('form', { name: '客户及发货信息', exact: true })
  await expect(contacts).toContainText('自动带出客户公司')
  await contacts.getByRole('button', { name: '新增', exact: true }).click()
  await contacts.getByRole('textbox', { name: '联系人 1', exact: true }).fill('张先生')
  await contacts.getByRole('textbox', { name: '联系方式 1', exact: true }).fill('13800000000')
  for (const index of [2, 3]) {
    await contacts.getByRole('button', { name: '新增', exact: true }).click()
    await contacts.getByRole('textbox', { name: '联系人 ' + index, exact: true }).fill('收货联系人' + index)
    await contacts.getByRole('textbox', { name: '收货地址 ' + index, exact: true }).fill('测试地址' + index)
    await contacts.getByRole('checkbox', { name: '标记收货地址 ' + index, exact: true }).check()
  }
  await contacts.getByRole('textbox', { name: '职位 1', exact: true }).fill('采购经理')
  await contacts.getByRole('textbox', { name: '备注 1', exact: true }).fill('工作日联系')
  await expect(contacts.getByRole('columnheader')).toHaveText(['序号', '用途', '联系人', '联系方式', '职位', '备注', '收货地址', '操作'])
  for (const label of ['销售人员', '售前人员']) {
    await info.getByRole('button', { name: '分配' + label, exact: true }).click()
    const selectDialog = page.getByRole('dialog', { name: '分配' + label, exact: true })
    await selectDialog.locator('.el-select').click()
    const combo = selectDialog.getByRole('combobox', { name: '选择销售售前人员' })
    await page.locator('#' + await combo.getAttribute('aria-controls')).getByRole('option', { name: '真实工程师', exact: true }).click()
    await selectDialog.getByRole('button', { name: '完成人员选择' }).click()
  }
  await contacts.getByRole('button', { name: '保存客户信息', exact: true }).click()
  await expect.poll(() => contactInformation.rowVersion).toBe(1)
  expect(contactInformation.entries.filter(entry => entry.isShippingAddress)).toHaveLength(2)
  expect(contactInformation.entries[0]!.address).toBe('')
  expect(contactInformation.salesPeople).toBe('engineer')
  expect(contactInformation.entries[0]!.position).toBe('采购经理')
  expect(contactInformation.entries[0]!.remarks).toBe('工作日联系')
  await contacts.getByRole('button', { name: '刷新客户信息', exact: true }).click()
  await expect(contacts.getByRole('textbox', { name: '收货地址 2', exact: true })).toHaveValue('测试地址2')
  await expect(contacts.locator('tr.is-shipping')).toHaveCount(2)
  const personnel = info.getByLabel('项目人员信息列表')
  await expect(personnel.locator('tbody tr')).toHaveCount(1)
  await expect(personnel.getByRole('columnheader', { name: '执行工程师', exact: true })).toHaveCount(0)
  await expect(info.getByLabel('项目基本信息列表').locator('tbody tr').nth(1)).toContainText('真实工程师')
  expect(await personnel.locator('table').evaluate(el => el.scrollWidth <= el.clientWidth)).toBeTruthy()
  const buttons = await contacts.locator('header .el-button').evaluateAll(elements => elements.map(el => ({ width: el.getBoundingClientRect().width, height: el.getBoundingClientRect().height })))
  expect(buttons).toEqual([{ width: 60, height: 24 }, { width: 60, height: 24 }, { width: 60, height: 24 }])
  expect(await personnel.evaluate(el => el.compareDocumentPosition(document.querySelector('[aria-label="项目基本信息列表"]')!) & Node.DOCUMENT_POSITION_FOLLOWING)).toBeTruthy()
  await page.screenshot({ path: testInfo.outputPath('project-contact-information.png') })
  await info.getByRole('button', { name: '分配团队人员', exact: true }).click()
  const team = page.getByRole('dialog', { name: '分配项目团队', exact: true })
  await expect(team.getByRole('combobox')).toHaveCount(13)
  const hardware = page.getByRole('combobox', { name: '电气硬件', exact: true })
  await team.locator('.el-select').filter({ has: hardware }).click()
  const hardwareOptions = page.locator('#' + await hardware.getAttribute('aria-controls'))
  await expect(hardwareOptions.getByRole('option')).toHaveCount(1)
  await hardwareOptions.getByRole('option', { name: '真实工程师', exact: true }).click()
  await team.getByText('分配项目团队', { exact: true }).click()
  await expect(team.getByRole('combobox', { name: '项目经理', exact: true })).toBeDisabled()
  await expect(team.getByRole('combobox', { name: '主设', exact: true })).toBeEnabled()
  await expect(team.getByRole('combobox', { name: '协同项目经理', exact: true })).toBeEnabled()
  await page.screenshot({ animations: 'disabled', path: testInfo.outputPath('project-complete-team.png') })
  await team.locator('.el-select').filter({ has: page.getByRole('combobox', { name: '团队售前人员', exact: true }) }).click()
  await page.locator('#' + await team.getByRole('combobox', { name: '团队售前人员' }).getAttribute('aria-controls')).getByRole('option', { name: '真实工程师', exact: true }).click()
  await team.getByText('分配项目团队', { exact: true }).click()
  await team.locator('.el-select').filter({ has: page.getByRole('combobox', { name: '验收', exact: true }) }).click()
  await page.locator('#' + await team.getByRole('combobox', { name: '验收', exact: true }).getAttribute('aria-controls')).getByRole('option', { name: '真实工程师', exact: true }).click()
  await page.locator('#' + await team.getByRole('combobox', { name: '验收', exact: true }).getAttribute('aria-controls')).getByRole('option', { name: '待分配人员', exact: true }).click()
  await team.getByText('分配项目团队', { exact: true }).click()
  await team.getByRole('button', { name: '保存人员分配', exact: true }).click()
  await expect(team).not.toBeVisible()
  expect(staffingWrites).toBe(1)
  expect(contactInformation.presalesPeople).toBe('')
  expect(phaseWrites).toBe(1)
  await expect(personnel.locator('thead th').nth(3)).toHaveText('电气硬件')
  await expect(personnel.locator('tbody td').nth(3)).toHaveText('真实工程师')
  await expect(personnel.locator('tbody td').last()).toHaveText('真实工程师、待分配人员')
  await info.getByRole('button', { name: '分配执行工程师 PRJ-REAL-001', exact: true }).click()
  const engineerDialog = page.getByRole('dialog', { name: '分配执行工程师 · PRJ-REAL-001', exact: true })
  await engineerDialog.locator('.el-select').filter({ has: page.getByRole('combobox', { name: '执行工程师', exact: true }) }).click()
  await page.locator('#' + await engineerDialog.getByRole('combobox', { name: '执行工程师', exact: true }).getAttribute('aria-controls')).getByRole('option', { name: '真实工程师', exact: true }).click()
  await page.keyboard.press('Escape')
  root.primaryProjectManager = 'engineer'
  await engineerDialog.getByRole('button', { name: '保存人员分配', exact: true }).click()
  await expect(engineerDialog).not.toBeVisible()
  expect(root.designers).toEqual(['engineer'])
  await expect(info.getByRole('button', { name: '分配团队人员', exact: true })).toHaveCount(0)
  failContactSave = true
  await contacts.getByRole('textbox', { name: '联系人 1', exact: true }).fill('未保存联系人')
  await contacts.getByRole('button', { name: '保存客户信息', exact: true }).click()
  await expect(contacts.getByRole('alert')).toContainText('已被修改')
  expect(contactInformation.entries[0]!.contact).toBe('张先生')
  errors.length = 0
  failContactSave = false
  contactCanEdit = false
  await contacts.getByRole('button', { name: '刷新客户信息', exact: true }).click()
  await expect(contacts.getByRole('textbox')).toHaveCount(0)
  await expect(contacts.getByRole('button', { name: '保存客户信息' })).toHaveCount(0)
  await expect(contacts).toContainText('张先生')
  await expect(personnel).toContainText('真实工程师')
  await expect(contacts).toContainText('采购经理')
  await expect(contacts).toContainText('工作日联系')
  await page.screenshot({ path: testInfo.outputPath('project-information-people.png') })
  await info.getByRole('button', { name: '创建铭牌 PRJ-REAL-001', exact: true }).click()
  const nameplate = page.getByRole('dialog', { name: '铭牌 · PRJ-REAL-001', exact: true })
  await expect(nameplate.getByLabel('铭牌信息')).toContainText('AK-ROOT-001')
  const dialogSize = await nameplate.locator('.el-dialog').boundingBox()
  const plateSize = await nameplate.getByLabel('铭牌信息').boundingBox()
  expect(dialogSize!.width).toBe(620)
  expect(dialogSize!.width - plateSize!.width).toBeLessThan(80)
  await expect(nameplate.getByLabel('铭牌信息').locator('input')).toHaveCount(0)
  await nameplate.getByRole('button', { name: '编辑铭牌', exact: true }).click()
  await expect(nameplate.getByRole('spinbutton', { name: '电压', exact: true })).toBeDisabled()
  await expect(nameplate.getByRole('textbox', { name: '气压', exact: true })).toHaveValue('0.5~0.7')
  const currentMonth = await page.evaluate(() => { const now = new Date(); return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}` })
  await expect(nameplate.locator('.el-date-editor input')).toHaveValue(currentMonth)
  await nameplate.getByText('启用铭牌', { exact: true }).click()
  await nameplate.getByRole('spinbutton', { name: '电压', exact: true }).fill('220')
  await nameplate.getByRole('spinbutton', { name: '频率', exact: true }).fill('50')
  await nameplate.getByRole('spinbutton', { name: '电流', exact: true }).fill('4')
  await expect(nameplate.getByLabel('额定功率', { exact: true })).toHaveText('0.88 kW')
  await nameplate.getByRole('spinbutton', { name: '长度', exact: true }).fill('1200')
  await nameplate.getByRole('spinbutton', { name: '宽度', exact: true }).fill('1200')
  await nameplate.getByRole('spinbutton', { name: '高度', exact: true }).fill('2200')
  await nameplate.getByRole('textbox', { name: '气压', exact: true }).fill('0.6')
  await nameplate.getByRole('spinbutton', { name: '重量', exact: true }).fill('500')
  await nameplate.locator('.el-date-editor input').fill('2026-10')
  await nameplate.locator('.el-date-editor input').press('Enter')
  await nameplate.getByRole('button', { name: '保存铭牌', exact: true }).click()
  await expect.poll(() => records.get(projectId)!.rowVersion).toBe(1)
  expect(records.get(projectId)!.factoryDate).toBe('2026-10-01')
  await expect(nameplate.getByRole('button', { name: '维护模板', exact: true })).toHaveCount(0)
  expect(records.get(projectId)!.powerSupply).toBe('220V 50Hz / 4A')
  expect(records.get(projectId)!.dimensions).toBe('1200mm x 1200mm x 2200mm')
  expect(records.get(projectId)!.weight).toBe('500 kg')
  expect(records.get(projectId)!.airPressure).toBe('0.6 MPa')
  await expect(nameplate.getByLabel('铭牌信息').locator('input')).toHaveCount(0)
  await expect(nameplate.getByLabel('铭牌信息')).toContainText('0.6 MPa')
  await expect(nameplate.getByLabel('铭牌信息')).toContainText('500 kg')
  await expect(nameplate.getByLabel('铭牌信息')).toContainText('1200mm x 1200mm x 2200mm')
  const selectedNameplate = await nameplate.getByLabel('铭牌信息').evaluate(element => {
    const range = document.createRange()
    range.selectNodeContents(element)
    const selection = window.getSelection()!
    selection.removeAllRanges()
    selection.addRange(range)
    const text = selection.toString()
    selection.removeAllRanges()
    return text
  })
  for (const value of ['220V 50Hz / 4A', '0.88 kW', '0.6 MPa', '500 kg', '1200mm x 1200mm x 2200mm', '2026-10', 'AK-ROOT-001', '70000001']) expect(selectedNameplate).toContain(value)
  await page.screenshot({ path: testInfo.outputPath('project-nameplate.png') })
  await nameplate.getByRole('button', { name: '关闭', exact: true }).click()
  await expect(nameplate).toBeHidden()
  await info.getByRole('button', { name: '创建铭牌 PRJ-REAL-001-1', exact: true }).click()
  const childNameplate = page.getByRole('dialog', { name: '铭牌 · PRJ-REAL-001-1', exact: true })
  await expect(childNameplate.getByLabel('铭牌信息')).toContainText('70000002')
  await expect(childNameplate.getByLabel('铭牌信息').locator('input')).toHaveCount(0)
  await childNameplate.getByRole('button', { name: '编辑铭牌', exact: true }).click()
  await expect(childNameplate.getByRole('spinbutton', { name: '电压', exact: true })).toHaveValue('')
  await expect(childNameplate.getByRole('checkbox', { name: '启用铭牌', exact: true })).not.toBeChecked()
  await childNameplate.getByRole('button', { name: '关闭', exact: true }).click()
  await expect(childNameplate).toBeHidden()
  await info.getByRole('button', { name: '查看铭牌 PRJ-REAL-001', exact: true }).click()
  await expect(nameplate.getByLabel('铭牌信息')).toContainText('220V 50Hz / 4A')
  await expect(nameplate.getByLabel('铭牌信息').locator('input')).toHaveCount(0)
  await nameplate.getByRole('button', { name: '关闭', exact: true }).click()
  await expect(nameplate).toBeHidden()
  await page.locator('.pdm-sidebar').getByRole('button', { name: '项目总览', exact: true }).click()
  await page.locator('.pdm-navigation-tabs').getByRole('tab', { name: '项目列表', exact: true }).click()
  const projectList = page.getByLabel('项目列表', { exact: true })
  await expect(projectList.getByRole('columnheader', { name: '铭牌', exact: true })).toBeVisible()
  await projectList.getByRole('button', { name: '查看铭牌 PRJ-REAL-001', exact: true }).click()
  await expect(nameplate.getByLabel('铭牌信息')).toContainText('220V 50Hz / 4A')
  await expect(nameplate.getByLabel('铭牌信息').locator('input')).toHaveCount(0)
  await nameplate.getByRole('button', { name: '关闭', exact: true }).click()
  await expect(nameplate).toBeHidden()
  await enterProject(page)
  await page.getByRole('navigation', { name: '项目功能' }).getByRole('button', { name: '发布', exact: true }).click()
  await expect(page.locator('.pdm-release-stream-card')).toHaveCount(3)
  await expect(page.locator('.pdm-release-overview-grid')).not.toContainText('历史组合发布')
  canEditNameplate = false
  await page.getByRole('navigation', { name: '项目功能' }).getByRole('button', { name: '信息', exact: true }).click()
  const childRow = info.getByLabel('项目基本信息列表').locator('tbody tr').filter({ hasText: 'PRJ-REAL-001-1' })
  await expect(childRow.locator('td').last()).toHaveText('未启用')
  await expect(childRow.getByRole('button', { name: '查看铭牌 PRJ-REAL-001-1', exact: true })).toHaveCount(0)
  await expect(info.getByRole('button', { name: '查看铭牌 PRJ-REAL-001', exact: true })).toBeEnabled()
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('project overview renders five-stage progress and shipping countdown', async ({ page }, testInfo) => {
  await page.clock.setFixedTime(new Date('2026-09-18T12:00:00+08:00'))
  await page.route('**/api/quality/validation-plan-approval-tasks/mine', route => route.fulfill({ json: [] }))
  const overviewProject = { id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: 'engineer', stage: 'Design', vaultLocation: 'D:\\PDM\\PRJ-REAL-001', releaseLocation: 'D:\\Release\\PRJ-REAL-001', isActive: true, organizationId: 'org-ks', quantity: 1, serialNumbers: ['70000001'], executionUnitName: '自动化事业部', primaryProjectManager: 'engineer', collaborativeProjectManagers: [], designLead: 'engineer', designers: ['engineer'], phaseOwners: { StandardProcurement: 'engineer' }, canManageMainStaffing: true, canAssignDesigners: true }
  await page.route('**/api/projects', route => route.fulfill({ json: [overviewProject] }))
  await page.route(`**/api/projects/${projectId}`, route => route.fulfill({ json: overviewProject }))
  let isLagging = false
  let isAtRisk = false
  await page.route(`**/api/projects/${projectId}/plan/portfolio`, route => route.fulfill({ json: {
    rootProjectId: projectId, currentStage: 'Design', completionPercent: 62, laggingProjectCount: 0, riskProjectCount: 0, plannedStart: '2026-08-01', plannedFinish: '2026-10-18',
    projects: [{ projectId, projectCode: 'PRJ-REAL-001', projectName: '真实装配项目', isRoot: true, hasPlan: true, currentStage: 'Design', completionPercent: 62, plannedFinish: '2026-10-18', isLagging, isAtRisk, plan: {
      id: 'plan-1', projectId, currentStage: 'Design', plannedStart: '2026-08-01', plannedFinish: '2026-10-18', approvalStatus: 'Approved', stages: [{ code: 'Design', name: '设计' }], tasks: [
        { id: 'task-drawing', name: '完成图纸审核', stage: 'Design', assignee: 'engineer', plannedStart: '2026-09-16', plannedFinish: '2026-09-18', actualStart: isLagging || isAtRisk ? '2026-09-16' : undefined, actualFinish: isLagging || isAtRisk ? '2026-09-17' : undefined, completionPercent: 40, status: 'InProgress', predecessorTaskIds: [], weight: 1, isMilestone: false, isRequired: true, sortOrder: 1, durationDays: 3 },
        { id: 'task-bom', name: '标准件BOM核对', stage: 'Design', assignee: 'engineer', plannedStart: '2026-09-17', plannedFinish: '2026-09-19', completionPercent: 0, status: 'NotStarted', predecessorTaskIds: [], weight: 1, isMilestone: false, isRequired: true, sortOrder: 2, durationDays: 3 },
        { id: 'task-material', name: '备料主任务', stage: 'MaterialPreparation', plannedStart: '2026-09-17', plannedFinish: '2026-09-19', completionPercent: 100, status: 'Completed', predecessorTaskIds: [], weight: 1, isMilestone: false, sortOrder: 3, durationDays: 3 },
      ],
    } }],
  } }))
  await page.route(`**/api/projects/${projectId}/validation-plan`, route => route.fulfill({ json: { id: 'validation-1', projectId, revisionNumber: 1, state: 'PendingApproval', items: [], approvalTasks: [], attachments: [], createdBy: 'engineer', createdAt: '2026-09-10', updatedBy: 'engineer', updatedAt: '2026-09-10', rowVersion: 1 } }))
  await page.route(`**/api/material-relations/projects/${projectId}/completeness`, route => route.fulfill({ json: { projectId, isComplete: false, mainMaterialCount: 2, incompleteGroupCount: 2, mainMaterials: [] } }))
  await page.route(`**/api/projects/${projectId}/procurement-tracking`, route => route.fulfill({ json: { projectId, projectCode: 'PRJ-REAL-001', hasPublishedBom: true, items: [
    { sequence: 1, projectCode: 'PRJ-REAL-001', materialCode: 'MAT-001', materialName: '关键传感器', impactStage: 'Assembly', quantity: 1, bomKind: 'Electrical', purchaseRequisitionNumbers: [], purchaseRequisitionStatus: '未请购', purchaseOrderNumbers: [], purchaseOrderStatus: '未采购', purchaseQuantity: 0, arrivedQuantity: 0, details: [] },
    { sequence: 2, projectCode: 'PRJ-REAL-001', materialCode: 'MAT-002', materialName: '普通紧固件', impactStage: null, quantity: 2, bomKind: 'Standard', purchaseRequisitionNumbers: [], purchaseRequisitionStatus: '未请购', purchaseOrderNumbers: [], purchaseOrderStatus: '未采购', purchaseQuantity: 0, arrivedQuantity: 0, details: [] },
  ] } }))
  await page.route(`**/api/projects/${projectId}/manager-notes`, route => route.fulfill({ json: { id: 'note-1', actor: 'engineer', action: 'project.manager-note', entityType: 'Project', entityId: projectId, detail: '风险已同步', occurredAt: '2026-09-25T09:00:00Z' } }))
  let todoRequest: { content?: string; dueDate?: string; recipientUsernames?: string[] } | undefined
  await page.route(`**/api/projects/${projectId}/todos`, route => {
    todoRequest = route.request().postDataJSON()
    return route.fulfill({ json: [] })
  })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.addInitScript(() => { window.setInterval = (() => 0) as unknown as typeof window.setInterval })
  await page.setViewportSize({ width: 1925, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)

  const overview = page.getByLabel('工作台主页面')
  await expect(overview.getByLabel('项目状态与下一步')).toContainText('设计阶段')
  await expect(overview.getByLabel('项目状态与下一步')).toContainText('项目进度 62%')
  await expect(overview.getByLabel('项目状态与下一步')).toContainText('2026/10/18')
  await expect(overview.getByLabel('三行项目进度')).toContainText('设计')
  await expect(overview.getByLabel('实际进度')).toContainText('未填报实际日期')
  await expect(overview.getByLabel('当前计划').locator('button').first()).toHaveAttribute('title', /2026-09-16 — 2026-09-19/)
  await expect(overview.getByLabel('初始基线')).toContainText('未建立基线')
  await expect(overview.locator('.pdm-overview-timeline__legend')).toHaveCount(0)
  await expect(overview.getByLabel('每日时间轴')).toHaveCount(0)
  const timeAxis = overview.getByLabel('计划时间刻度')
  expect(await timeAxis.locator('time').evaluateAll(ticks => ticks.map(tick => tick.getAttribute('datetime')))).toEqual(['2026-08-01', '2026-08-08', '2026-08-15', '2026-08-22', '2026-08-29', '2026-09-05', '2026-09-12', '2026-09-19', '2026-09-26', '2026-10-03', '2026-10-10', '2026-10-17', '2026-10-18'])
  await page.setViewportSize({ width: 1518, height: 855 })
  const tickBoxes = await timeAxis.locator('time').evaluateAll(ticks => ticks.filter(tick => tick.textContent).map(tick => ({ left: tick.getBoundingClientRect().left, right: tick.getBoundingClientRect().right })))
  expect(tickBoxes.every((box, index) => index === 0 || box.left >= tickBoxes[index - 1]!.right + 4)).toBe(true)
  const axisBox = await timeAxis.locator('.pdm-overview-timeline__axis').boundingBox()
  const trackBox = await overview.getByLabel('当前计划').locator('.pdm-overview-timeline__track').boundingBox()
  expect(Math.abs(axisBox!.x - trackBox!.x)).toBeLessThanOrEqual(1)
  expect(Math.abs(axisBox!.width - trackBox!.width)).toBeLessThanOrEqual(1)
  await overview.getByLabel('项目状态与下一步').screenshot({ path: testInfo.outputPath('timeline-date-scale.png') })
  await page.setViewportSize({ width: 1925, height: 1114 })
  await expect(overview.locator('.pdm-overview-timeline__day-line')).toHaveCount(0)
  await expect(overview.getByLabel('三行项目进度')).not.toContainText('今天')
  await expect(overview.locator('.pdm-overview-timeline__milestone')).toHaveCount(0)
  const dateArrows = overview.locator('.pdm-overview-timeline__today-arrow')
  await expect(dateArrows).toHaveCount(1)
  await expect(overview.getByLabel('实际进度').locator('.pdm-overview-timeline__today-arrow')).toHaveCount(0)
  for (const arrow of await dateArrows.all()) {
    await expect(arrow).toHaveClass(/is-success/)
    await expect(arrow).toHaveAttribute('title', '2026-09-18 · 正常')
    expect(await arrow.evaluate(element => getComputedStyle(element).borderBottomColor)).toBe('rgb(34, 163, 90)')
  }
  await overview.getByLabel('项目状态与下一步').screenshot({ path: testInfo.outputPath('timeline-no-actual-arrow.png') })
  await expect(overview.locator('.pdm-overview-timeline__truck')).toHaveCount(1)
  isLagging = true
  await overview.getByRole('button', { name: '刷新项目概览' }).click()
  const speeder = overview.getByRole('img', { name: '已滞后，货车行驶中' })
  await expect(speeder).toBeVisible()
  await expect(dateArrows).toHaveCount(2)
  const arrowPositions = await dateArrows.evaluateAll(elements => elements.map(element => element.getBoundingClientRect().x))
  expect(arrowPositions[0]).toBe(arrowPositions[1])
  const actualBar = await overview.getByLabel('实际进度').locator('.pdm-overview-timeline__segment').boundingBox()
  const plannedDesignBar = await overview.getByLabel('当前计划').locator('.pdm-overview-timeline__segment').first().boundingBox()
  expect(actualBar!.x).toBeCloseTo(plannedDesignBar!.x, 0)
  expect(actualBar!.width / plannedDesignBar!.width).toBeCloseTo(.2, 2)
  const speederBox = await speeder.boundingBox()
  expect(speederBox!.x).toBeGreaterThan(actualBar!.x + actualBar!.width)
  expect(speederBox!.height).toBeLessThanOrEqual(27)
  expect(Math.abs(speederBox!.y + speederBox!.height / 2 - actualBar!.y - actualBar!.height / 2)).toBeLessThanOrEqual(.5)
  expect(await speeder.locator('.pdm-overview-truck__body').evaluate(element => getComputedStyle(element).animationName)).toBe('pdm-truck-motion')
  await overview.getByLabel('项目状态与下一步').screenshot({ path: testInfo.outputPath('timeline-truck.png') })
  await page.emulateMedia({ reducedMotion: 'reduce' })
  expect(await speeder.locator('.pdm-overview-truck__body').evaluate(element => getComputedStyle(element).animationName)).toBe('none')
  await page.emulateMedia({ reducedMotion: 'no-preference' })
  isAtRisk = true
  isLagging = false
  await overview.getByRole('button', { name: '刷新项目概览' }).click()
  await expect(overview.getByRole('img', { name: '有风险，货车行驶中' })).toBeVisible()
  await overview.getByLabel('项目状态与下一步').screenshot({ path: testInfo.outputPath('timeline-truck-risk.png') })
  isAtRisk = false
  await overview.getByRole('button', { name: '刷新项目概览' }).click()
  await expect(overview.getByRole('button', { name: '刷新项目概览' })).toBeEnabled()
  await expect(overview.getByLabel('项目待办与风险').locator('.pdm-overview-alerts > *')).toHaveCount(3)
  await expect(overview.getByRole('button', { name: '查看关键物料' })).toHaveCount(0)
  await expect(overview.getByLabel('项目核心业务概览').locator('article')).toHaveCount(2)
  await expect(overview.getByLabel('当前阶段任务')).toHaveCount(0)
  const portfolio = overview.getByLabel('项目总览')
  await expect(portfolio.locator('thead th')).toHaveText(['项目', '执行', '阶段', '计划完成', '剩余工期', '进度', '负责人', '当前', '状态', '备注'])
  const portfolioRow = portfolio.locator('tbody tr').first()
  const portfolioProgress = portfolioRow.locator('.pdm-project-portfolio__progress')
  await expect(portfolioProgress.locator(':scope > em')).toHaveText('62%')
  expect(await portfolioProgress.evaluate(element => getComputedStyle(element).gridTemplateRows)).toBe('14px 10px')
  const [progressValue, progressBar] = await Promise.all([portfolioProgress.locator(':scope > em').boundingBox(), portfolioProgress.locator(':scope > i').boundingBox()])
  expect(progressValue!.y).toBeLessThan(progressBar!.y)
  await expect(portfolioRow.locator('td').nth(1)).toHaveText('真实工程师')
  await expect(portfolioRow.locator('td').nth(3)).toHaveText('2026/09/19')
  await expect(portfolioRow.locator('td').nth(3)).toHaveAttribute('title', /当前主任务“设计”/)
  await expect(portfolioRow.locator('td').nth(4)).toHaveText(/^(逾期 \d+ 天|剩余 \d+ 天)$/)
  await expect(portfolioRow.locator('td').nth(7)).toContainText('完成图纸审核')
  await expect(portfolioRow.locator('td').nth(7)).toHaveAttribute('title', /完成图纸审核/)
  const manageRecord = portfolio.getByRole('button', { name: '维护 PRJ-REAL-001 的记录' })
  await expect(manageRecord).toBeVisible()
  await manageRecord.click()
  const managerNoteDialog = page.getByRole('dialog', { name: '维护项目记录 · PRJ-REAL-001' })
  await expect(managerNoteDialog).toBeVisible()
  await managerNoteDialog.getByLabel('项目备注内容').fill('风险已同步')
  await managerNoteDialog.getByRole('button', { name: '保存记录' }).click()
  await expect(managerNoteDialog).toBeHidden()
  await manageRecord.click()
  await expect(managerNoteDialog).toBeVisible()
  await managerNoteDialog.getByLabel('项目备注内容').fill('确认客户现场准备情况')
  await managerNoteDialog.getByLabel('待办截止日期').fill('2026-10-01')
  await managerNoteDialog.locator('.pdm-project-todo-settings .el-select__wrapper').click()
  await page.getByRole('option', { name: /真实工程师/ }).click()
  await managerNoteDialog.getByRole('button', { name: '保存记录' }).click()
  await expect(managerNoteDialog).toBeHidden()
  expect(todoRequest).toEqual({ content: '确认客户现场准备情况', dueDate: '2026-10-01', recipientUsernames: ['engineer'] })
  await expect(page.getByLabel('工作台主页面')).toBeVisible()
  expect(await portfolio.locator('thead th').last().evaluate(cell => cell.getBoundingClientRect().right <= cell.closest('.pdm-project-portfolio__table-wrap')!.getBoundingClientRect().right)).toBe(true)
  expect(await portfolio.locator('thead th, tbody tr:not(.is-empty) td').evaluateAll(cells => cells.every(cell => getComputedStyle(cell).textAlign === 'center'))).toBe(true)
  const notesLayout = await portfolioRow.locator('td').last().evaluate(cell => {
    const button = cell.querySelector('button')!
    const cellBox = cell.getBoundingClientRect()
    const buttonBox = button.getBoundingClientRect()
    return { height: Math.round(cellBox.height), rightInset: Math.round(cellBox.right - buttonBox.right) }
  })
  expect(notesLayout.height).toBeLessThanOrEqual(44)
  expect(notesLayout.rightInset).toBeGreaterThanOrEqual(5)
  await expect(overview.getByLabel('项目团队')).toContainText('真实工程师')
  expect(await overview.getByLabel('项目团队').evaluate(panel => panel.parentElement?.classList.contains('pdm-overview-summary'))).toBe(true)
  expect(await portfolio.evaluate(panel => panel.getBoundingClientRect().top)).toBeLessThanOrEqual(await overview.getByLabel('项目团队').evaluate(panel => panel.getBoundingClientRect().top))
  for (const phase of ['标准件采购', '非标件采购', '非标件生产', '机械装配', '电气装配', '电气调试', '验收']) await expect(overview.getByLabel('项目团队')).toContainText(phase)
  await expect(overview.getByLabel('项目团队')).toContainText('标准件采购真实工程师')
  await expect(overview.getByLabel('项目团队').locator('thead th')).toHaveText(['职责', '负责人', '职责', '负责人'])
  await expect(overview.getByLabel('项目团队').locator('tbody tr')).toHaveCount(5)
  await expect(overview.getByRole('button', { name: '配置团队' })).toBeVisible()
  await page.setViewportSize({ width: 1537, height: 889 })
  const statusPanel = overview.getByLabel('项目状态与下一步')
  const statusBounds = async () => {
    const panel = (await statusPanel.boundingBox())!
    const lastAlert = (await overview.getByLabel('项目待办与风险').locator('.pdm-overview-alerts > :last-child').boundingBox())!
    return { panel, lastAlert }
  }
  for (const width of [1537, 1285]) {
    await page.setViewportSize({ width, height: 889 })
    const { panel, lastAlert } = await statusBounds()
    const alerts = (await overview.getByLabel('项目待办与风险').boundingBox())!
    const team = (await overview.getByLabel('项目团队').boundingBox())!
    const reserved = (await overview.getByLabel('预留区域').boundingBox())!
    const containerGap = await overview.locator('.pdm-overview-layout').evaluate(element => parseFloat(getComputedStyle(element).rowGap))
    expect(Math.abs(team.height - reserved.height)).toBeLessThanOrEqual(1)
    expect(Math.abs(reserved.y - team.y - team.height - containerGap)).toBeLessThanOrEqual(1)
    await expect(overview.getByLabel('预留区域')).toBeEmpty()
    const teamCells = overview.getByLabel('项目团队').locator('th, td')
    expect(await teamCells.evaluateAll(cells => cells.every(cell => getComputedStyle(cell).textAlign === 'center' && getComputedStyle(cell).verticalAlign === 'middle'))).toBe(true)
    const teamRows = await overview.getByLabel('项目团队').locator('tr').evaluateAll(rows => rows.map(row => row.getBoundingClientRect().height))
    expect(Math.max(...teamRows) - Math.min(...teamRows)).toBeLessThanOrEqual(1)
    expect(Math.abs(alerts.x - team.x)).toBeLessThanOrEqual(2)
    expect(Math.abs(alerts.x + alerts.width - team.x - team.width)).toBeLessThanOrEqual(2)
    const left = (await overview.getByLabel('项目总览').boundingBox())!
    expect(Math.abs(panel.x - left.x)).toBeLessThanOrEqual(1)
    expect(Math.abs(panel.width - left.width)).toBeLessThanOrEqual(1)
    expect(lastAlert.x + lastAlert.width).toBeLessThanOrEqual(alerts.x + alerts.width + 1)
    expect(lastAlert.y + lastAlert.height).toBeLessThanOrEqual(panel.y + panel.height + 1)
  }
  await page.setViewportSize({ width: 1537, height: 889 })
  const teamActions = overview.getByLabel('项目团队').locator('.pdm-overview-team__actions')
  expect(await teamActions.evaluate(element => getComputedStyle(element).display)).toBe('flex')
  await expect(teamActions.getByRole('button', { name: '配置团队' })).toBeVisible()
  expect(await portfolio.locator('thead th').last().evaluate(cell => cell.getBoundingClientRect().right <= cell.closest('.pdm-project-portfolio__table-wrap')!.getBoundingClientRect().right)).toBe(true)
  expect(await portfolioRow.locator('.pdm-project-portfolio__overdue').evaluate(element => getComputedStyle(element).textAlign)).toBe('center')
  await page.screenshot({ path: join(tmpdir(), 'pdm-overview-1537x889.png') })
  await page.route('**/api/projects/*/contact-information', route => route.fulfill({ json: { information: { projectId, entries: [], salesPeople: '', presalesPeople: '', rowVersion: 0 }, customerName: '', canEdit: false } }))
  await page.route('**/api/projects/*/nameplate', route => route.fulfill({ json: { nameplate: { enabled: false }, canEdit: false } }))
  await teamActions.getByRole('button', { name: '配置团队' }).click()
  await expect(page.getByLabel('项目基本信息列表')).toBeVisible()
  await expect(page.getByRole('heading', { name: '人员信息', exact: true })).toBeVisible()
  await expect(page.getByRole('dialog', { name: '配置项目团队 · PRJ-REAL-001' })).toHaveCount(0)
  await page.getByRole('button', { name: '概览', exact: true }).click()
  await expect(overview.getByLabel('项目位置')).toHaveCount(0)
  const overviewBox = await overview.boundingBox()
  expect(overviewBox).not.toBeNull()
  expect((overviewBox?.y ?? 0) + (overviewBox?.height ?? 0)).toBeLessThanOrEqual(page.viewportSize()!.height + 1)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('project-overview-command-center.png') })
  expect(errors).toEqual([])
})

test('procurement BOM kind filter stays on one line and filters all three kinds', async ({ page }, testInfo) => {
  await page.route(`**/api/projects/${projectId}/procurement-tracking`, route => route.fulfill({ json: {
    projectId, projectCode: 'PRJ-REAL-001', hasPublishedBom: true, lastSuccessfulRefreshAt: '2026-09-17T12:00:00Z', items: [
      { sequence: 1, projectCode: 'PRJ-REAL-001', materialCode: 'STD-01', materialName: '标准紧固件', brand: 'UPTON', quantity: 1, bomKind: 'Standard', purchaseRequisitionNumbers: [], purchaseRequisitionStatus: '未请购', purchaseOrderNumbers: [], purchaseOrderStatus: '未采购', purchaseQuantity: 0, arrivedQuantity: 0, details: [] },
      { sequence: 2, projectCode: 'PRJ-REAL-001', materialCode: 'NONSTD-01', materialName: '非标底板', brand: 'UPTON', quantity: 1, bomKind: 'NonStandard', purchaseRequisitionNumbers: [], purchaseRequisitionStatus: '未请购', purchaseOrderNumbers: [], purchaseOrderStatus: '未采购', purchaseQuantity: 0, arrivedQuantity: 0, details: [] },
      { sequence: 3, projectCode: 'PRJ-REAL-001', materialCode: 'ELEC-01', materialName: '电气传感器', brand: 'UPTON', quantity: 1, bomKind: 'Electrical', purchaseRequisitionNumbers: [], purchaseRequisitionStatus: '未请购', purchaseOrderNumbers: [], purchaseOrderStatus: '未采购', purchaseQuantity: 0, arrivedQuantity: 0, details: [] },
    ],
  } }))
  await page.route('**/api/quality/validation-plan-approval-tasks/mine', route => route.fulfill({ json: [] }))
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(`${message.text()} ${message.location().url}`) })
  await page.addInitScript(() => { window.setInterval = (() => 0) as unknown as typeof window.setInterval })
  await page.setViewportSize({ width: 1920, height: 1080 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: '备料', exact: true }).click()

  const kindFilter = page.getByLabel('筛选物料分类')
  const procurement = page.getByLabel('采购跟踪', { exact: true })
  expect(await procurement.locator('.procurement-tracking__heading button, .procurement-tracking__pagination button').evaluateAll(buttons => buttons.filter(button => Math.abs(button.getBoundingClientRect().width - (button.classList.contains('procurement-tracking__multi-filter') ? 80 : 60)) >= 1).map(button => ({ text: button.textContent, width: button.getBoundingClientRect().width })))).toEqual([])
  expect(await procurement.locator('.procurement-tracking__brand-filter, .procurement-tracking__kind-filter').evaluateAll(controls => controls.map(control => Math.round(control.getBoundingClientRect().width)))).toEqual([80, 80])
  await expect(procurement.locator('.procurement-tracking__filters [aria-hidden="true"]')).toHaveCount(0)
  expect(await procurement.locator('.procurement-tracking__heading input:not([type=checkbox]), .procurement-tracking__heading select, .procurement-tracking__heading button, .procurement-tracking__delay-filter').evaluateAll(controls => controls.filter(control => getComputedStyle(control).fontSize !== '11px').map(control => ({ tag: control.tagName, type: control.getAttribute('type'), font: getComputedStyle(control).fontSize })))).toEqual([])
  await expect(kindFilter.locator('option')).toHaveText(['全部分类', '标准件', '非标件', '电气件'])
  const layout = await page.locator('.procurement-tracking__heading').evaluate(heading => {
    const filters = heading.querySelector<HTMLElement>('.procurement-tracking__filters')!
    const controls = [...heading.querySelectorAll<HTMLElement>('.procurement-tracking__filters > :not(datalist), .procurement-tracking__actions > *, .procurement-tracking__updated')]
      .filter(element => element.getBoundingClientRect().width > 0)
    const centers = controls.map(element => {
      const rect = element.getBoundingClientRect()
      return Math.round(rect.top + rect.height / 2)
    })
    return {
      filterWrap: getComputedStyle(filters).flexWrap,
      headingOverflow: heading.scrollWidth - heading.clientWidth,
      centerSpread: Math.max(...centers) - Math.min(...centers),
    }
  })
  expect(layout.filterWrap).toBe('nowrap')
  expect(layout.headingOverflow).toBeLessThanOrEqual(1)
  expect(layout.centerSpread).toBeLessThanOrEqual(1)

  await procurement.getByRole('button', { name: '列设置', exact: true }).click()
  const columnSettings = page.getByRole('dialog', { name: '采购跟踪列设置' })
  await expect(columnSettings).toBeVisible()
  expect(await columnSettings.locator('.el-dialog__footer .el-button').evaluateAll(buttons => buttons.map(button => Math.round(button.getBoundingClientRect().width)))).toEqual([60, 60, 60])
  await columnSettings.getByRole('button', { name: '取消', exact: true }).click()
  await expect(columnSettings).toBeHidden()
  await kindFilter.selectOption('非标件')
  await expect(page.getByText('NONSTD-01', { exact: true })).toBeVisible()
  await expect(page.getByText('STD-01', { exact: true })).toHaveCount(0)
  await expect(page.getByText('ELEC-01', { exact: true })).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('procurement-kind-filter-one-line.png'), fullPage: false })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('wear-part BOM aggregates categories and stays outside release', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.addInitScript(() => { window.setInterval = (() => 0) as unknown as typeof window.setInterval })
  await page.setViewportSize({ width: 1988, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: /易损件BOM/ }).click()

  await expect(page.getByRole('tab', { name: '易损件BOM（2）' })).toHaveAttribute('aria-selected', 'true')
  const summary = page.getByLabel('易损件BOM统计说明')
  await expect(summary).toContainText('统计物料2 种')
  await expect(summary).toContainText('结构实例3 条')
  await expect(summary).toContainText('合计数量12')
  await expect(summary).toContainText('不参与审批、版本、发布包及 U9C')
  await expect(page.getByRole('button', { name: '发起发布' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: '保存BOM' })).toHaveCount(0)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('wear-part-bom-statistics.png'), fullPage: true })
  expect(errors).toEqual([])
})

test('released BOM rows expose linked 2D and 3D preview downloads', async ({ page }, testInfo) => {
  const releasedDocuments = [
    { id: 'doc-root', projectId, folderId: 'folder-main-mechanical', drawingNumber: 'REAL-ASM-001', name: '真实总装配', fileName: 'REAL-ASM-001.SLDASM', kind: 0, lifecycleState: 2, revision: { display: 'W2' }, storedVersionCount: 2, checkedOutBy: null },
    { id: 'doc-drawing', projectId, folderId: 'folder-main-mechanical', drawingNumber: 'REAL-ASM-001', name: '真实总装工程图', fileName: 'REAL-ASM-001.SLDDRW', kind: 2, lifecycleState: 2, revision: { display: 'W2' }, storedVersionCount: 2, checkedOutBy: null },
  ]
  await page.route(`**/api/projects/${projectId}/folder-documents`, route => route.fulfill({ json: releasedDocuments }))
  await page.route(`**/api/projects/${projectId}/bom-source-data`, route => route.fulfill({ json: [{
    id: 'source-non-standard-1', kind: 'NonStandard', sequence: 1, drawingNumber: 'REAL-ASM-001', name: '真实总装配', quantity: 1, unit: '件', material: 'Q235B', specification: '总装', heatTreatment: '调质', revision: 'W2', isComplete: true, source: 'Auto', sourceDocumentId: 'doc-root', isWearPart: false, isManuallyOverridden: false, isPendingRemoval: false,
  }] }))
  await page.route(`**/api/projects/${projectId}/boms/NonStandard`, route => route.fulfill({ json: [{
    id: 'bom-non-standard-1', kind: 'NonStandard', sequence: 1, drawingNumber: 'REAL-ASM-001', name: '真实总装配', quantity: 1, unit: '件', material: 'Q235B', specification: '总装', heatTreatment: '淬火', revision: 'W2', isComplete: true, source: 'Auto', sourceDocumentId: 'doc-root', isWearPart: false, isManuallyOverridden: false, isPendingRemoval: false,
  }] }))
  await page.route(`**/api/projects/${projectId}/release-packages`, route => route.fulfill({ json: [{
    id: 'package-non-standard-w2', number: 'RP-REAL-001', state: 5, scope: 6, publishedAt: '2026-09-16T01:00:00Z', approvalTasks: [],
  }] }))
  await page.route(`**/api/projects/${projectId}/drawing-review-candidates`, route => route.fulfill({ json: [{
    candidateId: 'review-bom-non-standard-1', bomItemId: 'bom-non-standard-1', modelDocumentId: 'doc-root', drawingDocumentId: 'doc-drawing', drawingNumber: 'REAL-ASM-001', name: '真实总装配', bomKinds: ['NonStandard'], modelRevision: 'W2', drawingRevision: 'W2', state: 'Ready', reason: '当前工程图尚未发起审核', selectable: true,
  }] }))
  await page.route('**/api/documents/doc-drawing/versions', route => route.fulfill({ json: [{
    id: 'drawing-release-w2', documentId: 'doc-drawing', revision: { display: 'W2' }, status: 1, fileLength: 100, sha256: 'D'.repeat(64), createdBy: 'engineer', createdAt: '2026-09-16T01:00:00Z', changeNote: '发布',
    preview: { format: 1, storageRelativePath: '.release-previews/drawing.pdf', fileLength: 80, sha256: 'P'.repeat(64), sourceSha256: 'D'.repeat(64) },
  }] }))
  await page.route('**/api/documents/doc-drawing/versions/drawing-release-w2/preview', route => route.fulfill({ contentType: 'application/pdf', body: '%PDF-1.4\n%%EOF' }))

  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.addInitScript(() => { window.setInterval = (() => 0) as unknown as typeof window.setInterval })
  await page.setViewportSize({ width: 1925, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()

  const table = page.locator('.pdm-bom-table')
  await page.getByRole('tab', { name: '源数据（1）' }).click()
  await expect(page.getByRole('tab', { name: '源数据（1）' })).toHaveAttribute('aria-selected', 'true')
  await expect(table.getByRole('columnheader', { name: '热处理', exact: true })).toBeVisible()
  await expect(table.locator('tbody tr').first()).toContainText('调质')
  await expect(table.getByRole('button', { name: '编辑热处理' })).toHaveCount(0)
  await page.screenshot({ path: join(tmpdir(), `pdm-bom-source-heat-treatment-${Date.now()}.png`) })

  await page.getByRole('tab', { name: /非标件BOM/ }).click()

  await expect(table.getByRole('columnheader', { name: '图纸', exact: true })).toBeVisible()
  await expect(table.getByRole('columnheader', { name: '热处理', exact: true })).toBeVisible()
  await expect(table.getByRole('button', { name: '编辑热处理' })).toHaveText('淬火')
  await expect(table.getByRole('button', { name: '预览2D图纸 REAL-ASM-001' })).toBeVisible()
  await expect(table.getByRole('button', { name: '下载3D图纸 REAL-ASM-001' })).toBeVisible()
  expect(await table.locator('tbody tr').first().evaluate(element => getComputedStyle(element).height)).toBe('30px')
  await page.screenshot({ path: testInfo.outputPath('non-standard-drawing-review-warning.png'), fullPage: false })

  let downloaded = false
  page.on('download', () => { downloaded = true })
  await table.getByRole('button', { name: '预览2D图纸 REAL-ASM-001' }).click()
  const preview = page.getByRole('dialog', { name: '2D图纸预览 · REAL-ASM-001' })
  await expect(preview.getByRole('link', { name: '下载 PDF' })).toHaveAttribute('download', 'REAL-ASM-001.pdf')
  await expect(preview.getByRole('region', { name: 'PDF图纸预览' })).toBeVisible()
  expect(downloaded).toBe(false)
  await preview.getByRole('button', { name: '关闭此对话框' }).click()

  await page.getByRole('tab', { name: /标准件BOM/ }).click()
  await expect(table.getByRole('columnheader', { name: '热处理', exact: true })).toHaveCount(0)
  await expect(page.getByText('查看版本', { exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: '对比发布', exact: true })).toBeVisible()
  const filterRows = await page.locator('.pdm-bom-filters > *').evaluateAll(elements =>
    elements.map(element => Math.round(element.getBoundingClientRect().top)),
  )
  expect(new Set(filterRows).size).toBe(1)
  const filterWidth = await page.locator('.pdm-bom-filters').evaluate(element => ({
    clientWidth: element.clientWidth,
    scrollWidth: element.scrollWidth,
  }))
  expect(filterWidth.scrollWidth).toBeLessThanOrEqual(filterWidth.clientWidth)
  await page.getByRole('button', { name: '列设置', exact: true }).click()
  const columnDialog = page.getByRole('dialog', { name: 'BOM列设置' })
  const heatTreatmentCheckbox = columnDialog.getByRole('checkbox', { name: '热处理' })
  await expect(heatTreatmentCheckbox).not.toBeChecked()
  await columnDialog.locator('label.el-checkbox').filter({ hasText: '热处理' }).click()
  await expect(heatTreatmentCheckbox).toBeChecked()
  await columnDialog.getByRole('button', { name: '保存', exact: true }).click()
  await expect(table.getByRole('columnheader', { name: '热处理', exact: true })).toBeVisible()
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  await page.screenshot({ path: join(tmpdir(), `pdm-bom-drawing-links-${Date.now()}.png`) })
  expect(errors).toEqual([])
})

test('related materials use a scoped right drawer for electrical BOM', async ({ page }, testInfo) => {
  await page.route(`**/api/projects/${projectId}/boms/Electrical`, route => route.fulfill({ json: [
    { id: 'bom-electrical-1', kind: 'Electrical', sequence: 1, drawingNumber: 'REAL-EL-001', name: '真实传感器', quantity: 1, unit: '件', material: null, specification: 'PNP', revision: 'A', isComplete: false },
    { id: 'bom-electrical-unconfigured', kind: 'Electrical', sequence: 2, drawingNumber: 'REAL-EL-UNCONFIGURED', name: '无关联配置电气料', quantity: 1, unit: '件', material: null, specification: 'NPN', revision: 'A', isComplete: false },
  ] }))
  await page.route(`**/api/material-relations/projects/${projectId}/completeness`, route => route.fulfill({ json: {
    projectId, isComplete: false, mainMaterialCount: 2, incompleteGroupCount: 2,
    mainMaterials: [
      { mainBomItemId: 'bom-standard-1', mainMaterialCode: 'REAL-STD-001', mainMaterialName: '标准紧固件', mainQuantity: 4, templateId: 'standard-template', revisionId: 'standard-revision', revisionVersion: 1, isComplete: false, groups: [{ groupId: 'standard-group', groupName: '标准配件', isRequired: true, selectionMode: 'Single', maxSelection: 1, isComplete: false, status: '待核对', expectedQuantity: 0, actualQuantity: 0, selectedOptionIds: [], reviewDecision: null, options: [{ id: 'standard-option', materialId: 'standard-accessory', materialCode: 'STD-ACC-001', materialName: '标准附件', materialKind: 'Standard', unitCode: '001', quantityMode: 'PerMainQuantity', quantityPerSet: 1, isDefault: true, sortOrder: 1 }] }] },
      { mainBomItemId: 'bom-electrical-1', mainMaterialCode: 'REAL-EL-001', mainMaterialName: '真实传感器', mainQuantity: 1, templateId: 'electrical-template', revisionId: 'electrical-revision', revisionVersion: 2, isComplete: false, groups: [{ groupId: 'electrical-group', groupName: '传感器附件', isRequired: true, selectionMode: 'Single', maxSelection: 1, isComplete: false, status: '待核对', expectedQuantity: 0, actualQuantity: 0, selectedOptionIds: [], reviewDecision: null, options: [{ id: 'electrical-option', materialId: 'electrical-accessory', materialCode: 'EL-ACC-001', materialName: '电气附件', materialKind: 'Electrical', unitCode: '001', quantityMode: 'PerMainQuantity', quantityPerSet: 1, isDefault: true, sortOrder: 1, specification: 'PNP-M18', brand: 'UPTON', selectionAdvice: '优先用于高速工位' }] }] },
    ],
  } }))
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.addInitScript(() => { window.setInterval = (() => 0) as unknown as typeof window.setInterval })
  await page.setViewportSize({ width: 1925, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: /电气件BOM/ }).click()
  await page.getByRole('button', { name: '关联物料', exact: true }).click()

  const drawer = page.getByRole('dialog', { name: 'BOM 关联物料核对' })
  await expect(drawer).toBeVisible()
  const mainPane = drawer.getByLabel('主物料明细')
  const relationPane = drawer.getByLabel('关联物料明细')
  await expect(mainPane).toContainText('REAL-EL-001')
  await expect(mainPane).not.toContainText('REAL-EL-UNCONFIGURED')
  await expect(mainPane).not.toContainText('REAL-STD-001')
  await expect(mainPane).toContainText('共 1 条')
  const optionControl = relationPane.getByLabel('选择关联物料 EL-ACC-001')
  await expect(optionControl).toBeVisible()
  await expect(relationPane.getByRole('columnheader')).toHaveText(['选择', '名称', '型号', '品牌', '选型建议', '数量'])
  await expect(relationPane).toContainText('PNP-M18')
  await expect(relationPane).toContainText('UPTON')
  await expect(relationPane).toContainText('优先用于高速工位')
  await expect(relationPane.locator('.pdm-material-relation-preferred')).toHaveText('优先')
  await expect(relationPane.locator('header')).not.toContainText('REAL-EL-001')
  const noAccessoryButton = relationPane.getByRole('button', { name: '无需配套', exact: true })
  await expect(noAccessoryButton).toBeVisible()
  const noAccessoryReason = relationPane.getByLabel('REAL-EL-001 传感器附件 无需配套原因')
  const reopenButton = relationPane.getByRole('button', { name: '重新核对', exact: true })
  const clearButton = relationPane.getByRole('button', { name: '清除选择', exact: true })
  await expect(noAccessoryReason).toBeVisible()
  await expect(noAccessoryReason).toBeDisabled()
  await expect(reopenButton).toBeDisabled()
  await expect(clearButton).toBeVisible()
  await expect(clearButton).toBeDisabled()
  const noAccessoryBox = await noAccessoryButton.boundingBox()
  expect(Math.round(noAccessoryBox?.width ?? 0)).toBe(80)
  expect(Math.round(noAccessoryBox?.height ?? 0)).toBe(30)
  const actionBoxes = await Promise.all([reopenButton, noAccessoryButton, clearButton].map(button => button.boundingBox()))
  expect(actionBoxes.every(box => Math.round(box?.width ?? 0) === 80 && Math.round(box?.height ?? 0) === 30)).toBe(true)
  expect(new Set(actionBoxes.map(box => Math.round(box?.y ?? 0))).size).toBe(1)
  const groupTitle = relationPane.locator('.pdm-material-relation-group-title')
  expect(await groupTitle.evaluate(element => element.scrollWidth <= element.clientWidth && element.getBoundingClientRect().height <= 32)).toBe(true)
  const optionCell = optionControl.locator('xpath=..')
  const [optionControlBox, optionCellBox] = await Promise.all([optionControl.boundingBox(), optionCell.boundingBox()])
  expect(optionControlBox).not.toBeNull()
  expect(optionCellBox).not.toBeNull()
  expect(optionControlBox!.x).toBeGreaterThanOrEqual(optionCellBox!.x)
  expect(optionControlBox!.x + optionControlBox!.width).toBeLessThanOrEqual(optionCellBox!.x + optionCellBox!.width)
  expect(Math.abs((optionControlBox!.x + optionControlBox!.width / 2) - (optionCellBox!.x + optionCellBox!.width / 2))).toBeLessThanOrEqual(1)
  const relationCells = relationPane.locator('table th, table td')
  expect(await relationCells.evaluateAll(cells => cells.every(cell => getComputedStyle(cell).textAlign === 'center' && getComputedStyle(cell).verticalAlign === 'middle'))).toBe(true)
  const mainCells = mainPane.locator('table th, table td')
  expect(await mainCells.evaluateAll(cells => cells.every(cell => getComputedStyle(cell).textAlign === 'center' && getComputedStyle(cell).verticalAlign === 'middle'))).toBe(true)
  expect(await relationPane.locator('.pdm-material-relation-option-list').evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
  await optionControl.check()
  await expect(clearButton).toBeEnabled()
  await clearButton.click()
  await expect(optionControl).not.toBeChecked()
  await expect(clearButton).toBeDisabled()
  await noAccessoryButton.click()
  await page.getByRole('button', { name: '确认无需配套', exact: true }).click()
  await expect(noAccessoryReason).toBeEnabled()
  await expect(reopenButton).toBeEnabled()
  await expect(noAccessoryButton).toBeDisabled()
  await reopenButton.click()
  await expect(noAccessoryReason).toBeDisabled()
  await expect(noAccessoryButton).toBeEnabled()
  await expect.poll(async () => {
    const box = await drawer.boundingBox()
    return (box?.x ?? 0) + (box?.width ?? 0)
  }).toBeLessThanOrEqual(page.viewportSize()!.width + 1)
  const [mainBox, relationBox] = await Promise.all([mainPane.boundingBox(), relationPane.boundingBox()])
  const drawerBox = await drawer.boundingBox()
  expect(mainBox).not.toBeNull()
  expect(relationBox).not.toBeNull()
  expect(drawerBox).not.toBeNull()
  expect(Math.abs((mainBox?.width ?? 0) - (relationBox?.width ?? 0))).toBeLessThanOrEqual(2)
  expect((drawerBox?.x ?? 0) + (drawerBox?.width ?? 0)).toBeLessThanOrEqual(page.viewportSize()!.width + 1)
  expect(await mainPane.locator('tbody td').first().evaluate(element => getComputedStyle(element).fontSize)).toBe('12px')
  expect(await relationPane.locator('tbody td').first().evaluate(element => getComputedStyle(element).fontSize)).toBe('12px')
  await expect(drawer.getByRole('button', { name: '保存核对结果' })).toBeInViewport()
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('electrical-related-material-drawer.png') })
  expect(errors).toEqual([])
})

test('related material candidates remain scrollable within many groups', async ({ page }, testInfo) => {
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [] }))
  const groups = Array.from({ length: 8 }, (_, groupIndex) => ({
    groupId: `scroll-group-${groupIndex}`, groupName: groupIndex === 0 ? '驱动器' : `配件组${groupIndex + 1}`,
    isRequired: true, selectionMode: 'Single', maxSelection: 1, isComplete: false, status: '待核对',
    expectedQuantity: 0, actualQuantity: 0, selectedOptionIds: [], reviewDecision: null,
    options: Array.from({ length: groupIndex === 0 ? 12 : 3 }, (_, optionIndex) => ({
      id: `scroll-option-${groupIndex}-${optionIndex}`, materialId: `scroll-material-${groupIndex}-${optionIndex}`,
      materialCode: `SCROLL-${groupIndex}-${optionIndex}`, materialName: `配件${groupIndex + 1}-${optionIndex + 1}`,
      materialKind: 'Electrical', unitCode: '001', quantityMode: 'PerMainQuantity', quantityPerSet: 1,
      isDefault: false, sortOrder: optionIndex + 1, specification: `MODEL-${optionIndex + 1}`, brand: 'UPTON',
    })),
  }))
  await page.route(`**/api/projects/${projectId}/boms/Electrical`, route => route.fulfill({ json: [
    { id: 'bom-electrical-1', kind: 'Electrical', sequence: 1, drawingNumber: 'REAL-EL-001', name: '伺服电机', quantity: 1, unit: '个', revision: 'A', isComplete: false },
  ] }))
  await page.route(`**/api/material-relations/projects/${projectId}/completeness`, route => route.fulfill({ json: {
    projectId, isComplete: false, mainMaterialCount: 1, incompleteGroupCount: groups.length,
    mainMaterials: [{ mainBomItemId: 'bom-electrical-1', mainMaterialCode: 'REAL-EL-001', mainMaterialName: '伺服电机', mainQuantity: 1,
      templateId: 'scroll-template', revisionId: 'scroll-revision', revisionVersion: 1, isComplete: false, groups }],
  } }))
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.route('**/assets/melon-jelly-*.html*', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html>' }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: /电气件BOM/ }).click()
  await page.getByRole('button', { name: '关联物料', exact: true }).click()
  const drawer = page.getByRole('dialog', { name: 'BOM 关联物料核对' })
  await expect(drawer).toBeVisible()
  await expect(drawer).toHaveCSS('transform', 'none')
  const candidateLists = drawer.locator('.pdm-material-relation-option-list')
  const firstList = candidateLists.first()
  await expect(firstList.locator('tbody tr')).toHaveCount(12)
  for (const viewport of [{ width: 1912, height: 1050 }, { width: 1280, height: 800 }]) {
    await page.setViewportSize(viewport)
    await firstList.evaluate(element => { element.scrollTop = 0 })
    // 鼠标滚轮必须能到达最后一项；仅在DOM中存在但被裁切不能算通过。
    await firstList.hover()
    await page.mouse.wheel(0, 1200)
    await expect.poll(() => firstList.evaluate(element => element.scrollTop)).toBeGreaterThan(0)
    const lastOption = firstList.getByLabel('选择关联物料 SCROLL-0-11', { exact: true })
    await expect(lastOption).toBeInViewport()
    await lastOption.check()
    await expect(lastOption).toBeChecked()
    const headerBox = await firstList.locator('th').first().boundingBox()
    const listBox = await firstList.boundingBox()
    expect(Math.abs(headerBox!.y - listBox!.y)).toBeLessThanOrEqual(2)
    await page.screenshot({ path: testInfo.outputPath(`related-candidate-options-${viewport.width}.png`) })
    const groupList = drawer.locator('.pdm-material-relation-groups')
    await groupList.evaluate(element => { element.scrollTop = element.scrollHeight })
    const finalOption = candidateLists.last().getByLabel('选择关联物料 SCROLL-7-2', { exact: true })
    await expect(finalOption).toBeInViewport()
    await finalOption.check()
    await expect(finalOption).toBeChecked()
    await expect(drawer.getByRole('button', { name: '保存核对结果', exact: true })).toBeInViewport()
    await expect(drawer.locator('.pdm-material-relation-workspace')).toBeVisible()
    await page.screenshot({ path: testInfo.outputPath(`related-candidate-scroll-${viewport.width}.png`) })
    await groupList.evaluate(element => { element.scrollTop = 0 })
  }
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('project settings keeps the confirmed project-copy scope', async ({ page }, testInfo) => {
  const failedResponses: string[] = []
  page.on('response', response => { if (response.status() >= 400) failedResponses.push(response.url()) })
  const targetId = '33333333-3333-3333-3333-333333333333'
  const sourceId = '44444444-4444-4444-4444-444444444444'
  const baseProject = { owner: 'admin', vaultLocation: 'D:\\PDM\\TEST', releaseLocation: 'D:\\Release\\TEST', isActive: true,
    quantity: 1, serialNumbers: [], collaborativeProjectManagers: [], designers: [], canReadContent: true }
  let previewBody: Record<string, unknown> | null = null
  await page.route('**/api/projects', route => route.fulfill({ json: [
    { ...baseProject, id: targetId, code: 'P700002', name: '新建目标项目' },
    { ...baseProject, id: sourceId, code: 'P700001', name: '现有源项目' },
  ] }))
  await page.route(new RegExp(`/api/projects/(?:${targetId}|${sourceId})$`), route => {
    const id = new URL(route.request().url()).pathname.split('/').at(-1)!
    return route.fulfill({ json: { ...baseProject, id, code: id === targetId ? 'P700002' : 'P700001', name: id === targetId ? '新建目标项目' : '现有源项目' } })
  })
  await page.route(`**/api/projects/${targetId}/**`, route => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/reference-tree')) return route.fulfill({ json: null })
    if (path.endsWith('/plan/portfolio')) return route.fulfill({ json: { projects: [], completionPercent: 0, plannedFinish: null } })
    if (path.endsWith('/validation-plan')) return route.fulfill({ json: null })
    if (path.endsWith('/procurement-tracking')) return route.fulfill({ json: { items: [] } })
    if (['/folders', '/documents', '/folder-documents', '/document-relations', '/boms/Standard', '/boms/NonStandard', '/boms/Unclassified', '/boms/Electrical', '/bom-source-data', '/boms/empty-declarations', '/bom-versions', '/bom-baselines', '/drawing-reviews', '/release-packages'].some(suffix => path.endsWith(suffix))) return route.fulfill({ json: [] })
    return route.fallback()
  })
  await page.route(`**/api/projects/${targetId}/copy-preview`, async route => {
    previewBody = route.request().postDataJSON()
    await route.fulfill({ json: {
      sourceProjectId: sourceId, targetProjectId: targetId, modelCount: 12, drawingCount: 8, bomItemCount: 36,
      validationItemCount: 5, projectFileCount: 1, totalBytes: 10485760, blockingReasons: [], warnings: [], canExecute: true,
      folders: [
        { id: 'gas-folder', name: '气路时序', path: '机械图纸 / 气路时序', templateKey: 'mechanical.air-sequence', fileCount: 1, totalBytes: 1024, defaultSelected: true },
        { id: 'other-folder', name: '其他资料', path: '机械图纸 / 其他资料', templateKey: 'mechanical.other', fileCount: 2, totalBytes: 2048, defaultSelected: false },
      ],
    } })
  })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.addInitScript(() => { window.setInterval = (() => 0) as unknown as typeof window.setInterval })
  await page.setViewportSize({ width: 1988, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.locator('section.pdm-project-workspace')).toBeVisible()
  const projectTabs = page.getByRole('navigation', { name: '项目功能' })
  await expect(projectTabs.getByRole('button').last()).toHaveAccessibleName('项目设置')
  await page.screenshot({ path: testInfo.outputPath('project-settings-entry.png') })
  await projectTabs.getByRole('button', { name: '项目设置' }).click()

  const dialog = page.getByRole('dialog', { name: /项目设置 · P700002/ })
  const copyTab = dialog.getByRole('tab', { name: '项目复制' })
  const resetTab = dialog.getByRole('tab', { name: '项目重置' })
  await expect(copyTab).toHaveAttribute('aria-selected', 'true')
  await expect(dialog.getByRole('heading', { name: '项目复制' })).toBeVisible()
  await expect(dialog.getByRole('heading', { name: '项目重置' })).toBeHidden()
  await expect(dialog).toContainText('目标项目')
  await dialog.getByRole('combobox').click()
  await page.getByRole('option', { name: 'P700001 · 现有源项目' }).click()
  await expect(dialog.getByRole('checkbox', { name: '最新 3D 图档' })).toBeChecked()
  await expect(dialog.getByRole('checkbox', { name: '最新 2D 图档' })).toBeChecked()
  await expect(dialog.getByRole('checkbox', { name: 'BOM' })).toBeChecked()
  await expect(dialog.getByRole('checkbox', { name: '验证计划检查项目' })).toBeChecked()
  await expect(dialog.getByRole('checkbox', { name: /气路时序/ })).toBeChecked()
  await expect(dialog.getByRole('checkbox', { name: /其他资料/ })).not.toBeChecked()
  await expect(dialog).toContainText('验证检查项 5')
  await expect(dialog).toContainText('共 10.0 MB')
  expect(previewBody).toMatchObject({ sourceProjectId: sourceId, copyModels: true, copyDrawings: true, copyBom: true, copyValidationItems: true, folderIds: null })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('project-settings-copy.png') })
  await resetTab.click()
  await expect(resetTab).toHaveAttribute('aria-selected', 'true')
  await expect(dialog.getByRole('heading', { name: '项目复制' })).toBeHidden()
  await expect(dialog.getByRole('heading', { name: '项目重置' })).toBeVisible()
  await expect(dialog).toContainText('当前项目没有可重置的业务内容')
  await page.screenshot({ path: testInfo.outputPath('project-settings-reset.png') })
  expect(await page.title()).toBe('UPTON-PLM')
  expect(errors, failedResponses.join(' | ')).toEqual([])
})

test('project plan week header centers ISO week and places Monday day on the gridline', async ({ page }) => {
  const plan = {
    id: 'plan-week-header', projectId, templateId: 'template-1', templateName: '设备模板', currentStage: 'Design',
    approvalStatus: 'Draft', baselineVersion: 0, plannedStart: '2026-09-07', plannedFinish: '2026-11-08', forecastFinish: '2026-11-08', rowVersion: 1,
    stages: [{ code: 'Design', name: '设计', participatesInDelivery: true, durationRatio: 1, progressRatio: 1 }],
    stageSchedules: [{ stage: 'Design', startDate: '2026-09-07', durationDays: 63 }],
    tasks: [{
      id: 'task-week-header', name: '机械设计', stage: 'Design', assignee: 'engineer', durationDays: 63,
      plannedStart: '2026-09-07', plannedFinish: '2026-11-08', completionPercent: 0, status: 'NotStarted',
      predecessorTaskIds: [], weight: 1, isMilestone: false, isRequired: true, sortOrder: 10,
    }],
    createdBy: 'admin', createdAt: '2026-09-10T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-10T00:00:00Z',
  }
  await page.route(/\/api\/(?:project-plan-templates|projects\/[^/]+\/plan(?:\/portfolio)?)(?:\?.*)?$/, async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/project-plan-templates') return route.fulfill({ json: [] })
    if (path.endsWith('/portfolio')) return route.fulfill({ json: { rootProjectId: projectId, currentStage: 'Design', completionPercent: 0, laggingProjectCount: 0, riskProjectCount: 0, projects: [] } })
    return route.fulfill({ json: plan })
  })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error' || message.type() === 'warning') errors.push(message.text()) })
  await page.setViewportSize({ width: 1988, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: '计划', exact: true }).click()

  await page.getByLabel('甘特图缩放').getByRole('button', { name: '周', exact: true }).click()
  await expect(page.locator('.pdm-plan-switch')).toHaveText('休息日')
  await expect(page.locator('.pdm-gantt-calendar-band.is-year')).toHaveText('2026年')
  await expect(page.locator('.pdm-gantt-calendar-band.is-month')).toHaveText(['8月', '9月', '10月', '11月'])
  const ticks = page.locator('.pdm-gantt-tick')
  await expect(ticks.first().locator('.pdm-gantt-tick__week')).toHaveText(/^W\d{2}$/)
  await expect(ticks.first().locator('.pdm-gantt-tick__week-start')).toHaveText(/^\d{1,2}$/)
  const linePlacement = await ticks.nth(1).evaluate(tick => {
    const tickBox = tick.getBoundingClientRect()
    const dateBox = tick.querySelector<HTMLElement>('.pdm-gantt-tick__week-start')!.getBoundingClientRect()
    return { lineX: tickBox.left, dateCenterX: dateBox.left + dateBox.width / 2 }
  })
  expect(Math.abs(linePlacement.dateCenterX - linePlacement.lineX)).toBeLessThanOrEqual(1)
  const starts = await ticks.locator('time').evaluateAll(items => items.map(item => item.getAttribute('datetime')!))
  expect(starts.length).toBeGreaterThan(2)
  for (let index = 1; index < starts.length; index += 1) {
    expect(new Date(starts[index]!).getTime() - new Date(starts[index - 1]!).getTime()).toBe(7 * 86_400_000)
  }
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  await page.screenshot({ path: join(tmpdir(), 'pdm-plan-week-header.png') })
  expect(await page.title()).toBe('UPTON-PLM')
  expect(errors).toEqual([])
})

test('hierarchy category links open the owning project BOM', async ({ page }, testInfo) => {
  await mockPlanCompanionPanels(page)
  await page.route('**/assets/melon-jelly*.html*', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>登录背景占位</title>' }))
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [] }))
  const childId = '22222222-2222-2222-2222-222222222222'
  const base = { owner: '真实工程师', vaultLocation: 'D:\\PDM\\TEST', releaseLocation: 'D:\\Release\\TEST', isActive: true, quantity: 1, serialNumbers: [], designers: [], rootProjectId: projectId }
  const root = { ...base, id: projectId, code: 'PRJ-REAL-001', name: '导航测试产线', bomItemCategoryCode: '0301' }
  const child = { ...base, id: childId, code: 'PRJ-REAL-001-1', name: '导航测试子项目', parentProjectId: projectId, childSequence: 1, bomItemCategoryCode: '0302' }
  await page.route('**/api/projects**', async route => {
    const url = new URL(route.request().url())
    if (url.pathname === '/api/projects') return route.fulfill({ json: [root, child] })
    if (url.pathname === `/api/projects/${projectId}`) return route.fulfill({ json: root })
    if (url.pathname === `/api/projects/${childId}`) return route.fulfill({ json: child })
    if (url.pathname.startsWith(`/api/projects/${childId}/`)) return route.fallback({ url: url.toString().replace(childId, projectId) })
    return route.fallback()
  })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error' || message.type() === 'warning') errors.push(message.text()) })
  await page.setViewportSize({ width: 1988, height: 1114 })
  await page.goto('/', { waitUntil: 'domcontentloaded' })
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('button', { name: '选择项目号 PRJ-REAL-001', exact: true }).click()
  await page.getByRole('tab', { name: '多级总览' }).click()
  for (const category of ['标准件BOM', '非标件BOM', '电气件BOM']) {
    const expandChild = page.getByRole('button', { name: '展开PRJ-REAL-001-1的三类BOM', exact: true })
    if (await expandChild.isVisible()) await expandChild.click()
    await expect(page.getByRole('button', { name: `进入PRJ-REAL-001-1的${category}`, exact: true })).toBeVisible()
    if (category === '标准件BOM') {
      await page.setViewportSize({ width: 1552, height: 855 })
      const overview = page.locator('.bom-overview:visible')
      const layout = await overview.evaluate(element => {
        const table = element.querySelector('.bom-overview__table-wrap')!
        return {
          bottom: table.getBoundingClientRect().bottom,
          panelBottom: element.closest('.pdm-bom-manager-panel')!.getBoundingClientRect().bottom,
          rows: [...table.querySelectorAll('tbody tr')].map(row => row.getBoundingClientRect().height),
          headerColor: getComputedStyle(table.querySelector('thead th')!).backgroundColor,
          types: [...table.querySelectorAll('tbody tr[data-bom-type]')].map(row => ({ type: row.getAttribute('data-bom-type'), color: getComputedStyle(row.querySelector('td')!).backgroundColor })),
          names: [...table.querySelectorAll('.bom-overview__project')].map(name => ({ width: name.clientWidth, content: name.scrollWidth, whiteSpace: getComputedStyle(name).whiteSpace })),
        }
      })
      expect(layout.panelBottom - layout.bottom).toBeLessThanOrEqual(16)
      for (const height of layout.rows) expect(height).toBeCloseTo(30, 1)
      const typeColors = new Map<string | null, string>()
      for (const row of layout.types) {
        if (typeColors.has(row.type)) expect(row.color).toBe(typeColors.get(row.type))
        typeColors.set(row.type, row.color)
      }
      expect(typeColors.size).toBe(5)
      expect(typeColors.get('Standard')).toBe('rgb(255, 255, 255)')
      expect(typeColors.get('NonStandard')).toBe(typeColors.get('Standard'))
      expect(typeColors.get('Electrical')).toBe(typeColors.get('Standard'))
      expect(typeColors.get('MainProject')).toBe(typeColors.get('ChildProject'))
      expect(typeColors.get('MainProject')).not.toBe(typeColors.get('Standard'))
      expect(typeColors.get('MainProject')).toBe(layout.headerColor)
      for (const name of layout.names) {
        expect(name.whiteSpace).toBe('nowrap')
        expect(name.content).toBeLessThanOrEqual(name.width + 1)
      }
      await page.screenshot({ path: testInfo.outputPath('hierarchy-links.png') })
    }
    await page.getByRole('button', { name: `进入PRJ-REAL-001-1的${category}`, exact: true }).click()
    await expect(page.getByRole('tab', { name: category })).toHaveAttribute('aria-selected', 'true')
    await expect(page.locator('.pdm-project-detail')).toContainText('导航测试子项目')
    await expect(page.getByLabel('BOM维护')).toBeVisible()
    if (category === '标准件BOM') await page.screenshot({ path: testInfo.outputPath('child-standard-bom.png') })
    await page.getByRole('tab', { name: '多级总览' }).click()
    await expect(page.locator('.bom-overview .is-master-row')).toHaveCount(1)
    await expect(page.locator('.bom-overview .is-master-row')).toContainText('PRJ-REAL-001-1')
    await expect(page.getByRole('button', { name: '进入PRJ-REAL-001的标准件BOM', exact: true })).toHaveCount(0)
  }
  await page.getByRole('button', { name: '选择项目号 PRJ-REAL-001', exact: true }).click()
  await expect(page.locator('.bom-overview .is-master-row')).toHaveCount(2)
  await page.getByRole('button', { name: '进入PRJ-REAL-001的标准件BOM', exact: true }).click()
  await expect(page.getByRole('tab', { name: '标准件BOM' })).toHaveAttribute('aria-selected', 'true')
  await expect(page.locator('.pdm-project-detail')).toContainText('导航测试产线')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(await page.title()).not.toBe('')
  expect(errors).toEqual([])
})

test('three BOM categories compare inside the release picker and return to the previous BOM', async ({ page }, testInfo) => {
  test.setTimeout(90_000)
  await page.route('**/assets/melon-jelly*.html*', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>登录背景占位</title>' }))
  await mockPlanCompanionPanels(page)
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [] }))
  await page.route('**/api/auth/heartbeat', route => route.fulfill({ json: {} }))
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  const categories = [
    { kind: 'Standard', prefix: 'S', label: '标准件BOM' },
    { kind: 'NonStandard', prefix: 'N', label: '非标件BOM' },
    { kind: 'Electrical', prefix: 'E', label: '电气件BOM' },
  ]
  const snapshots = categories.flatMap(({ kind, prefix }) => [1, 2].map(number => ({
    id: `${kind}-version-${number}`, projectId, kind, versionNumber: number, label: `${prefix}-V0${number}`, state: 'Released',
    items: [{ id: `${kind}-part`, sequence: 1, drawingNumber: 'PART', name: `${prefix}历史版本物料${number}`, quantity: number, unit: '001', revision: 'A', complete: true }],
    createdBy: 'engineer', updatedBy: 'engineer', createdAt: `2026-10-0${number}T00:00:00Z`, updatedAt: `2026-10-0${number}T00:00:00Z`,
  })))
  await page.route('**/api/projects/*/bom-versions**', route => route.fulfill({ json: snapshots }))
  await page.route('**/api/projects/*/release-packages', route => route.fulfill({ json: [] }))
  await page.setViewportSize({ width: 1959, height: 1080 })
  await page.goto('/', { waitUntil: 'domcontentloaded' })
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  const projectSearch = page.getByRole('region', { name: '当前项目', exact: true })
  const searchAlignment = await projectSearch.evaluate(element => {
    const box = element.getBoundingClientRect()
    const layout = element.closest('.pdm-project-layout')!
    const sidebar = layout.querySelector('.pdm-project-sidebar')!.getBoundingClientRect()
    const header = layout.querySelector('.pdm-project-detail__header')!.getBoundingClientRect()
    return Math.abs(box.left - sidebar.left) < 1 && Math.abs(box.width - sidebar.width) < 1 && Math.abs(box.top - header.top) < 1 && Math.abs(box.bottom - header.bottom) < 1
  })
  expect(searchAlignment).toBe(true)
  await projectSearch.getByRole('button', { name: '浏览项目', exact: true }).click()
  await expect(page.getByRole('dialog', { name: '浏览项目', exact: true })).toBeVisible()
  await page.getByRole('button', { name: '关闭项目浏览', exact: true }).click()
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  for (const { kind, prefix, label } of categories) {
    await page.getByRole('tab', { name: new RegExp('^' + label) }).click()
    const summaryActions = page.getByLabel('BOM统计与操作', { exact: true })
    await expect(summaryActions).toBeVisible()
    await expect(summaryActions).not.toContainText('对比已发布基线')
    const filters = summaryActions.getByRole('group', { name: '筛选发布版差异' })
    await expect(filters).toBeVisible()
    const sameRow = await summaryActions.evaluate(element => {
      const counts = element.querySelector('.pdm-bom-comparison-filters')!.getBoundingClientRect()
      const action = element.querySelector('.pdm-bom-release-strip-actions button')!.getBoundingClientRect()
      return Math.abs(counts.top + counts.height / 2 - action.top - action.height / 2) < 8
    })
    expect(sameRow).toBe(true)
    const searchFilters = page.getByRole('search', { name: '筛选BOM物料', exact: true })
    await expect(searchFilters.getByRole('searchbox')).toHaveCSS('width', '150px')
    for (const select of await searchFilters.locator('select').all()) await expect(select).toHaveCSS('width', '120px')
    await page.screenshot({ path: testInfo.outputPath(`${kind}-summary-actions.png`) })
    const picker = page.getByRole('dialog', { name: '选择发布包', exact: true })
    const openPicker = async () => {
      await page.getByRole('button', { name: '选择发布包', exact: true }).click()
      await expect(picker).toBeVisible()
      await expect(page.locator('.pdm-bom-release-picker-popover:visible')).toHaveCSS('opacity', '1')
    }
    await openPicker()
    if (!await picker.getByLabel('选择BOM版本').isVisible()) await picker.locator('summary').click()
    await picker.getByLabel('选择BOM版本').selectOption(`${kind}-version-1`)
    await expect(picker).not.toBeVisible()
    await expect(page.locator('.pdm-bom-table:visible')).toContainText(`${prefix}历史版本物料1`)
    const startComparison = async () => {
      await openPicker()
      for (const number of [1, 2]) await picker.getByRole('checkbox', { name: `对比 ${prefix}-V0${number}`, exact: true }).check()
      const compare = picker.locator('header').getByRole('button', { name: '对比', exact: true })
      await expect(compare).toBeVisible()
      const boxes = await compare.evaluate(button => ({ button: button.getBoundingClientRect().toJSON(), header: button.closest('header')!.getBoundingClientRect().toJSON() }))
      expect(boxes.button.x).toBeGreaterThan(boxes.header.x + boxes.header.width / 2)
      if (kind === 'Standard') await page.screenshot({ path: testInfo.outputPath('comparison-button-inside-picker.png') })
      await compare.click()
      await expect(picker).not.toBeVisible()
      await expect(page.locator('.bom-release-comparison__panel')).toHaveCount(2)
    }
    await startComparison()
    await page.getByRole('button', { name: '关闭对比', exact: true }).click()
    await expect(page.locator('.bom-release-comparison__panel')).toHaveCount(0)
    await expect(page.locator('.pdm-bom-table:visible')).toContainText(`${prefix}历史版本物料1`)
    await startComparison()
    await openPicker()
    await picker.getByLabel('选择BOM版本').selectOption(`${kind}-version-2`)
    await expect(picker).not.toBeVisible()
    await expect(page.locator('.bom-release-comparison__panel')).toHaveCount(0)
    await expect(page.locator('.pdm-bom-table:visible')).toContainText(`${prefix}历史版本物料2`)
  }
  await page.setViewportSize({ width: 1100, height: 800 })
  await page.getByRole('button', { name: '选择发布包', exact: true }).click()
  const picker = page.getByRole('dialog', { name: '选择发布包', exact: true })
  await expect(picker.locator('header').getByRole('button', { name: '对比', exact: true })).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('comparison-picker-narrow.png') })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(await page.title()).toBe('UPTON-PLM')
  expect(errors).toEqual([])
})

test('login security settings switches and device approval work on LAN HTTP', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  const policy = { singleSessionEnabled: false, deviceBindingEnabled: false, version: 'initial' }
  const device = { id: 'device-one', machine: '设计电脑-LI', mac: '001122334455', approved: false, requestedAt: '2026-10-22T01:00:00Z', lastSeenAt: new Date().toISOString() }
  const session = { id: 'plugin-session', type: 'solidworks', machine: device.machine, version: 'initial', revoked: false, createdAt: device.requestedAt, lastSeenAt: device.lastSeenAt }
  const writes: string[] = []
  await mockPlanCompanionPanels(page)
  await page.route('**/api/login-security**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/policy')) { Object.assign(policy, route.request().postDataJSON(), { version: 'enabled' }); writes.push('policy'); return route.fulfill({ json: policy }) }
    if (path.endsWith('/devices/device-one')) { device.approved = route.request().postDataJSON().approved; writes.push('device'); return route.fulfill({ json: true }) }
    if (path.endsWith('/sessions/plugin-session')) { session.revoked = true; writes.push('logout'); return route.fulfill({ json: true }) }
    return route.fulfill({ json: { policy, canEditPolicy: true, users: [{ username: 'li.pw', devices: [device], sessions: [session] }] } })
  })
  await page.setViewportSize({ width: 1600, height: 1000 })
  await page.goto('/')
  expect(await page.title()).toBe('UPTON-PLM')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.locator('.pdm-sidebar').getByRole('button', { name: '系统设置', exact: true }).click()
  await page.getByRole('button', { name: '登录安全', exact: true }).click()
  const panel = page.getByLabel('登录安全设置')
  await expect(panel).toBeVisible()
  await expect(panel.getByRole('switch')).toHaveCount(2)
  for (const item of await panel.getByRole('switch').all()) await expect(item).not.toBeChecked()
  await panel.getByRole('switch', { name: /启用同端登录限制/ }).check()
  await panel.getByRole('button', { name: '保存设置' }).click()
  await expect(panel.getByRole('status')).toContainText('所有旧登录需要重新登录')
  page.on('dialog', dialog => dialog.accept())
  await panel.getByRole('button', { name: '确认绑定' }).click()
  await expect(panel.getByRole('button', { name: '解除绑定' })).toBeVisible()
  await panel.getByRole('button', { name: '强制下线' }).click()
  await expect(panel.getByRole('button', { name: '强制下线' })).toBeDisabled()
  await expect(panel).toContainText('网页端只限制登录会话，不限制电脑')
  await page.screenshot({ path: join(tmpdir(), 'pdm-login-security-desktop.png'), fullPage: false })
  await page.setViewportSize({ width: 1100, height: 800 })
  await expect(panel.getByRole('switch', { name: /启用同端登录限制/ })).toBeVisible()
  await page.screenshot({ path: join(tmpdir(), 'pdm-login-security-narrow.png'), fullPage: false })
  expect(writes).toEqual(['policy', 'device', 'logout'])
  expect(errors).toEqual([])
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
})

async function mockPlanCompanionPanels(page: Page) {
  await page.route('**/api/projects/*/contact-information', route => route.fulfill({ json: { information: { projectId, rowVersion: 0 }, customerName: '', canEdit: false } }))
  await page.route('**/api/projects/*/nameplate', route => route.fulfill({ json: { nameplate: { projectId, rowVersion: 0 }, canEdit: false, template: { fields: [] } } }))
  await page.route('**/api/projects/*/budget/assessments', route => route.fulfill({ json: { sheets: [], amounts: {} } }))
}

test('project plan stage task and assignee filters keep table and gantt rows aligned over LAN HTTP', async ({ page }) => {
  await mockPlanCompanionPanels(page)
  const project = { id: projectId, code: 'PLAN-FILTER', name: '甘特图筛选验收', owner: 'admin', primaryProjectManager: 'admin',
    collaborativeProjectManagers: [], designers: ['engineer'], designLeads: [], quantity: 1, serialNumbers: [], isActive: true,
    vaultLocation: 'D:/PDM', releaseLocation: 'D:/Release', canReadContent: true, effectiveProjectPermissions: ['project.plan.edit'] }
  await page.route('**/api/projects', route => route.fulfill({ json: [project] }))
  await page.route(`**/api/projects/${projectId}`, route => route.fulfill({ json: project }))
  const task = { stage: 'Design', plannedStart: '2026-09-10', plannedFinish: '2026-09-22', durationDays: 13, completionPercent: 0,
    status: 'NotStarted', predecessorTaskIds: [], weight: 1, isMilestone: false, isRequired: true, sortOrder: 10 }
  const plan = { id: 'filter-plan', projectId, templateId: 'template', templateName: '设备模板', currentStage: 'Design', approvalStatus: 'Draft',
    baselineVersion: 0, plannedStart: task.plannedStart, plannedFinish: task.plannedFinish, forecastFinish: task.plannedFinish, rowVersion: 1,
    stages: [{ code: 'Design', name: '设计' }, { code: 'Assembly', name: '装配' }],
    tasks: [{ ...task, id: 'mechanical', name: '机械设计', assignee: 'engineer' }, { ...task, id: 'electrical', name: '电气设计', assignee: 'admin', sortOrder: 20 },
      { ...task, id: 'assembly', name: '机械装配', stage: 'Assembly', assignee: 'engineer', sortOrder: 30 },
      { ...task, id: 'acceptance', name: '装配验收', stage: 'Assembly', sortOrder: 40 }],
    createdBy: 'admin', createdAt: '2026-09-10T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-10T00:00:00Z' }
  await page.route(/\/api\/(?:project-plan-templates|projects\/[^/]+\/plan(?:\/portfolio)?)(?:\?.*)?$/, route => {
    const path = new URL(route.request().url()).pathname
    return route.fulfill({ json: path === '/api/project-plan-templates' ? [] : path.endsWith('/portfolio')
      ? { rootProjectId: projectId, currentStage: 'Design', completionPercent: 0, projects: [] } : plan })
  })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.setViewportSize({ width: 1916, height: 1080 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: '计划', exact: true }).click()
  const filters = page.getByLabel('甘特图任务筛选', { exact: true })
  const taskNames = () => page.locator('.pdm-gantt-info-row.is-task .pdm-gantt-name strong')
  await expect(taskNames()).toHaveText(['机械设计', '电气设计', '机械装配', '装配验收'])
  const ticks = (await page.locator('.pdm-gantt-timeline-head').textContent())!
  const choose = async (label: string, option: string) => {
    await filters.locator('.el-select').filter({ has: page.getByRole('combobox', { name: label, exact: true }) }).locator('.el-select__wrapper').click()
    await page.getByRole('option', { name: option, exact: true }).click()
    await filters.locator('small').click()
  }
  const checkAlignment = async () => {
    const rows = await page.locator('.pdm-gantt-table').evaluate(element => {
      const info = [...element.querySelectorAll('.pdm-gantt-info-row')].map(row => row.getBoundingClientRect())
      const gantt = [...element.querySelectorAll('.pdm-gantt-timeline-row')].map(row => row.getBoundingClientRect())
      return { sameLength: info.length === gantt.length, aligned: info.every((row, index) => Math.abs(row.top - gantt[index]!.top) <= 1 && Math.abs(row.height - gantt[index]!.height) <= 1) }
    })
    expect(rows).toEqual({ sameLength: true, aligned: true })
  }
  await choose('筛选阶段', '设计')
  await expect(taskNames()).toHaveText(['机械设计', '电气设计'])
  await expect(page.locator('.pdm-gantt-info-row.is-stage')).toHaveCount(1)
  await expect(page.locator('.pdm-gantt-timeline-head')).toHaveText(ticks)
  await page.getByLabel('筛选任务', { exact: true }).fill('机械')
  await expect(taskNames()).toHaveText(['机械设计'])
  await checkAlignment()
  await choose('筛选责任人', '系统管理员')
  await expect(page.getByRole('heading', { name: '暂无符合筛选条件的任务', exact: true })).toBeVisible()
  await filters.getByRole('button', { name: '清空筛选', exact: true }).click()
  await expect(taskNames()).toHaveCount(4)
  await choose('筛选阶段', '装配')
  await choose('筛选责任人', '待分配')
  await expect(taskNames()).toHaveText(['装配验收'])
  await checkAlignment()
  await expect(filters.getByRole('status')).toHaveText('匹配 1 / 4 项任务')
  await page.screenshot({ path: join(tmpdir(), 'pdm-plan-filters-combined.png'), animations: 'disabled' })
  await filters.getByRole('button', { name: '清空筛选', exact: true }).click()
  await expect(taskNames()).toHaveCount(4)
  await page.getByLabel('折叠信息列', { exact: true }).click()
  await expect(page.getByRole('combobox', { name: '筛选责任人', exact: true })).toBeVisible()
  await page.getByLabel('筛选任务', { exact: true }).fill('设计')
  await expect(taskNames()).toHaveText(['机械设计', '电气设计'])
  await checkAlignment()
  await page.setViewportSize({ width: 1280, height: 900 })
  await expect(filters).toBeVisible()
  expect(await filters.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true)
  await checkAlignment()
  await page.screenshot({ path: join(tmpdir(), 'pdm-plan-filters-narrow.png'), animations: 'disabled' })
  expect(errors).toEqual([])
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
})

test('project plan toolbar uses uniform buttons and shows actual completion date', async ({ page }, testInfo) => {
  await mockPlanCompanionPanels(page)
  const plan = {
    id: 'plan-1', projectId, templateId: 'template-1', templateName: '设备模板', currentStage: 'Design',
    approvalStatus: 'Draft', baselineVersion: 0, plannedStart: '2026-09-10', plannedFinish: '2026-09-22', forecastFinish: '2026-09-22', rowVersion: 1,
    stages: [{ code: 'Design', name: '设计', participatesInDelivery: true, durationRatio: 1, progressRatio: 1 }],
    stageSchedules: [{ stage: 'Design', startDate: '2026-09-10', durationDays: 13 }],
    tasks: [{
      id: 'task-1', name: '机械设计', stage: 'Design', assignee: 'engineer', durationDays: 13,
      plannedStart: '2026-09-10', plannedFinish: '2026-09-22', actualStart: '2026-09-10', actualFinish: '2026-09-20',
      completionPercent: 100, status: 'Completed', predecessorTaskIds: [], weight: 1, isMilestone: false, isRequired: true, sortOrder: 10,
    }],
    createdBy: 'admin', createdAt: '2026-09-10T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-10T00:00:00Z',
  }
  await page.route(/\/api\/(?:project-plan-templates|projects\/[^/]+\/plan(?:\/portfolio)?)(?:\?.*)?$/, async route => {
    const path = new URL(route.request().url()).pathname
    const body = path === '/api/project-plan-templates'
      ? []
      : path.endsWith('/portfolio')
        ? { rootProjectId: projectId, currentStage: 'Design', completionPercent: 100, laggingProjectCount: 0, riskProjectCount: 0, projects: [] }
        : plan
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
  })
  const errors: string[] = []
  page.on('console', message => {
    if (message.type() === 'error') errors.push(`${message.location().url}: ${message.text()}`)
  })
  await page.setViewportSize({ width: 1920, height: 1080 })
  await page.clock.install({ time: new Date('2026-09-16T12:00:00+08:00') })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: '计划', exact: true }).click()

  const toolbar = page.locator('.pdm-plan-toolbar')
  await expect(toolbar).toBeVisible()
  const buttonSizes = await toolbar.locator('.pdm-plan-view-tabs > button, .pdm-plan-toolbar__actions > button').evaluateAll(buttons => buttons.map(button => {
    const rect = button.getBoundingClientRect()
    return { text: button.textContent?.trim(), isTab: !!button.closest('.pdm-plan-view-tabs'), width: rect.width, height: rect.height }
  }))
  expect(buttonSizes.length).toBeGreaterThan(2)
  expect(buttonSizes.every(button => button.width === 80 && button.height === (button.isTab ? 28 : 32))).toBe(true)
  const headerCells = await page.locator('.pdm-gantt-info-head > span').evaluateAll(cells => cells.filter(cell => cell.textContent?.trim()).map(cell => {
    const rect = cell.getBoundingClientRect()
    return { text: cell.textContent?.trim(), left: rect.left, centerY: rect.top + rect.height / 2 }
  }))
  expect(headerCells.map(cell => cell.text)).toEqual(['项目 / 任务', '阶段', '责任人', '进度', '计划日期', '工期', '完成日期'])
  expect(headerCells.every((cell, index) => index === 0 || cell.left > headerCells[index - 1]!.left), JSON.stringify(headerCells)).toBe(true)
  expect(Math.max(...headerCells.map(cell => cell.centerY)) - Math.min(...headerCells.map(cell => cell.centerY))).toBeLessThanOrEqual(1)
  await expect(page.locator('.pdm-gantt-info-row.is-task > span').filter({ hasText: '2026-09-20' })).toHaveCount(1)
  await toolbar.getByRole('button', { name: '日', exact: true }).click()
  await expect(toolbar.getByRole('button', { name: '日', exact: true })).toHaveClass(/is-active/)
  await toolbar.getByRole('button', { name: '周', exact: true }).click()
  await expect(toolbar.getByRole('button', { name: '周', exact: true })).toHaveClass(/is-active/)
  await expect(page.locator('.pdm-gantt-tick', { hasText: 'W37' }).first()).toBeVisible()
  const todayMarker = await page.locator('.pdm-gantt-today.is-header').evaluate(marker => {
    const line = marker.getBoundingClientRect()
    const label = marker.querySelector<HTMLElement>('.pdm-gantt-today__label')!.getBoundingClientRect()
    return { text: marker.textContent?.trim(), lineX: line.left, labelCenterX: label.left + label.width / 2 }
  })
  expect(todayMarker.text).toBe('今天')
  expect(Math.abs(todayMarker.labelCenterX - todayMarker.lineX)).toBeLessThanOrEqual(1)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
  await page.screenshot({ path: testInfo.outputPath('project-plan-toolbar-80x32-completion-date.png'), fullPage: false })
  await page.setViewportSize({ width: 1285, height: 1114 })
  await page.screenshot({ path: testInfo.outputPath('project-plan-week-number-today-1285.png'), fullPage: false })
})

test('project plan stages can overlap and moving one stage keeps unrelated stages unchanged', async ({ page }, testInfo) => {
  const project = { id: projectId, code: 'PRJ-REAL-001', name: '并行排期项目', owner: 'admin', primaryProjectManager: 'admin', collaborativeProjectManagers: [],
    vaultLocation: 'D:\\PDM\\PRJ-REAL-001', releaseLocation: 'D:\\Release\\PRJ-REAL-001', isActive: true, quantity: 1, serialNumbers: [], designers: [], canReadContent: true, effectiveProjectPermissions: ['project.plan.edit'] }
  const plan = {
    id: 'parallel-plan', projectId, templateId: 'template-1', templateName: '并行模板', currentStage: 'Design',
    approvalStatus: 'Draft', baselineVersion: 0, plannedStart: '2026-09-05', plannedFinish: '2026-09-25', forecastFinish: '2026-09-25', rowVersion: 1,
    stages: [
      { code: 'Design', name: '设计', participatesInDelivery: true, durationRatio: .5, progressRatio: .5 },
      { code: 'Preparation', name: '备料', participatesInDelivery: true, durationRatio: .5, progressRatio: .5 },
    ],
    stageSchedules: [
      { stage: 'Design', startDate: '2026-09-05', durationDays: 11 },
      { stage: 'Preparation', startDate: '2026-09-16', durationDays: 10 },
    ],
    tasks: [
      { id: 'design-task', name: '设计任务', stage: 'Design', assignee: 'admin', durationDays: 14, plannedStart: '2026-09-05', plannedFinish: '2026-09-18', completionPercent: 82, status: 'InProgress', predecessorTaskIds: [], weight: 1, isMilestone: false, isRequired: true, sortOrder: 10 },
      { id: 'prepare-task', name: '备料任务', stage: 'Preparation', assignee: 'admin', durationDays: 16, plannedStart: '2026-09-10', plannedFinish: '2026-09-25', completionPercent: 0, status: 'NotStarted', predecessorTaskIds: [], weight: 1, isMilestone: false, isRequired: true, sortOrder: 20 },
    ],
    createdBy: 'admin', createdAt: '2026-09-10T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-10T00:00:00Z',
  }
  let saved: { tasks: typeof plan.tasks; changeReason: string } | null = null
  await page.route('**/api/projects**', async route => {
    const url = new URL(route.request().url())
    if (url.pathname === '/api/projects') return route.fulfill({ json: [project] })
    if (url.pathname === `/api/projects/${projectId}`) return route.fulfill({ json: project })
    return route.fallback()
  })
  await page.route(/\/api\/(?:project-plan-templates|projects\/[^/]+\/plan(?:\/portfolio)?)(?:\?.*)?$/, async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/project-plan-templates') return route.fulfill({ json: [] })
    if (path.endsWith('/portfolio')) return route.fulfill({ json: { rootProjectId: projectId, currentStage: 'Design', completionPercent: 0, laggingProjectCount: 0, riskProjectCount: 0, projects: [] } })
    if (route.request().method() === 'PUT') {
      saved = route.request().postDataJSON() as typeof saved
      return route.fulfill({ json: { ...plan, ...(saved ?? {}), rowVersion: 2 } })
    }
    return route.fulfill({ json: plan })
  })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error' || message.type() === 'warning') errors.push(message.text()) })
  await page.setViewportSize({ width: 1988, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: '计划', exact: true }).click()

  await expect(page.getByText(/拖动阶段条可整体平移/)).toHaveCount(0)
  await expect(page.getByText(/保存主项目计划会同步跟随且未批准/)).toHaveCount(0)
  await expect(page.locator('.pdm-plan-summary')).toContainText('项目发货日期')
  await expect(page.locator('.pdm-plan-summary')).toContainText('2026-09-25')
  await expect(page.getByText('资源冲突', { exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: '收起阶段', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: '折叠列', exact: true })).toHaveCount(0)
  await page.getByRole('button', { name: '收起全部阶段', exact: true }).click()
  await expect(page.getByRole('button', { name: '展开全部阶段', exact: true })).toBeVisible()
  await page.getByRole('button', { name: '展开全部阶段', exact: true }).click()
  await page.getByRole('button', { name: '折叠信息列', exact: true }).click()
  await expect(page.getByRole('button', { name: '展开信息列', exact: true })).toBeVisible()
  await page.getByRole('button', { name: '展开信息列', exact: true }).click()
  const infoRowHeights = await page.locator('.pdm-gantt-info-row').evaluateAll(rows => rows.map(row => row.getBoundingClientRect().height))
  const timelineRowHeights = await page.locator('.pdm-gantt-timeline-row').evaluateAll(rows => rows.map(row => row.getBoundingClientRect().height))
  expect([...new Set(infoRowHeights)]).toEqual([40])
  expect([...new Set(timelineRowHeights)]).toEqual([40])
  const stageBars = page.locator('.pdm-gantt-timeline-row.is-stage .pdm-gantt-bar')
  await expect(stageBars).toHaveCount(2)
  await expect(stageBars.first().locator('b')).toHaveCSS('mix-blend-mode', 'difference')
  const boxes = await stageBars.evaluateAll(items => items.map(item => {
    const rect = item.getBoundingClientRect()
    return { left: rect.left, right: rect.right }
  }))
  expect(boxes[0]!.right).toBeGreaterThan(boxes[1]!.left)
  const first = await stageBars.first().boundingBox()
  expect(first).not.toBeNull()
  await page.mouse.move(first!.x + first!.width / 2, first!.y + first!.height / 2)
  await page.mouse.down()
  await page.mouse.move(first!.x + first!.width / 2 + 60, first!.y + first!.height / 2)
  await page.mouse.up()
  await expect.poll(() => saved).not.toBeNull()
  const design = saved!.tasks.find(task => task.id === 'design-task')!
  const preparation = saved!.tasks.find(task => task.id === 'prepare-task')!
  expect(design.plannedStart).not.toBe('2026-09-05')
  expect(preparation.plannedStart).toBe('2026-09-10')
  expect(preparation.plannedFinish).toBe('2026-09-25')
  expect(saved!.changeReason).toContain('其他阶段不再保持首尾连续')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
  await page.screenshot({ path: testInfo.outputPath('project-plan-overlapping-stages.png'), fullPage: false })
})

test('engineer logs in and reads the API-backed PLM workspace', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1920, height: 1080 })
  const projectTabErrors: string[] = []
  page.on('pageerror', error => projectTabErrors.push(error.message))
  page.on('console', message => {
    if (message.type() === 'error' || message.type() === 'warning') projectTabErrors.push(`${message.text()} ${message.location().url}`.trim())
  })
  const contentResponses: number[] = []
  page.on('response', response => {
    if (response.url().includes('globalStatusContent')) contentResponses.push(response.status())
  })
  await page.addInitScript(() => {
    Object.defineProperty(window, 'pdmHostMessages', {
      configurable: true,
      value: [],
      writable: true,
    })
    Object.defineProperty(window.chrome, 'webview', {
      configurable: true,
      value: { postMessage: (message: unknown) => (window as unknown as { pdmHostMessages: unknown[] }).pdmHostMessages.push(message), addEventListener: () => undefined },
    })
  })
  await page.goto('/')

  await expect(page.getByRole('main', { name: '未登录主页' })).toBeVisible()
  const loginForm = page.getByLabel('登录PLM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('engineer')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.getByRole('button', { name: '项目中心', exact: true })).toBeVisible()
  const titlebar = page.locator('.pdm-titlebar')
  await expect(titlebar).toContainText('昆山阿普顿自动化系统有限公司')
  await expect(titlebar).toContainText('工程师')
  await expect(titlebar.getByRole('button', { name: '消息' })).toBeVisible()
  await expect(titlebar.getByRole('button', { name: '主题' })).toBeVisible()
  await expect(titlebar.getByRole('button', { name: '退出' })).toBeVisible()
  const idleStatus = titlebar.locator('.pdm-global-status.is-idle')
  await expect(idleStatus).toBeVisible()
  await expect(idleStatus.locator('.pdm-global-status__text')).not.toBeEmpty()
  await expect(idleStatus.locator('.pdm-global-status__source')).not.toBeEmpty()
  await expect(idleStatus).not.toContainText('UPTON')
  await idleStatus.click()
  await expect(page.locator('.pdm-global-status-detail')).toContainText('来源：')
  await page.screenshot({ path: testInfo.outputPath('global-status-content.png'), fullPage: false })
  await page.keyboard.press('Escape')
  await page.setViewportSize({ width: 1285, height: 1114 })
  const statusGeometry = await page.evaluate(() => {
    const company = document.querySelector<HTMLElement>('.pdm-tenant-company')?.getBoundingClientRect()
    const statusElement = document.querySelector<HTMLElement>('.pdm-global-status')
    const textElement = statusElement?.querySelector<HTMLElement>('.pdm-global-status__text')
    const sourceElement = statusElement?.querySelector<HTMLElement>('.pdm-global-status__source')
    const status = statusElement?.getBoundingClientRect()
    const text = textElement?.getBoundingClientRect()
    const source = sourceElement?.getBoundingClientRect()
    return {
      gap: company && status ? status.left - company.right : -1,
      overflow: document.documentElement.scrollWidth - window.innerWidth,
      lineHeight: statusElement ? getComputedStyle(statusElement).lineHeight : '',
      sourceFontWeight: sourceElement ? getComputedStyle(sourceElement).fontWeight : '',
      textInside: Boolean(status && text && text.top >= status.top && text.bottom <= status.bottom),
      sourceInside: Boolean(status && source && source.top >= status.top && source.bottom <= status.bottom),
    }
  })
  expect(Math.abs(statusGeometry.gap - 50)).toBeLessThanOrEqual(1)
  expect(statusGeometry.overflow).toBeLessThanOrEqual(0)
  expect(statusGeometry.lineHeight).toBe('20px')
  expect(statusGeometry.sourceFontWeight).toBe('400')
  expect(statusGeometry.textInside).toBe(true)
  expect(statusGeometry.sourceInside).toBe(true)
  await page.screenshot({ path: testInfo.outputPath('global-status-content-1285.png'), fullPage: false })
  expect(contentResponses).toContain(200)
  await page.setViewportSize({ width: 1920, height: 1080 })
  const crmShellLayout = await page.evaluate(() => {
    const sidebar = document.querySelector<HTMLElement>('.pdm-sidebar')
    const brand = document.querySelector<HTMLElement>('.pdm-sidebar__brand')
    const navItem = document.querySelector<HTMLElement>('.pdm-nav-item')
    const titlebar = document.querySelector<HTMLElement>('.pdm-titlebar')
    const main = document.querySelector<HTMLElement>('.pdm-main')
    const sidebarStyle = sidebar ? getComputedStyle(sidebar) : null
    const titlebarStyle = titlebar ? getComputedStyle(titlebar) : null
    const mainStyle = main ? getComputedStyle(main) : null
    return {
      sidebarWidth: sidebar?.getBoundingClientRect().width ?? 0,
      brandHeight: brand?.getBoundingClientRect().height ?? 0,
      navItemHeight: navItem?.getBoundingClientRect().height ?? 0,
      titlebarHeight: titlebar?.getBoundingClientRect().height ?? 0,
      sidebarBackground: sidebarStyle?.backgroundImage ?? '',
      titlebarBackground: titlebarStyle?.backgroundColor ?? '',
      mainPadding: mainStyle?.paddingTop ?? '',
    }
  })
  expect(crmShellLayout.sidebarWidth).toBe(155)
  expect(crmShellLayout.brandHeight).toBe(62)
  expect(crmShellLayout.navItemHeight).toBe(56)
  expect(crmShellLayout.titlebarHeight).toBe(50)
  expect(crmShellLayout.sidebarBackground).toContain('linear-gradient')
  expect(crmShellLayout.titlebarBackground).toBe('rgb(255, 255, 255)')
  expect(crmShellLayout.mainPadding).toBe('5px')
  await page.screenshot({ path: testInfo.outputPath('crm-shell-project-center.png'), fullPage: false })
  await page.getByRole('banner').getByRole('button', { name: '主题' }).click()
  await page.getByRole('menuitem', { name: /石墨青绿/ }).click()
  await expect(page.locator('.pdm-app-shell')).toHaveClass(/theme-c/)
  await expect.poll(() => page.evaluate(() => localStorage.getItem('pdm_theme'))).toBe('c')
  await page.getByRole('banner').getByRole('button', { name: '消息' }).click()
  await expect(page.locator('.pdm-message-drawer').getByLabel('我的待办')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.locator('.pdm-message-drawer')).toBeHidden()
  await page.locator('.pdm-sidebar').getByRole('button', { name: '项目总览', exact: true }).click()
  await page.locator('.pdm-navigation-tabs').getByRole('tab', { name: '项目列表', exact: true }).click()
  await expect(page.getByRole('columnheader', { name: '序列号' })).toBeVisible()
  await expect(page.getByRole('cell', { name: '70000001' })).toBeVisible()
  await page.getByLabel('事业部筛选').selectOption('自动化事业部')
  await page.getByLabel('项目经理筛选').selectOption('project-manager-2')
  await page.getByLabel('主设工程师筛选').selectOption('design-lead')
  await expect(page.getByRole('button', { name: '进入项目 PRJ-REAL-001', exact: true })).toBeVisible()
  await page.getByRole('button', { name: '进入项目 PRJ-REAL-001', exact: true }).click()

  await expect(page.locator('.pdm-project-sidebar__summary').getByText('PRJ-REAL-001 · 真实装配项目', { exact: true })).toBeVisible()
  await expect(page.getByRole('banner').getByText('真实工程师', { exact: true })).toBeVisible()
  await expect(page.locator('.pdm-project-tabs button')).toHaveText(['概览', '信息', '文件', '计划', 'OPL', '预算', '质量', '图档', 'BOM', '发布', '备料', '记录', '设置'])
  await page.getByRole('button', { name: '文件', exact: true }).click()
  await expect(page.getByText('项目文件夹', { exact: true })).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('project-tabs-overview-file-project-plan-1920.png'), fullPage: false })
  await page.setViewportSize({ width: 1285, height: 1114 })
  await expect(page.locator('.pdm-project-tabs button')).toHaveText(['概览', '信息', '文件', '计划', 'OPL', '预算', '质量', '图档', 'BOM', '发布', '备料', '记录', '设置'])
  await page.screenshot({ path: testInfo.outputPath('project-tabs-overview-file-project-plan-1285.png'), fullPage: false })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(projectTabErrors).toEqual([])
  await page.setViewportSize({ width: 1920, height: 1080 })
  await expect(page.getByText('机械图纸', { exact: true })).toBeVisible()
  await page.getByText('PRJ-REAL-001-0', { exact: true }).first().click()
  const fileDetails = page.getByRole('table', { name: '受控图档' })
  await expect(fileDetails).toBeVisible()
  await expect(fileDetails.getByRole('columnheader')).toHaveCount(8)
  const fileDetailWidths = await fileDetails.evaluate(table => {
    const headers = [...table.querySelectorAll<HTMLElement>('th')]
    return { table: table.getBoundingClientRect().width, number: headers[1]?.getBoundingClientRect().width ?? 0, name: headers[2]?.getBoundingClientRect().width ?? 0, updated: headers[7]?.getBoundingClientRect().width ?? 0 }
  })
  expect(fileDetailWidths.table).toBeGreaterThanOrEqual(1080)
  expect(fileDetailWidths.number).toBeGreaterThanOrEqual(200)
  expect(fileDetailWidths.name).toBeGreaterThanOrEqual(260)
  expect(fileDetailWidths.updated).toBeGreaterThanOrEqual(170)
  await page.getByRole('button', { name: '图档', exact: true }).click()
  await page.evaluate(({ projectId }) => window.dispatchEvent(new CustomEvent('pdm-workspace-local-state', { detail: {
    projectId,
    projectCode: 'PRJ-REAL-001',
    projectDirectory: 'E:\\Workspace\\View\\PRJ-REAL-001',
    projectDirectoryExists: true,
    items: [
      { documentId: 'doc-root', fileName: 'REAL-ASM-001.SLDASM', fullPath: 'E:\\Workspace\\View\\PRJ-REAL-001\\REAL-ASM-001.SLDASM', localState: 'Editable', localStateLabel: '可编辑', localRevision: 'W2', latestRevision: 'W2', message: '已由你检出，可以继续编辑。', isReadOnly: false },
      { documentId: 'doc-part', fileName: 'REAL-PRT-001.SLDPRT', fullPath: 'E:\\Workspace\\View\\PRJ-REAL-001\\REAL-PRT-001.SLDPRT', localState: 'NeedsUpdate', localStateLabel: '需要更新', localRevision: 'W1', latestRevision: 'A', message: '本地版本需要更新。', isReadOnly: true },
    ],
  } })), { projectId })
  await expect(page.getByLabel('项目设计树')).toContainText('REAL-PRT-001')
  await expect(page.getByLabel('工作区位置')).toContainText('可编辑')
  await expect(page.getByLabel('项目设计树')).toContainText('需要更新')
  await page.getByRole('button', { name: '打开文件夹', exact: true }).click()
  await expect.poll(() => page.evaluate(() => (window as unknown as { pdmHostMessages: Array<{ type?: string }> }).pdmHostMessages.some(message => message.type === 'workspace-open-folder'))).toBe(true)
  await expect(page.getByLabel('项目基本信息与全部项目号').getByLabel('BOM完整性')).toHaveCount(0)
  await expect(page.getByLabel('项目基本信息与全部项目号').getByLabel('当前发布包')).toHaveCount(0)
  const previewLayout = await page.evaluate(() => {
    const preview = document.querySelector<HTMLElement>('[aria-label="客户端内嵌eDrawings预览区"]')?.getBoundingClientRect()
    const controls = document.querySelector<HTMLElement>('[aria-label="图档查看与操作"]')?.getBoundingClientRect()
    const family = document.querySelector<HTMLElement>('.pdm-project-family')?.getBoundingClientRect()
    const sidebar = document.querySelector<HTMLElement>('.pdm-project-sidebar')?.getBoundingClientRect()
    return { previewTop: preview?.top ?? -1, controlsBottom: controls?.bottom ?? -1, familyBottom: family?.bottom ?? -1, sidebarWidth: sidebar?.width ?? -1, sidebarBottom: sidebar?.bottom ?? -1, viewportHeight: window.innerHeight, pageHeight: document.documentElement.scrollHeight }
  })
  expect(previewLayout.previewTop).toBeGreaterThanOrEqual(previewLayout.controlsBottom)
  expect(previewLayout.previewTop - previewLayout.controlsBottom).toBeLessThanOrEqual(1)
  expect(Math.abs(previewLayout.sidebarWidth - 200)).toBeLessThanOrEqual(2)
  expect(Math.abs(previewLayout.familyBottom - previewLayout.sidebarBottom)).toBeLessThanOrEqual(2)
  expect(previewLayout.sidebarBottom).toBeLessThanOrEqual(previewLayout.viewportHeight)
  expect(previewLayout.pageHeight).toBeLessThanOrEqual(previewLayout.viewportHeight)
  await expect(page.getByText('PRJ-2026-018')).toHaveCount(0)

  const markupToolbar = page.getByLabel('图形批注工具')
  await expect(markupToolbar.locator('button')).toHaveCount(4)
  expect(await markupToolbar.locator('button').evaluateAll(buttons => buttons.map(button => button.getAttribute('aria-label')))).toEqual([
    '引线批注',
    '云线批注',
    '框选批注',
    '手绘批注',
  ])
  await expect(page.getByRole('button', { name: '使用位置' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: '作废图档' })).toHaveCount(0)
  await expect(page.getByLabel('图档属性')).toBeVisible()
  await expect(page.getByText('设计工作版 · 不供生产')).toHaveCount(0)
  await page.getByRole('button', { name: '保存批注' }).click()
  await expect.poll(() => page.evaluate(() => (window as unknown as { pdmHostMessages: Array<{ type?: string }> }).pdmHostMessages.some(message => message.type === 'preview-host-save-markup'))).toBe(true)
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('pdm-preview-markup-status', { detail: { state: 'saved', message: '批注已保存。' } })))

  await page.evaluate(() => window.dispatchEvent(new CustomEvent('pdm-solidworks-capability', { detail: { available: true } })))
  const previewCommands = page.locator('.pdm-preview-command')
  await expect(previewCommands).toHaveCount(3)
  expect(await previewCommands.evaluateAll(buttons => buttons.map(button => Math.round(button.getBoundingClientRect().width)))).toEqual([100, 100, 84])
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('pdm-preview-markup-status', { detail: { state: 'dirty', message: '批注尚未保存。' } })))
  await expect(page.getByRole('button', { name: '保存批注' })).toHaveClass(/is-markup-dirty/)
  const solidWorksButton = page.getByRole('button', { name: '打开最新' })
  await expect(solidWorksButton).toBeEnabled()
  await page.screenshot({ path: testInfo.outputPath('controlled-open-actions.png'), fullPage: false })
  await solidWorksButton.click()
  await expect.poll(() => page.evaluate(() => (window as unknown as { pdmHostMessages: Array<{ type?: string; payload?: { mode?: string } }> }).pdmHostMessages?.filter(message => message.type === 'open-document').at(-1))).toMatchObject({ type: 'open-document', payload: { mode: 'LatestReadOnly' } })

  await page.getByRole('treeitem').first().click({ button: 'right' })
  await expect(page.getByRole('menuitem', { name: '获取最新版本并编辑' })).toHaveCount(0)
  const releasedButton = page.getByRole('menuitem', { name: '只读打开最近正式发布版' })
  await expect(releasedButton).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('controlled-open-context-menu.png'), fullPage: false })
  await releasedButton.click()
  await expect.poll(() => page.evaluate(() => (window as unknown as { pdmHostMessages: Array<{ type?: string; payload?: { mode?: string } }> }).pdmHostMessages?.filter(message => message.type === 'open-document').at(-1))).toMatchObject({ type: 'open-document', payload: { mode: 'LatestReleased' } })

  await page.getByRole('tab', { name: /^2D/ }).click()
  await expect(page.getByLabel('项目设计树')).toContainText('真实总装工程图')
  await page.locator('.pdm-drawing-list .pdm-tree-row').first().click()
  await expect(page.getByLabel('图档预览')).toContainText('REAL-ASM-001.SLDDRW')
  await expect(page.getByText('设计工作版 · 不供生产')).toBeVisible()

  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '标准件BOM' }).click()
  await expect(page.locator('.pdm-edit-table tbody tr').first().getByRole('button', { name: '编辑物料编码' })).toHaveText('REAL-STD-001')
  await page.locator('.pdm-edit-table tbody tr').first().getByLabel('选择物料').check()
  await expect(page.getByText(/已选择 1 项/)).toBeVisible()
  await page.getByRole('button', { name: '编辑', exact: true }).click()
  const batchDialog = page.getByRole('dialog', { name: '批量编辑BOM属性' })
  await expect(batchDialog).toBeVisible()
  await expect(batchDialog.getByText('1 条物料 · 0 个属性')).toBeVisible()
  await batchDialog.getByLabel('修改品牌').check()
  await batchDialog.getByPlaceholder('勾选后留空即清空').nth(2).fill('UPTON')
  await batchDialog.getByLabel('修改易损件').check()
  await batchDialog.getByLabel('易损件批量值').selectOption({ label: '否' })
  await expect(batchDialog.getByText('1 条物料 · 2 个属性')).toBeVisible()
  await page.screenshot({ path: join(tmpdir(), 'pdm-bom-batch-wearpart-20260915.png'), fullPage: false })
  await batchDialog.getByRole('button', { name: '取消' }).click()
  await expect(batchDialog).toHaveCount(0)
})

test('BOM issue details confirm master model and brand and clear resolved warnings', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.route('**/boms/Standard', route => route.fulfill({ json: [{ id: 'bom-brand-empty', kind: 'Standard', sequence: 1,
    drawingNumber: '01020009750', name: '接头', specification: 'CEF-1.0-20211028123', brand: '', sourceDocumentId: 'doc-part', source: 'Auto', quantity: 2, unit: '001', revision: 'W1', isComplete: true,
    reconciliationStatus: 'ManualOverrideMismatch', reconciliationNote: 'BOM维护值与料品主档不一致：型号、品牌。' }] }))
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [{ bomItemId: 'bom-brand-empty', status: 'ValidationFailed', candidates: [], application: null,
    issues: ['图档品牌缺失'], material: { id: 'material-brand', materialCode: '01020009750', name: '接头', specification: 'CEF-1.0', brand: '西霸士', approvalStatus: 'Approved', isArchived: false } }] }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '标准件BOM' }).click()
  await page.getByRole('button', { name: '查看问题详情：型号、品牌不一致', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '问题详情' })
  await expect(dialog).toContainText('西霸士')
  await expect(dialog.getByRole('button', { name: '确认填入品牌' })).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('bom-confirm-master-brand.png') })
  await dialog.getByRole('button', { name: '确认填入型号' }).click()
  await expect(dialog.getByRole('button', { name: '确认填入型号' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: '查看问题详情：品牌不一致', exact: true })).toBeVisible()
  await dialog.getByRole('button', { name: '确认填入品牌' }).click()
  await expect(dialog).toHaveCount(0)
  await expect(page.getByRole('button', { name: '查看问题详情：品牌不一致', exact: true })).toHaveCount(0)
  const row = page.locator('.pdm-edit-table tbody tr').first()
  await expect(row.getByRole('button', { name: '编辑品牌', exact: true })).toHaveText('西霸士')
  await expect(row.getByRole('button', { name: '编辑型号', exact: true })).toHaveText('CEF-1.0')
  await expect(row.getByRole('button', { name: '编辑物料编码', exact: true })).toHaveText('01020009750')
  expect(errors).toEqual([])
})

test('three BOMs process summary groups and load instance structure only in source data', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  let sourceReads = 0
  let saveBody: { items: unknown[]; summaryRows: Array<{ itemIds: string[]; fields: string[] }>; summaryOrder: string[] } | undefined
  let summaries = Array.from({ length: 31 }, (_, index) => ({ id: `summary-${index}`, kind: 'Standard', sequence: index + 1, drawingNumber: `CODE-${index}`, name: `物料${index}`, quantity: 100,
    unit: '001', specification: 'MODEL', brand: 'BRAND', revision: 'W1', isComplete: true, source: 'Auto', sourceDocumentId: 'doc-part',
    summaryItemIds: Array.from({ length: 100 }, (_, member) => member === 0 ? `summary-${index}` : `instance-${index}-${member}`), summaryFingerprint: `fingerprint-${index}` }))
  const deleted = { ...summaries[0], id: 'deleted-summary', drawingNumber: 'DELETED', isManuallyExcluded: true, summaryItemIds: ['deleted-summary', 'deleted-instance'] }
  await page.route('**/boms/Standard?*', async route => {
    if (route.request().method() === 'PUT') {
      saveBody = route.request().postDataJSON()
      summaries = summaries.map((item, index) => index === 0 ? { ...item, quantity: 120, remark: '按种类处理' } : item)
      await route.fulfill({ json: { items: [...summaries, deleted], draft: null } })
    } else await route.fulfill({ json: [...summaries, deleted] })
  })
  await page.route('**/bom-source-data', async route => {
    sourceReads++
    await route.fulfill({ json: [0, 1].map(index => ({ id: `source-${index}`, kind: 'Standard', sequence: index + 1,
      drawingNumber: 'SOURCE', name: '结构物料', quantity: 1, unit: '001', revision: 'W1', isComplete: true,
      source: 'Auto', sourceDocumentId: 'doc-part', sourceInstancePath: `ROOT/PART-${index}` })) })
  })
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [] }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '标准件BOM（31）', exact: true }).click()
  await expect(page.getByRole('button', { name: '按结构', exact: true })).toHaveCount(0)
  await expect(page.locator('.pdm-edit-table tbody tr:not(.is-quick-entry)')).toHaveCount(31)
  expect(sourceReads).toBe(0)
  const row = page.locator('.pdm-edit-table tbody tr').first()
  await row.getByRole('button', { name: '编辑备注信息', exact: true }).click()
  await row.getByLabel('内联编辑备注信息').fill('按种类处理')
  await row.getByLabel('内联编辑备注信息').press('Enter')
  await row.getByRole('button', { name: '编辑数量', exact: true }).click()
  await row.getByLabel('内联编辑数量').fill('120')
  await row.getByLabel('内联编辑数量').press('Enter')
  await expect(page.getByRole('dialog', { name: '选择汇总数量修改位置' })).toHaveCount(0)
  await page.getByRole('button', { name: /保存BOM/ }).click()
  await expect.poll(() => saveBody?.summaryRows.length).toBe(1)
  expect(saveBody!.items).toHaveLength(0)
  expect(saveBody!.summaryRows[0].itemIds).toHaveLength(100)
  expect(saveBody!.summaryRows[0].fields.sort()).toEqual(['quantity', 'remark'])
  expect(saveBody!.summaryOrder).toHaveLength(31)
  await page.getByRole('button', { name: '回收站（1）', exact: true }).click()
  await expect(page.locator('.pdm-bom-recycle-dialog tbody tr')).toHaveCount(1)
  await page.getByRole('button', { name: '关闭BOM回收站', exact: true }).click()
  await page.screenshot({ path: testInfo.outputPath('bom-31-summary-groups.png') })
  await page.getByRole('tab', { name: /^源数据/ }).click()
  await expect.poll(() => sourceReads).toBe(1)
  await expect(page.getByRole('button', { name: '按结构', exact: true })).toBeVisible()
  expect(errors).toEqual([])
})

test('BOM distinguishes absent material codes from property mismatches', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.route('**/boms/Standard', route => route.fulfill({ json: [
    { id: 'code-absent', kind: 'Standard', sequence: 1, drawingNumber: '01021000072', name: '接头', specification: 'MODEL', brand: '', sourceDocumentId: 'doc-part', source: 'Auto', quantity: 2, unit: '001', revision: 'W1', isComplete: true,
      reconciliationStatus: 'ManualOverrideMismatch', reconciliationNote: 'BOM维护值与料品主档不一致：物料编码、型号、品牌。' },
    { id: 'code-empty', kind: 'Standard', sequence: 2, drawingNumber: '', name: '未填写料号物料', specification: 'OTHER', brand: '', quantity: 1, unit: '001', revision: 'W1', isComplete: false }
  ] }))
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [{ bomItemId: 'code-absent', status: 'CodeNotFound', material: null, candidates: [], application: null, issues: ['料号不存在于料品主档'] }] }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '标准件BOM' }).click()
  await expect(page.getByRole('button', { name: '查看问题详情：未填写料号', exact: true })).toBeVisible()
  await page.getByRole('button', { name: '查看问题详情：料号不存在', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '问题详情' })
  await expect(dialog).toContainText('料号“01021000072”不存在于料品主档')
  await expect(dialog).toContainText('无法核对型号和品牌')
  await expect(dialog.getByRole('button', { name: '确认填入品牌' })).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('bom-code-not-found.png') })
  expect(errors).toEqual([])
})

test('BOM archived master permits confirmed model and brand fill', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.route('**/boms/Standard', route => route.fulfill({ json: [{ id: 'bom-brand-empty', kind: 'Standard', sequence: 1,
    drawingNumber: '01020009750', name: '接头', specification: 'CEF-1.0-20211028123', brand: '', sourceDocumentId: 'doc-part', source: 'Auto', quantity: 2, unit: '001', revision: 'W1', isComplete: true,
    reconciliationStatus: 'ManualOverrideMismatch', reconciliationNote: 'BOM维护值与料品主档不一致：物料编码、型号、品牌。' }] }))
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [{ bomItemId: 'bom-brand-empty', status: 'ValidationFailed', candidates: [], application: null,
    issues: ['料品主档已归档', '图档品牌缺失'], material: { id: 'material-brand', materialCode: '01020009750', name: '接头', specification: 'CEF-1.0', brand: '西霸士', approvalStatus: 'Approved', isArchived: true } }] }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '标准件BOM' }).click()
  await page.getByRole('button', { name: '查看问题详情：物料编码等3项不一致', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '问题详情' })
  await expect(dialog).toContainText('西霸士')
  await expect(dialog.getByRole('button', { name: '确认填入品牌' })).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('bom-confirm-master-brand.png') })
  await dialog.getByRole('button', { name: '确认填入型号' }).click()
  await expect(dialog.getByRole('button', { name: '确认填入型号' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: '查看问题详情：物料编码、品牌不一致', exact: true })).toBeVisible()
  await dialog.getByRole('button', { name: '确认填入品牌' }).click()
  await expect(dialog).toBeVisible()
  await expect(dialog).toContainText('料品主档已归档')
  await expect(dialog).not.toContainText('图档品牌缺失')
  await expect(dialog).not.toContainText('型号与料品主档不一致')
  await expect(page.getByRole('button', { name: '查看问题详情：物料编码不一致', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: '查看问题详情：品牌不一致', exact: true })).toHaveCount(0)
  const row = page.locator('.pdm-edit-table tbody tr').first()
  await expect(row.getByRole('button', { name: '编辑品牌', exact: true })).toHaveText('西霸士')
  await expect(row.getByRole('button', { name: '编辑型号', exact: true })).toHaveText('CEF-1.0')
  await expect(row.getByRole('button', { name: '编辑物料编码', exact: true })).toHaveText('01020009750')
  expect(errors).toEqual([])
})

test('BOM inline brand searches existing brands and accepts a suggestion', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [] }))
  await page.route('**/api/materials/brands?*', async route => {
    const query = new URL(route.request().url()).searchParams.get('query') || ''
    await route.fulfill({ json: ['SMC', '施耐德'].filter(brand => brand.toLowerCase().includes(query.toLowerCase())) })
  })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '标准件BOM' }).click()
  const buttonSizes = await page.locator('.pdm-bom-manager-panel :is(.pdm-project-subtabs, .pdm-manager-actions, .pdm-bom-selection-toolbar, .pdm-bom-release-strip-actions) button').evaluateAll(buttons => buttons
    .filter(button => button.getClientRects().length > 0)
    .map(button => ({ label: button.textContent, isTab: button.getAttribute('role') === 'tab', width: button.getBoundingClientRect().width, height: button.getBoundingClientRect().height })))
  expect(buttonSizes.length).toBeGreaterThan(10)
  for (const button of buttonSizes) {
    expect(button.width, button.label || '').toBeCloseTo(button.isTab ? 120 : 60, 1)
    expect(button.height, button.label || '').toBeCloseTo(button.isTab ? 28 : 30, 1)
  }
  await page.screenshot({ path: testInfo.outputPath('bom-buttons-60x28.png') })
  const row = page.locator('.pdm-edit-table tbody tr').first()
  await row.getByRole('button', { name: '编辑品牌', exact: true }).click()
  const input = row.getByLabel('内联编辑品牌')
  await input.fill('sm')
  await expect(page.getByRole('option', { name: 'SMC', exact: true })).toBeVisible()
  await input.fill('施')
  const option = page.getByRole('option', { name: '施耐德', exact: true })
  await expect(option).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('bom-brand-suggestions.png') })
  await option.click()
  await expect(input).toHaveCount(0)
  await expect(row.getByRole('button', { name: '编辑品牌', exact: true })).toHaveText('施耐德')
  await page.screenshot({ path: testInfo.outputPath('bom-brand-one-selection.png') })
  await page.locator('tr.is-quick-entry').getByLabel('物料名称', { exact: true }).fill('新增气缸')
  const draft = page.locator('.pdm-edit-table tbody tr:not(.is-quick-entry)').last()
  const draftBrand = draft.getByLabel('品牌', { exact: true })
  await draftBrand.fill('sm')
  await page.getByRole('option', { name: 'SMC', exact: true }).click()
  await expect(draftBrand).toHaveValue('SMC')
  await page.screenshot({ path: testInfo.outputPath('bom-new-row-brand-index.png') })
  expect(errors).toEqual([])
})

test('release overview displays failure details and published status', async ({ page }, testInfo) => {
  const releaseProject = { id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: 'admin',
    vaultLocation: 'D:/PDM', releaseLocation: 'D:/Release', isActive: true, quantity: 1, serialNumbers: ['70000001'],
    designers: [], canReadContent: true, effectiveProjectPermissions: ['project.edit', 'bom.mechanical.edit', 'release.manage'] }
  await page.route('**/api/projects', route => route.fulfill({ json: [releaseProject] }))
  await page.route(`**/api/projects/${projectId}`, route => route.fulfill({ json: releaseProject }))
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.setViewportSize({ width: 994, height: 855 })
  const common = { workflowVersion: 1, approvalTasks: [], selectedBomItemIds: [], standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [] }
  let conversionRunning = false
  await page.route('**/api/release-packages/waiting-release/preview/retry', route => {
    setTimeout(() => { conversionRunning = true }, 1200)
    return route.fulfill({ status: 202, json: { message: '已重新排队' } })
  })
  await page.route(`**/api/projects/${projectId}/release-packages`, route => route.fulfill({ json: [
    { ...common, id: 'failed-release', number: 'RP-FAILED', scope: 'NonStandardWithDrawing', state: 'PublishFailed',
      publishError: 'Deadlock found when trying to get lock; try restarting transaction' },
    { ...common, id: 'published-release', number: 'RP-SUCCESS', scope: 'StandardFormal', state: 'Published', previewState: 'Running', publishedAt: '2026-10-08T09:34:23Z' },
    { ...common, id: 'waiting-release', number: 'RP-WAITING', scope: 'ElectricalFormal', state: 'Publishing',
      previewState: conversionRunning ? 'Running' : 'Pending', previewError: conversionRunning ? null : 'COM error 80080005 (CO_E_SERVER_EXEC_FAILURE)', previewUpdatedAt: '2026-10-09T00:15:41Z' },
  ] }))
  await page.route(`**/api/projects/${projectId}/release-preview-items*`, route => route.fulfill({ json: [{
    releasePackageId: 'published-release', releasePackageNumber: 'RP-SUCCESS', documentId: 'preview-doc',
    drawingNumber: 'D-001', fileName: 'D-001.SLDPRT', kind: 'Part', format: 'Step', succeeded: false,
    fileLength: 0, error: '正在转换…',
  }, {
    releasePackageId: 'published-release', releasePackageNumber: 'RP-SUCCESS', documentId: 'preview-ready',
    drawingNumber: 'D-002', fileName: 'D-002.SLDPRT', kind: 'Part', format: 'Step', succeeded: true,
    fileLength: 1024, error: null,
  }] }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: '发布', exact: true }).click()
  const overview = page.getByRole('region', { name: '发布总览' })
  await expect(overview.locator('.pdm-release-failure-reason')).toHaveText('失败原因：数据库事务发生死锁，本次发布被中止。请稍后重试；如反复出现，请联系管理员排查。')
  await expect(overview.locator('.pdm-release-published-state')).toHaveText('已发布')
  await expect(overview.locator('.pdm-release-published-state')).toBeVisible()
  await expect(overview.locator('.pdm-release-preview-table').getByText('转换中', { exact: true })).toBeVisible()
  await expect(overview).toContainText('转换中 1、排队中 0、失败 0')
  await expect(overview.locator('.pdm-release-preview-table').getByRole('button', { name: '重试', exact: true })).toBeDisabled()
  await expect(overview.locator('.pdm-release-publishing-reason')).toContainText('当前环节：等待图纸转换')
  await expect(overview.locator('.pdm-release-publishing-reason')).toContainText('转图服务器无法启动 SolidWorks（错误码 80080005）')
  const downloadButton = overview.getByRole('button', { name: '下载', exact: true })
  await expect(downloadButton).toHaveClass(/pdm-secondary-action/)
  await expect(downloadButton).toHaveCSS('width', '60px')
  await expect(downloadButton).toHaveCSS('height', '24px')
  await expect(overview.locator('.pdm-release-publishing-reason')).toBeVisible()
  await expect(overview.getByRole('button', { name: '重试转图', exact: true })).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('release-status-overview.png'), fullPage: false })
  await overview.getByRole('button', { name: '重试转图', exact: true }).click()
  await expect(overview.locator('.pdm-release-retry-feedback')).toContainText('已请求重试')
  await expect(overview.locator('.pdm-release-publishing-reason')).toContainText('正在生成正式图纸及发布资料', { timeout: 15000 })
  await overview.getByRole('button', { name: /RP-FAILED/ }).click()
  await expect(page.locator('.pdm-bom-release-drawer')).toBeVisible()
  await expect(page.locator('.pdm-bom-release-drawer')).toContainText('数据库事务发生死锁，本次发布被中止')
  expect(errors).toEqual([])
})

test('PDF title block fields are independent and persist in administrator settings', async ({ page }) => {
  await mockPlanCompanionPanels(page)
  await page.route('**/api/standard-structures/storage', route => route.fulfill({ json: { storageRoot: 'D:/Standard', rowVersion: 1 } }))
  await page.route('**/api/browser-archives/capabilities', route => route.fulfill({ json: { enabled: false, canConfigure: false } }))
  await page.route('**/@vite/client', route => route.request().frame().parentFrame()
    ? route.fulfill({ contentType: 'application/javascript', headers: { 'Access-Control-Allow-Origin': '*' }, body: '' }) : route.fallback())
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => {
    const loginGpuFallback = message.text().includes('WebGPU') && (message.location().url.includes('melon-jelly-') || message.text().includes('普通 HTTP'))
    if (message.type() === 'error' && !loginGpuFallback) errors.push(message.text())
  })
  let settings: Record<string, unknown> = { vaultRoot: 'D:/PDM/Vault', releaseRoot: 'D:/PDM/Release',
    checkoutHeartbeatSeconds: 180, checkoutLeaseMinutes: 15, checkoutOfflineGraceMinutes: 60,
    checkoutReminderHours: 4, checkoutStrongReminderHours: 8, checkoutOverdueHours: 24, checkoutForceReleaseHours: 48 }
  await page.route('**/api/system-settings', async route => {
    if (route.request().method() === 'PUT') settings = route.request().postDataJSON()
    await route.fulfill({ json: settings })
  })
  await page.setViewportSize({ width: 1680, height: 1050 })
  await page.goto(process.env.PDM_QA_LAN_URL || '/', { waitUntil: 'domcontentloaded' })
  await expect(page).toHaveTitle('UPTON-PLM')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '系统设置', exact: true }).click()
  await page.getByRole('button', { name: '编号与存储', exact: true }).click()
  await page.getByRole('tab', { name: 'PDF属性填写', exact: true }).click()
  const panel = page.locator('.pdf-title-block-settings')
  await expect(panel.getByRole('heading', { name: 'PDF 属性自动填写' })).toBeInViewport({ ratio: 1 })
  await expect(panel.getByRole('button', { name: '保存 PDF 填写设置' })).toBeInViewport({ ratio: 1 })
  for (const checkbox of await panel.getByRole('checkbox').all()) await expect(checkbox).toBeInViewport({ ratio: 1 })
  await expect(panel.getByRole('checkbox')).toHaveCount(19)
  await expect(panel.locator('input:checked')).toHaveCount(0)
  for (const name of ['易损件', '物料编码', '重量', '设计日期', '本次下发数量']) await panel.getByLabel(`自动填写${name}`, { exact: true }).check()
  await panel.getByRole('button', { name: '保存 PDF 填写设置' }).click()
  await expect.poll(() => settings.pdfTitleBlockFill).toEqual({ enabledFields: ['wearPart', 'materialCode', 'weight', 'designedOn', 'quantity'] })
  await expect(panel.getByLabel('自动填写设计人', { exact: true })).not.toBeChecked()
  await page.getByRole('tab', { name: '图纸转换', exact: true }).click()
  await expect(panel).not.toBeVisible()
  await page.getByRole('tab', { name: 'PDF属性填写', exact: true }).click()
  await expect(panel.getByLabel('自动填写重量', { exact: true })).toBeChecked()
  await expect(panel.getByRole('heading', { name: 'PDF 属性自动填写' })).toBeVisible()
  expect(await panel.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true)
  const evidence = process.env.PDM_QA_SCREENSHOT
  if (evidence) await page.screenshot({ path: evidence, fullPage: false })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(new URL(page.url()).protocol).toBe('http:')
  expect(errors).toEqual([])
})

test('conversion settings display multiple named servers', async ({ page }, testInfo) => {
  await page.route('**/api/system-settings', route => route.fulfill({ json: {
    vaultRoot: 'D:/PDM/Vault', releaseRoot: 'D:/PDM/Release', checkoutHeartbeatSeconds: 180, checkoutLeaseMinutes: 15,
    checkoutOfflineGraceMinutes: 60, checkoutReminderHours: 4, checkoutStrongReminderHours: 8, checkoutOverdueHours: 24, checkoutForceReleaseHours: 48,
    previewConversion: { mode: 'Remote', agentUrl: 'http://one:5199', agentToken: '', timeoutMinutes: 30,
      servers: [{ name: '一号转图机', agentUrl: 'http://one:5199', agentToken: '', timeoutMinutes: 30 },
        { name: '二号转图机', agentUrl: 'http://two:5199', agentToken: '', timeoutMinutes: 30 }] },
  } }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '系统设置', exact: true }).click()
  await page.getByRole('button', { name: '编号与存储', exact: true }).click()
  await page.getByRole('tab', { name: '图纸转换', exact: true }).click()
  await expect(page.getByText('转图服务器：一号转图机 · http://one:5199', { exact: true })).toBeVisible()
  await expect(page.getByText('转图服务器：二号转图机 · http://two:5199', { exact: true })).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('named-conversion-servers.png'), fullPage: false })
})

test('supplement release links multiple OPL issues and exposes complete publication history over LAN HTTP', async ({ page }) => {
  await page.setViewportSize({ width: 1916, height: 1080 })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  const project = { id: projectId, code: 'PRJ-REAL-001', name: 'OPL发布关联验收', owner: 'admin', vaultLocation: 'D:/PDM', releaseLocation: 'D:/Release',
    isActive: true, quantity: 1, serialNumbers: ['70000001'], designers: [], canReadContent: true, effectiveProjectPermissions: ['project.edit', 'bom.mechanical.edit', 'release.manage'] }
  await page.route('**/api/projects', route => route.fulfill({ json: [project] }))
  await page.route(`**/api/projects/${projectId}`, route => route.fulfill({ json: project }))
  const formal = { id: 'formal-opl', projectId, number: 'RP-FORMAL', scope: 'StandardFormal', state: 'Published', workflowVersion: 1,
    publishedAt: '2026-10-18T01:00:00Z', standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], approvalTasks: [], selectedBomItemIds: [] }
  await page.route('**/api/projects/*/release-packages', route => route.fulfill({ json: [formal] }))
  await page.route('**/boms/Standard?summary=true', route => route.fulfill({ json: [{ id: 'new-material', kind: 'Standard', sequence: 1,
    drawingNumber: '01020003283', name: '新增接头', quantity: 1, unit: '001', revision: 'W1', isComplete: true, specification: 'H08-01', brand: 'SMC' }] }))
  const issue = { id: 'opl-a', projectId, number: 1, description: '相机支架位置调整', priority: '普通', status: '待处理', owner: 'admin', dueDate: null,
    solution: '', createdBy: 'admin', createdAt: '2026-10-18T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-10-18T00:00:00Z', rowVersion: 1, attachments: [] }
  const records = [
    { releasePackageId: 'history-new', projectId, packageNumber: 'RP-OPL-002', bomType: '非标件', drawingCount: 8, publishedAt: '2026-10-19T01:00:00Z' },
    { releasePackageId: 'history-old', projectId, packageNumber: 'RP-OPL-001', bomType: '标准件', drawingCount: 0, publishedAt: '2026-10-18T01:00:00Z' },
  ]
  await page.route('**/api/projects/*/opl', route => route.fulfill({ json: { rows: [{ issue, canEdit: true, canComplete: true, releases: records },
    { issue: { ...issue, id: 'opl-b', number: 2, description: '电气线路修改' }, canEdit: true, canComplete: true, releases: [records[0]] }],
    people: [{ username: 'admin', displayName: '系统管理员' }], rootProject: project, subprojects: [] } }))
  let submitted: { relatedOplIssueIds: string[] } | undefined
  await page.route('**/api/release-packages', async route => {
    submitted = route.request().postDataJSON()
    await route.fulfill({ status: 400, json: { detail: '验收请求已捕获，未写入实际发布数据。' } })
  })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '标准件BOM' }).click()
  await page.getByRole('button', { name: '发起发布', exact: true }).click()
  const release = page.locator('.pdm-bom-release-drawer')
  await release.getByRole('button', { name: '新建发布包', exact: true }).click()
  await release.getByLabel('发布类型', { exact: true }).selectOption('StandardSupplement')
  await release.getByLabel('整包需求日期').fill('2026-10-30')
  await release.getByLabel('变更原因 设计问题 / 设计变更').check()
  await release.locator('.release-opl-picker summary').click()
  await release.getByLabel('关联OPL 1', { exact: true }).check()
  await release.getByLabel('关联OPL 2', { exact: true }).check()
  await expect(release.locator('.release-opl-picker summary')).toContainText('已选 2 项')
  await page.screenshot({ path: join(tmpdir(), 'pdm-opl-release-picker.png'), animations: 'disabled' })
  await release.getByRole('button', { name: '创建草稿', exact: true }).click()
  await expect.poll(() => submitted?.relatedOplIssueIds).toEqual(['opl-a', 'opl-b'])
  await release.locator('.el-drawer__close-btn').click()
  await page.getByRole('button', { name: 'OPL', exact: true }).click()
  await expect(page.locator('.opl-table')).toContainText('2026-10-19')
  await page.locator('.opl-table tbody tr').first().click()
  const history = page.getByLabel('关联发布记录', { exact: true })
  await expect(history).toContainText('2 次')
  await expect(history).toContainText('RP-OPL-002')
  await expect(history).toContainText('RP-OPL-001')
  await expect(history.locator('tbody tr').first()).toContainText('8')
  await page.screenshot({ path: join(tmpdir(), 'pdm-opl-release-history.png'), animations: 'disabled' })
  await page.setViewportSize({ width: 800, height: 900 })
  await expect(history).toBeVisible()
  await expect(page.getByRole('button', { name: '保存问题', exact: true })).toBeVisible()
  expect(await page.locator('body').evaluate(el => el.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: join(tmpdir(), 'pdm-opl-release-history-narrow.png'), animations: 'disabled' })
  expect(errors).toEqual([])
  expect(await page.locator('vite-error-overlay').count()).toBe(0)
})

test('compact BOM formal release sends every selected instance', async ({ page }, testInfo) => {
  const releaseProject = { id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: 'admin',
    vaultLocation: 'D:/PDM', releaseLocation: 'D:/Release', isActive: true, quantity: 1, serialNumbers: ['70000001'],
    designers: [], canReadContent: true, effectiveProjectPermissions: ['project.edit', 'bom.mechanical.edit', 'release.manage'] }
  await page.route('**/api/projects', route => route.fulfill({ json: [releaseProject] }))
  await page.route(`**/api/projects/${projectId}`, route => route.fulfill({ json: releaseProject }))
  const ids = Array.from({ length: 8 }, (_, index) => `11111111-1111-4111-8111-${String(index + 1).padStart(12, '0')}`)
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.route('**/boms/Standard?summary=true', route => route.fulfill({ json: [{
    id: ids[0], summaryItemIds: ids, kind: 'Standard', sequence: 1, drawingNumber: '01020003283', name: '直通卡套接头',
    quantity: 8, unit: '001', revision: 'W1', isComplete: true, specification: 'H08-01', brand: 'SMC',
  }] }))
  let submitted: string[] | undefined
  await page.route('**/api/release-packages', async route => {
    if (route.request().method() !== 'POST') return route.fallback()
    submitted = route.request().postDataJSON().selectedBomItemIds
    await route.fulfill({ status: 400, json: { detail: '测试请求已捕获，未创建实际发布包' } })
  })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '标准件BOM' }).click()
  await page.getByRole('button', { name: '发起发布', exact: true }).click()
  await page.getByLabel('发布类型', { exact: true }).selectOption('StandardFormal')
  await page.getByLabel('整包需求日期').fill('2026-10-18')
  await page.getByRole('button', { name: '创建草稿', exact: true }).click()
  await expect.poll(() => submitted).toEqual(ids)
  expect(errors).toEqual([])
  await page.screenshot({ path: testInfo.outputPath('compact-release-instances.png'), fullPage: false })
})

test('BOM save timeout releases the button and preserves unsaved edits', async ({ page }) => {
  let saves = 0
  await page.route('**/boms/Standard?includeDraft=true', () => { saves++ })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '标准件BOM' }).click()
  const row = page.locator('.pdm-edit-table tbody tr').first()
  await row.getByRole('button', { name: '编辑品牌', exact: true }).click()
  await row.getByLabel('内联编辑品牌').fill('超时后保留')
  await row.getByLabel('内联编辑品牌').press('Enter')
  await page.clock.install()
  await page.getByRole('button', { name: /保存BOM/ }).click()
  await expect(page.getByRole('button', { name: '保存中…' })).toBeDisabled()
  await page.clock.fastForward(60_001)
  await expect(page.getByText(/服务端保存结果尚未确认/).first()).toBeVisible()
  await expect(page.getByRole('button', { name: /保存BOM/ })).toBeEnabled()
  await expect(row.getByRole('button', { name: '编辑品牌', exact: true })).toHaveText('超时后保留')
  await expect(page.getByText(/未保存 \d+ 项/)).toBeVisible()
  expect(saves).toBe(1)
})

test('BOM save reuses returned draft rows without reloading all versions', async ({ page }, testInfo) => {
  let versionReads = 0
  let baselineReads = 0
  let saves = 0
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.route('**/bom-versions', async route => { versionReads++; await route.fulfill({ json: [] }) })
  await page.route('**/bom-baselines', async route => { baselineReads++; await route.fulfill({ json: [] }) })
  await page.route('**/boms/Standard?includeDraft=true', async route => {
    saves++
    const items = route.request().postDataJSON().items
    await route.fulfill({ json: {
      items, draft: { id: 'saved-draft', projectId, kind: 'Standard', state: 'Draft', versionNumber: 1,
        label: 'S-COMPACT-V01', items: [], createdBy: 'engineer', createdAt: '2026-10-08T05:00:00Z', updatedBy: 'engineer', updatedAt: '2026-10-08T05:01:00Z' },
      draftItemIds: items.map((item: { id: string }) => item.id), draftItemOverrides: [],
    } })
  })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '标准件BOM' }).click()
  const row = page.locator('.pdm-edit-table tbody tr').first()
  await row.getByRole('button', { name: '编辑品牌', exact: true }).click()
  await row.getByLabel('内联编辑品牌').fill('保存性能测试')
  await row.getByLabel('内联编辑品牌').press('Enter')
  const before = [versionReads, baselineReads]
  await page.getByRole('button', { name: /保存BOM/ }).click()
  await expect(page.getByText('S-COMPACT-V01', { exact: false }).first()).toBeVisible()
  await expect(page.getByRole('button', { name: /保存BOM/ })).toBeEnabled()
  await expect(row.getByRole('button', { name: '编辑品牌', exact: true })).toHaveText('保存性能测试')
  expect(saves).toBe(1)
  expect([versionReads, baselineReads]).toEqual(before)
  expect(errors).toEqual([])
  await page.screenshot({ path: testInfo.outputPath('bom-compact-save.png'), fullPage: false })
})

test('BOM duplicate candidates require confirmation and summary quantity is editable', async ({ page }, testInfo) => {
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [] }))
  const consoleMessages: string[] = []
  const failedResponses: string[] = []
  page.on('console', message => {
    if (message.type() === 'error' || message.type() === 'warning') consoleMessages.push(`${message.type()}: ${message.text()}`)
  })
  page.on('response', response => {
    if (response.status() >= 400) failedResponses.push(`${response.status()} ${response.url()}`)
  })
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto('/')
  const loginForm = page.getByLabel('登录PLM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('engineer')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '标准件BOM' }).click()

  const materialCodeActions = page.locator('.pdm-bom-selection-actions .pdm-bom-material-code-toolbar-action')
  await expect(materialCodeActions).toHaveText(['引用物料', '关联物料', '核对料号', '申请料号'])
  expect(await materialCodeActions.evaluateAll(buttons => buttons.map(button => {
    const box = button.getBoundingClientRect()
    return { width: box.width, height: box.height }
  }))).toEqual([
    { width: 60, height: 30 },
    { width: 60, height: 30 },
    { width: 60, height: 30 },
    { width: 60, height: 30 },
  ])
  await page.screenshot({ path: testInfo.outputPath('bom-material-code-actions.png'), fullPage: false })
  await page.getByRole('button', { name: '引用物料', exact: true }).click()
  const materialReferenceDialog = page.getByRole('dialog', { name: '引用物料' })
  await expect(materialReferenceDialog).toBeVisible()
  await expect(materialReferenceDialog.getByRole('button', { name: '普通料品', exact: true })).toBeVisible()
  await expect(materialReferenceDialog.getByRole('button', { name: 'UKIT套件', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: '引用套件', exact: true })).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('bom-unified-material-reference.png'), fullPage: false })
  await materialReferenceDialog.getByRole('button', { name: '关闭物料引用' }).click()

  const row = page.locator('.pdm-edit-table tbody tr').first()
  await expect(row.getByRole('button', { name: '编辑数量' })).toHaveText('10')
  await row.getByRole('button', { name: '编辑数量' }).click()
  await row.getByLabel('内联编辑数量').fill('2')
  await row.getByLabel('内联编辑数量').press('Enter')
  const quantityDialog = page.getByRole('dialog', { name: '分配汇总数量到结构位置' })
  await expect(quantityDialog).toBeVisible()
  await expect(quantityDialog.getByText('汇总数量 10 → 2')).toBeVisible()
  await expect(quantityDialog.getByText('REAL-STD-001')).toBeVisible()
  await expect(quantityDialog.getByText('标准紧固件')).toBeVisible()
  await expect(quantityDialog.getByText('M8')).toBeVisible()
  await expect(quantityDialog.getByText('FESTO')).toBeVisible()
  await expect(quantityDialog.getByRole('row')).toHaveCount(3)
  await expect(row.getByRole('button', { name: '编辑数量' })).toHaveText('10')
  await expect(quantityDialog.getByRole('button', { name: '确认分配' })).toBeDisabled()
  const quantityInputs = quantityDialog.getByRole('spinbutton')
  await quantityInputs.nth(0).fill('0')
  await quantityInputs.nth(1).fill('2')
  await expect(quantityDialog.getByText('待分配 0')).toBeVisible()
  await expect(quantityDialog.getByText('其中 1 个位置数量为 0')).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('summary-quantity-location.png'), fullPage: false })
  await quantityDialog.getByRole('button', { name: '确认分配' }).click()
  await expect(page.getByText('保存BOM时这些位置将自动移入回收站')).toBeVisible()
  await page.getByRole('button', { name: '确认删除并继续' }).click()
  await expect(quantityDialog).toHaveCount(0)
  await expect(row.getByRole('button', { name: '编辑数量' })).toHaveText('2')

  await row.getByRole('button', { name: '编辑型号' }).click()
  await row.getByLabel('内联编辑型号').fill('PH602')
  await row.getByLabel('内联编辑型号').press('Enter')
  await expect(row.getByRole('button', { name: '编辑型号' })).toHaveText('PH602')
  await row.getByRole('button', { name: '确认料品', exact: true }).click()
  const duplicateDialog = page.getByRole('dialog', { name: '重复料品，请确认选择' })
  await expect(duplicateDialog).toBeVisible()
  await expect(duplicateDialog.getByRole('row')).toHaveCount(3)
  await expect.poll(() => duplicateDialog.evaluate(element => getComputedStyle(element).transform)).toBe('none')
  const dialogBox = await duplicateDialog.boundingBox()
  const confirmButtonBoxes = await duplicateDialog.getByRole('button', { name: '确认选择' }).evaluateAll(buttons => buttons.map(button => {
    const box = button.getBoundingClientRect()
    return { left: box.left, right: box.right }
  }))
  const viewportWidth = await page.evaluate(() => window.innerWidth)
  expect(dialogBox?.width).toBeGreaterThan(900)
  expect(confirmButtonBoxes.every(box => box.left >= 0 && box.right <= viewportWidth)).toBe(true)
  await expect(row.getByRole('button', { name: '编辑物料编码' })).toHaveText('—')
  await page.screenshot({ path: testInfo.outputPath('duplicate-material-choice.png'), fullPage: false })
  await duplicateDialog.getByRole('row').filter({ hasText: '01020000602' }).getByRole('button', { name: '确认选择' }).click()
  await expect(duplicateDialog).toHaveCount(0)
  await expect(row.getByRole('button', { name: '编辑物料编码' })).toHaveText('01020000602')
  await page.screenshot({ path: testInfo.outputPath('bom-duplicate-confirmation.png'), fullPage: false })
  expect({ consoleMessages, failedResponses }).toEqual({ consoleMessages: [], failedResponses: [] })
})

test('project numbers remain fully visible at the compact adaptive width', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 982, height: 994 })
  await page.goto('/')
  const loginForm = page.getByLabel('登录PLM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('engineer')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()

  await expect(page.locator('.pdm-project-sidebar__summary').getByText('PRJ-REAL-001 · 真实装配项目', { exact: true })).toBeVisible()
  await page.locator('.pdm-sidebar').getByRole('button', { name: '项目总览', exact: true }).click()
  await page.locator('.pdm-navigation-tabs').getByRole('tab', { name: '项目列表', exact: true }).click()
  const projectCode = page.getByRole('button', { name: '进入项目 PRJ-REAL-001' })
  await expect(projectCode).toBeVisible()
  const layout = await projectCode.evaluate(element => {
    const table = element.closest('table')
    const container = table?.closest('.pdm-project-number-scroll')
    return {
      codeClientWidth: element.clientWidth,
      codeScrollWidth: element.scrollWidth,
      tableScrollWidth: table?.scrollWidth ?? 0,
      containerClientWidth: container?.clientWidth ?? 0,
    }
  })
  expect(layout.codeScrollWidth).toBeLessThanOrEqual(layout.codeClientWidth)
  expect(layout.tableScrollWidth).toBeGreaterThan(layout.containerClientWidth)
  await page.screenshot({ path: testInfo.outputPath('project-number-compact.png'), fullPage: false })
})

test('operator columns display the user name instead of the account name', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => {
    if (message.type() === 'error' || message.type() === 'warning') errors.push(`${message.text()} ${message.location().url}`.trim())
  })
  await page.setViewportSize({ width: 1285, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: '图档', exact: true }).click()
  await expect(page.getByLabel('项目设计树')).toBeVisible()
  await page.locator('.pdm-tree-row').first().click({ button: 'right' })
  await page.getByRole('menuitem', { name: /查看\/对比历史版本/ }).click()

  const versionDrawer = page.getByRole('dialog', { name: '图档历史版本对比' })
  await expect(versionDrawer).toContainText('真实工程师')
  await expect(versionDrawer).not.toContainText('engineer')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
  await page.screenshot({ path: testInfo.outputPath('operator-display-name.png'), fullPage: false })
})

test('administrator switches independent company organization trees', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => {
    if (message.type() !== 'error' && message.type() !== 'warning') return
    const location = message.location().url
    if (location.endsWith('/api/auth/resume') && message.text().includes('404')) return
    errors.push(`${message.text()} ${location}`.trim())
  })
  await page.setViewportSize({ width: 1536, height: 864 })
  await page.addInitScript(() => localStorage.setItem('pdm_active_organization', 'org-ks'))
  await page.goto('/')
  const loginForm = page.getByLabel('登录PLM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('admin')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()

  await expect(page.locator('.pdm-project-sidebar__summary').getByText('PRJ-REAL-001 · 真实装配项目', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: '系统设置', exact: true }).click()
  await page.getByRole('button', { name: '用户设置', exact: true }).click()
  await page.getByLabel('用户设置功能').getByRole('button', { name: '组织关系', exact: true }).click()
  await expect(page.locator('.org-embedded-heading').getByText('组织关系', { exact: true })).toBeVisible()
  const companyTree = page.getByLabel('公司组织树')
  await expect(companyTree).toContainText('昆山自动化事业部')
  await expect(companyTree).not.toContainText('广州自动化事业部')
  await expect(page.getByLabel('组织详情')).toContainText('真实工程师')
  await expect(companyTree).toContainText('未分配人员2')

  await page.getByLabel('选择当前公司').selectOption('org-gz')
  await expect(page.getByRole('button', { name: '切换当前公司' })).toContainText('广州阿普顿自动化系统有限公司')
  await page.getByRole('button', { name: '系统设置', exact: true }).click()
  await page.getByRole('button', { name: '用户设置', exact: true }).click()
  await page.getByLabel('用户设置功能').getByRole('button', { name: '组织关系', exact: true }).click()
  const switchedCompanyTree = page.getByLabel('公司组织树')
  await expect(switchedCompanyTree).toContainText('广州自动化事业部')
  await expect(switchedCompanyTree).not.toContainText('昆山自动化事业部')
  await expect(page.getByLabel('组织详情')).toContainText('广州设计员')
  await expect.poll(() => page.evaluate(() => localStorage.getItem('pdm_active_organization'))).toBe('org-gz')

  await page.getByRole('button', { name: '公司管理', exact: true }).click()
  await expect(page.getByLabel('公司管理')).toContainText('昆山阿普顿自动化系统有限公司')
  await expect(page.getByLabel('公司管理')).toContainText('广州阿普顿自动化系统有限公司')
  await page.screenshot({ path: testInfo.outputPath('company-organization-management.png'), fullPage: false })
  await page.getByLabel('公司管理').getByRole('button', { name: '编辑', exact: true }).first().click()
  const companyDialog = page.getByRole('dialog', { name: '编辑公司' })
  const activeToggle = companyDialog.locator('.org-form-check').filter({ hasText: '启用' })
  await expect(activeToggle).toBeVisible()
  await expect(activeToggle.locator('input[type="checkbox"]')).toBeChecked()
  const companyAlignment = await activeToggle.evaluate(element => {
    const checkboxBox = element.querySelector('input[type="checkbox"]')!.getBoundingClientRect()
    const textBox = element.querySelector('span')!.getBoundingClientRect()
    return { checkboxX: checkboxBox.x, textX: textBox.x, centerDelta: Math.abs((checkboxBox.y + checkboxBox.height / 2) - (textBox.y + textBox.height / 2)) }
  })
  expect(companyAlignment.checkboxX).toBeLessThan(companyAlignment.textX)
  expect(companyAlignment.centerDelta).toBeLessThanOrEqual(1)
  await page.screenshot({ path: testInfo.outputPath('company-active-checkbox.png'), fullPage: false })
  await companyDialog.getByRole('button', { name: '取消', exact: true }).click()

  await page.getByLabel('用户设置功能').getByRole('button', { name: '组织关系', exact: true }).click()
  await page.getByRole('button', { name: '新建部门', exact: true }).click()
  const unitDialog = page.getByRole('dialog', { name: '新建部门' })
  for (const label of ['制造部门（可承接项目）', '启用']) {
    const toggle = unitDialog.locator('.org-form-check').filter({ hasText: label })
    const alignment = await toggle.evaluate(element => {
      const checkboxBox = element.querySelector('input[type="checkbox"]')!.getBoundingClientRect()
      const textBox = element.querySelector('span')!.getBoundingClientRect()
      return { checkboxX: checkboxBox.x, textX: textBox.x, centerDelta: Math.abs((checkboxBox.y + checkboxBox.height / 2) - (textBox.y + textBox.height / 2)) }
    })
    expect(alignment.checkboxX).toBeLessThan(alignment.textX)
    expect(alignment.centerDelta).toBeLessThanOrEqual(1)
  }
  await page.screenshot({ path: testInfo.outputPath('organization-checkbox-alignment.png'), fullPage: false })
  await unitDialog.getByRole('button', { name: '取消', exact: true }).click()
  await page.getByLabel('选择当前公司').selectOption('org-ks')
  await expect(page.getByRole('button', { name: '切换当前公司' })).toContainText('昆山阿普顿自动化系统有限公司')
  await page.getByRole('button', { name: '系统设置', exact: true }).click()
  await page.getByRole('button', { name: '用户设置', exact: true }).click()
  await page.getByLabel('用户设置功能').getByRole('button', { name: '组织关系', exact: true }).click()
  const returnedCompanyTree = page.getByLabel('公司组织树')
  await expect(returnedCompanyTree).toContainText('昆山设计部')
  await returnedCompanyTree.getByText('昆山设计部', { exact: true }).click()
  await page.getByLabel('组织详情').getByRole('button', { name: '编辑', exact: true }).click()
  const editUnitDialog = page.getByRole('dialog', { name: '编辑组织' })
  await expect(editUnitDialog.getByLabel('本级组织编码')).toHaveValue('DESIGN')
  await expect(editUnitDialog).toContainText('完整编码：KS-AUTO-DESIGN')
  await editUnitDialog.locator('label').filter({ hasText: '上级组织' }).locator('select').selectOption('ks-other')
  await expect(editUnitDialog).toContainText('完整编码：KS-OTHER-DESIGN')
  await page.screenshot({ path: testInfo.outputPath('organization-hierarchical-code.png'), fullPage: false })
  await editUnitDialog.getByRole('button', { name: '取消', exact: true }).click()
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('role permissions follow the system module directory and include future modules', async ({ page }, testInfo) => {
  const errors: string[] = []
  let savedPermissions: string[] = []
  const permissionDirectory = {
    permissions: [
      { code: 'project.view', name: '查看项目清单', module: '项目管理', description: '查看当前公司范围内的项目。', sensitive: false },
      { code: 'document.edit', dependencies: ['project.content.view', 'project.view'], action: 'edit', name: '编辑项目图档', module: '项目内容', description: '编辑图档并创建新版本。', sensitive: false },
      { code: 'project.content.view', dependencies: ['project.view'], action: 'view', name: '查看项目内容', module: '项目内容', description: '查看图档与业务内容。', sensitive: false },
      { code: 'future-module.view', name: '查看新增模块', module: '新增模块', description: '模拟后续发布的系统模块权限。', sensitive: false },
    ],
    roles: [
      { role: 'PlanningManager', name: '计划管理', description: '按所属公司分配项目执行事业部。', baseRole: 'PlanningManager', isSystem: true, isSystemAdministrator: false, permissions: ['project.view'], userCount: 2 },
    ],
  }
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => {
    if (message.type() === 'error' || message.type() === 'warning') errors.push(`${message.text()} ${message.location().url}`.trim())
  })
  await page.route('**/api/role-permissions/PlanningManager', route => {
    savedPermissions = route.request().postDataJSON().permissions
    return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(permissionDirectory),
    })
  })
  await page.route('**/api/role-permissions', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(permissionDirectory),
  }))

  await page.setViewportSize({ width: 1988, height: 1114 })
  await page.goto('/')
  const loginForm = page.getByLabel('登录PLM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('admin')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.locator('.pdm-project-sidebar__summary').getByText('PRJ-REAL-001 · 真实装配项目', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: '系统设置', exact: true }).click()
  const systemManagement = page.getByRole('region', { name: '系统管理', exact: true })
  await expect(systemManagement).toBeVisible()
  await systemManagement.getByRole('button', { name: '用户设置', exact: true }).click()
  await page.getByLabel('用户设置功能').getByRole('button', { name: '角色权限', exact: true }).click()

  const roleList = page.getByLabel('角色列表')
  await expect(roleList).toContainText('计划管理')
  await expect(roleList.getByRole('button', { name: '权限设置', exact: true })).toHaveCount(1)
  await expect(roleList.getByText('基础权限', { exact: true })).toHaveCount(0)
  await expect(roleList.getByText('单据权限', { exact: true })).toHaveCount(0)
  await roleList.getByRole('button', { name: '权限设置', exact: true }).click()

  const dialog = page.getByRole('dialog', { name: '角色权限' })
  await expect(dialog).toBeVisible()
  await expect(dialog).toContainText('权限模块由系统功能目录自动生成')
  await expect(dialog).toContainText('3 个模块、4 项权限')
  const modules = dialog.getByRole('navigation', { name: '权限模块' })
  await expect(modules).toContainText('项目管理')
  await expect(dialog).toContainText('查看项目清单')
  await modules.getByRole('button', { name: /项目内容/ }).click()
  await expect(dialog).toContainText('编辑项目图档')
  const edit = dialog.getByRole('checkbox', { name: /编辑项目图档/ })
  const view = dialog.getByRole('checkbox', { name: /查看项目内容/ })
  await edit.check()
  await expect(view).toBeChecked()
  await view.uncheck()
  await expect(edit).not.toBeChecked()
  await edit.check()
  await modules.getByRole('button', { name: /新增模块/ }).click()
  await expect(dialog).toContainText('查看新增模块')
  await dialog.getByRole('button', { name: '保存并立即生效', exact: true }).click()
  await expect.poll(() => [...savedPermissions].sort()).toEqual(['document.edit', 'project.content.view', 'project.view'])
  await expect(dialog).toBeVisible()
  await expect(dialog).toContainText('角色权限已保存并立即生效')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  // Existing app boot warnings from registering the shared dialog wrappers are unrelated to permissions.
  expect(errors.filter(message => !/Component "El(?:Drawer|Dialog)" has already been registered/.test(message))).toEqual([])
  await page.screenshot({ path: join(tmpdir(), 'pdm-role-permission-dependencies-desktop.png'), fullPage: false })
  await page.setViewportSize({ width: 834, height: 900 })
  await modules.getByRole('button', { name: /项目内容/ }).click()
  await expect(edit).toBeVisible()
  await page.screenshot({ path: join(tmpdir(), 'pdm-role-permission-dependencies-narrow.png'), fullPage: false })
})

test('custom project positions consume future directory operations without UI changes', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  const initialRules = { MainManager: [], ChildManager: [], MainDesigner: [], Engineer: ['bom.electrical.edit'], Other: [] }
  const future = { code: 'project.change.create', name: '创建项目变更', module: '项目变更', scope: 'project', projectAuthorization: 'position', action: 'create', sensitive: false, dependencies: [] }
  let saved: { rules: Record<string, string[]>; customPositions: Array<{ key: string; name: string; scope: string; isActive: boolean }> } | undefined
  await page.route('**/api/project-permissions', route => {
    if (route.request().method() === 'PUT') saved = route.request().postDataJSON()
    return route.fulfill({ json: { ...projectPermissionDirectory, rules: saved?.rules ?? initialRules, permissions: [...projectPermissionDirectory.permissions!, future], customPositions: saved?.customPositions ?? [], positions: [...projectPermissionDirectory.positions!, ...(saved?.customPositions ?? [])] } })
  })
  await page.setViewportSize({ width: 1518, height: 855 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '系统设置', exact: true }).click()
  await page.getByRole('button', { name: '项目权限', exact: true }).click()
  const panel = page.getByRole('region', { name: '项目岗位授权规则', exact: true })
  await panel.getByText('岗位管理及适用范围（5类）', { exact: true }).click()
  await panel.getByRole('button', { name: '新增岗位', exact: true }).click()
  await panel.locator('input[aria-label$="岗位名称"]').fill('变更复核')
  await panel.getByLabel('变更复核岗位范围', { exact: true }).selectOption('subtree')
  await panel.getByLabel('功能模块', { exact: true }).selectOption('项目变更')
  const permission = panel.getByRole('checkbox', { name: '变更复核：创建项目变更（项目变更）', exact: true })
  await permission.check()
  await panel.getByRole('button', { name: '保存并立即生效' }).click()
  await expect.poll(() => saved?.customPositions[0]?.scope).toBe('subtree')
  const position = saved!.customPositions[0]!
  expect(position.key).toMatch(/^custom-[a-f0-9]{32}$/)
  expect(saved!.rules[position.key]).toEqual([future.code])
  expect(saved!.rules.Engineer).toEqual(initialRules.Engineer)
  await panel.getByText('岗位管理及适用范围（6类）', { exact: true }).click()
  await expect(permission).toBeChecked()
  await page.screenshot({ path: join(tmpdir(), 'pdm-custom-position-future-operation.png'), fullPage: false })
  await page.setViewportSize({ width: 792, height: 855 })
  await permission.scrollIntoViewIfNeeded()
  await expect(permission).toBeVisible()
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('project permission matrix preserves budget and other positions when saving over HTTP', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  const initialRules = { MainManager: [], ChildManager: [], MainDesigner: [], Engineer: ['bom.electrical.edit'], Other: [] }
  let savedRules: Record<string, string[]> | undefined
  await page.route('**/api/project-permissions', route => {
    if (route.request().method() === 'PUT') savedRules = route.request().postDataJSON().rules
    return route.fulfill({ json: { ...projectPermissionDirectory, rules: savedRules ?? initialRules } })
  })
  await page.setViewportSize({ width: 1518, height: 855 })
  await page.goto('/')
  await expect(page).toHaveTitle(/PLM|UPLM|PDM/)
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '系统设置', exact: true }).click()
  await page.getByRole('button', { name: '项目权限', exact: true }).click()
  const panel = page.getByRole('region', { name: '项目岗位授权规则', exact: true })
  const matrix = panel.getByRole('region', { name: '功能与岗位授权矩阵' })
  await expect(matrix.getByRole('checkbox')).toHaveCount(55)
  await expect(matrix.locator('input:checked')).toHaveCount(1)
  await expect(panel).toContainText('预算查看可由角色独立授权')
  await panel.getByText('岗位管理及适用范围（5类）', { exact: true }).click()
  await expect(panel.getByLabel('项目岗位适用范围')).toContainText('仅负责的子项目')
  await panel.getByText('岗位管理及适用范围（5类）', { exact: true }).click()
  await matrix.getByRole('checkbox', { name: '主项目经理／协同项目经理：编辑项目信息（项目管理）', exact: true }).check()
  await matrix.getByRole('checkbox', { name: '主设：维护质量计划（质量）', exact: true }).check()
  await panel.getByRole('button', { name: '保存并立即生效' }).click()
  await expect.poll(() => savedRules).toEqual({ ...initialRules, MainManager: ['project.edit'], MainDesigner: ['validation-plan.edit'] })
  await expect(matrix.getByRole('checkbox', { name: '执行工程师／电气硬件：电气件BOM（BOM）', exact: true })).toBeChecked()
  await expect(matrix.getByRole('checkbox', { name: '主项目经理／协同项目经理：查看项目预算（预算）', exact: true })).not.toBeChecked()
  await expect(panel.getByRole('button', { name: '保存并立即生效' })).toBeDisabled()
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  await matrix.getByRole('checkbox', { name: '主项目经理／协同项目经理：查看项目预算（预算）', exact: true }).scrollIntoViewIfNeeded()
  await page.screenshot({ path: join(tmpdir(), 'pdm-project-permission-grants-desktop.png'), fullPage: false })
  await page.setViewportSize({ width: 792, height: 855 })
  const other = matrix.getByRole('checkbox', { name: '其他人员：编辑项目信息（项目管理）', exact: true })
  await other.check()
  await expect(other).toBeChecked()
  await panel.getByRole('button', { name: '撤销未保存修改' }).click()
  await expect(other).not.toBeChecked()
  await expect(panel.getByRole('button', { name: '保存并立即生效' })).toBeDisabled()
  const scroll = panel.getByLabel('岗位授权表，可横向滚动')
  expect(await scroll.evaluate(element => element.scrollWidth > element.clientWidth)).toBe(true)
  await matrix.getByRole('checkbox', { name: '主项目经理／协同项目经理：查看项目预算（预算）', exact: true }).scrollIntoViewIfNeeded()
  expect(new URL(page.url()).protocol).toBe('http:')
  await page.screenshot({ path: join(tmpdir(), 'pdm-project-permission-grants-narrow.png'), fullPage: false })
  expect(errors).toEqual([])
})

for (const scale of [
  { name: '125-percent', width: 1536, height: 864 },
  { name: '150-percent', width: 1280, height: 720 },
]) {
  test(`workspace remains fixed and interactive at ${scale.name} logical viewport`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: scale.width, height: scale.height })
    await page.goto('/')
    const loginForm = page.getByLabel('登录PLM')
    await loginForm.getByRole('textbox', { name: '账号' }).fill('engineer')
    await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
    await loginForm.getByRole('button', { name: '登录', exact: true }).click()
    const shellLayout = await page.evaluate(() => {
      const sidebar = document.querySelector<HTMLElement>('.pdm-sidebar')?.getBoundingClientRect()
      const titlebar = document.querySelector<HTMLElement>('.pdm-titlebar')?.getBoundingClientRect()
      const labels = [...document.querySelectorAll<HTMLElement>('.pdm-nav-item span')]
      return {
        sidebarWidth: sidebar?.width ?? 0,
        titlebarHeight: titlebar?.height ?? 0,
        clippedLabels: labels.filter(label => label.scrollWidth > label.clientWidth).map(label => label.textContent),
      }
    })
    expect(shellLayout.sidebarWidth).toBe(155)
    expect(shellLayout.titlebarHeight).toBe(62)
    expect(shellLayout.clippedLabels).toEqual([])
    await enterProject(page)
    await page.getByRole('button', { name: '图档', exact: true }).click()
    await expect(page.getByLabel('项目设计树')).toBeVisible()

    const projectHeaderLayout = await page.evaluate(() => {
      const sidebar = document.querySelector<HTMLElement>('.pdm-project-sidebar')?.getBoundingClientRect()
      const context = document.querySelector<HTMLElement>('.pdm-project-sidebar__context')?.getBoundingClientRect()
      return { sidebarLeft: sidebar?.left ?? -1, contextLeft: context?.left ?? -1, sidebarTop: sidebar?.top ?? -1, contextTop: context?.top ?? -1, contextBottom: context?.bottom ?? -1 }
    })
    expect(projectHeaderLayout.contextLeft).toBe(projectHeaderLayout.sidebarLeft)
    expect(projectHeaderLayout.contextTop).toBeLessThan(projectHeaderLayout.sidebarTop)
    expect(projectHeaderLayout.contextBottom).toBeLessThanOrEqual(projectHeaderLayout.sidebarTop)

    const layout = await page.evaluate(() => {
      const tree = document.querySelector<HTMLElement>('[role="tree"]')
      const shell = document.querySelector<HTMLElement>('.pdm-app-shell')
      return {
        bodyClientHeight: document.body.clientHeight,
        bodyScrollHeight: document.body.scrollHeight,
        shellClientHeight: shell?.clientHeight ?? 0,
        shellScrollHeight: shell?.scrollHeight ?? 0,
        treeClientHeight: tree?.clientHeight ?? 0,
        treeScrollHeight: tree?.scrollHeight ?? 0,
      }
    })
    expect(layout.bodyScrollHeight).toBe(layout.bodyClientHeight)
    expect(layout.shellScrollHeight).toBe(layout.shellClientHeight)
    expect(layout.treeScrollHeight).toBeGreaterThan(layout.treeClientHeight)

    const navCases = [
      ['概览', '工作台主页面'],
      ['文件', '项目文件夹'],
      ['图档', '项目设计树'],
      ['BOM', 'BOM维护'],
      ['发布', '发布总览'],
      ['记录', '审计查询'],
    ] as const
    for (const [buttonName, panelName] of navCases) {
      const button = page.getByRole('button', { name: buttonName, exact: true })
      await expect(button).toBeEnabled()
      await button.click()
      await expect(page.getByLabel(panelName).or(page.getByText(panelName, { exact: true })).first()).toBeVisible()
    }

    await page.getByRole('button', { name: '图档', exact: true }).click()
    await page.locator('.pdm-tree-row').first().click({ button: 'right' })
    await page.getByRole('menuitem', { name: /查看\/对比历史版本/ }).click()
    await expect(page.getByText('图档历史版本对比')).toBeVisible()
    await expect(page.getByRole('button', { name: '只读打开左版' })).toHaveCount(0)
    await expect(page.getByRole('button', { name: '下载左版原始文件' })).toBeEnabled()
    const drawerLayout = await page.locator('.el-drawer__body').evaluate(element => ({ clientWidth: element.clientWidth, scrollWidth: element.scrollWidth }))
    expect(drawerLayout.scrollWidth).toBeLessThanOrEqual(drawerLayout.clientWidth)

    await page.screenshot({ path: testInfo.outputPath(`workspace-${scale.name}.png`), fullPage: false })
  })
}

test('controlled folders expose selection controls without recycle permission', async ({ page }, testInfo) => {
  const consoleErrors: string[] = []
  page.on('pageerror', error => consoleErrors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()) })
  await page.setViewportSize({ width: 1925, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: '文件', exact: true }).click()
  await page.locator('.pdm-folder-pane .el-tree-node').filter({ hasText: 'PRJ-REAL-001-0' }).first().click()

  const table = page.getByRole('table', { name: '受控图档' })
  const selectAll = table.getByRole('checkbox', { name: '选择当前列表全部受控图档' })
  await expect(selectAll).toBeVisible()
  await selectAll.check()
  await expect(table.locator('tbody input[type="checkbox"]:checked')).toHaveCount(3)
  await page.screenshot({ path: testInfo.outputPath('controlled-folder-selection.png'), fullPage: false })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(consoleErrors).toEqual([])
})

test('project file list provides a direct preview link and editable description', async ({ page }, testInfo) => {
  let savedDescription = ''
  await page.route(`**/api/projects/${projectId}/folders`, route => route.fulfill({ json: [
    { id: 'folder-root', rootProjectId: projectId, parentFolderId: null, targetProjectId: projectId, folderKey: 'root', templateKey: 'root', name: 'PRJ-REAL-001', purpose: 0, sortOrder: 0, isSystem: true, inheritPermissions: true, effectiveAccess: 127, permissions: [] },
    { id: 'folder-project-files', rootProjectId: projectId, parentFolderId: 'folder-root', targetProjectId: projectId, folderKey: 'project-files', templateKey: 'project-files', name: '项目资料', purpose: 5, sortOrder: 10, isSystem: true, inheritPermissions: true, effectiveAccess: 31, permissions: [] },
  ] }))
  await page.route(`**/api/projects/${projectId}/files?*`, route => route.fulfill({ json: [{
    id: 'project-file-pdf', rootProjectId: projectId, folderId: 'folder-project-files', fileName: '项目交付清单.pdf', description: savedDescription,
    createdBy: 'engineer', createdAt: '2026-09-23T12:00:00Z', updatedBy: 'engineer', updatedAt: '2026-09-23T12:00:00Z',
    currentVersion: { id: 'project-file-pdf-v1', projectFileId: 'project-file-pdf', versionNumber: 1, fileName: '项目交付清单.pdf', fileLength: 1024, sha256: 'A'.repeat(64), uploadedBy: 'engineer', uploadedAt: '2026-09-23T12:00:00Z' },
  }] }))
  await page.route(`**/api/projects/${projectId}/files/project-file-pdf/description`, async route => {
    savedDescription = (route.request().postDataJSON() as { description: string }).description
    await route.fulfill({ json: { id: 'project-file-pdf', rootProjectId: projectId, folderId: 'folder-project-files', fileName: '项目交付清单.pdf', description: savedDescription } })
  })
  await page.route(`**/api/projects/${projectId}/files/project-file-pdf/content?*`, route => route.fulfill({ status: 200, contentType: 'application/pdf', body: '%PDF-1.4' }))
  await page.addInitScript(() => {
    ;(window as unknown as { previewUrls: string[] }).previewUrls = []
    window.open = ((url?: string | URL) => { (window as unknown as { previewUrls: string[] }).previewUrls.push(String(url)); return null }) as typeof window.open
    window.setInterval = (() => 0) as unknown as typeof window.setInterval
  })
  await page.setViewportSize({ width: 1925, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: '文件', exact: true }).click()
  await page.getByText('项目资料', { exact: true }).click()

  const table = page.getByRole('table', { name: '项目资料文件' })
  await expect(table).toBeVisible()
  await expect(table.getByRole('columnheader')).toHaveText(['', '文件名', '说明', '版本', '大小', '上传人', '更新时间', '操作'])
  await table.getByRole('button', { name: '项目交付清单.pdf', exact: true }).click()
  await expect.poll(() => page.evaluate(() => (window as unknown as { previewUrls: string[] }).previewUrls.length)).toBe(1)
  await table.getByRole('button', { name: '编辑 项目交付清单.pdf 的说明' }).click()
  const descriptionInput = table.getByRole('textbox', { name: '编辑 项目交付清单.pdf 的说明' })
  await expect(descriptionInput).toBeVisible()
  await expect(page.getByRole('dialog', { name: '编辑文件说明' })).toHaveCount(0)
  await descriptionInput.fill('供现场验收使用')
  await descriptionInput.press('Enter')
  await expect.poll(() => savedDescription).toBe('供现场验收使用')
  await expect(table).toContainText('供现场验收使用')
  await page.screenshot({ path: testInfo.outputPath('project-file-preview-and-description.png'), fullPage: false })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
})

test('summary material check includes hidden instances and exposes their failures', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  const ids = ['coded-instance', 'hidden-instance-1', 'hidden-instance-2', 'hidden-instance-3']
  const requests: string[][] = []
  const material = { id: 'master', materialCode: '01021000088', name: 'HFC71-50', specification: 'HFC71-50',
    brand: '国优', kind: 'Standard', approvalStatus: 'Approved', isArchived: false, unitCode: '001', u9SyncConfirmed: true }
  await page.route('**/boms/Standard?*', route => route.fulfill({ json: [{ id: ids[0], kind: 'Standard', sequence: 1,
    drawingNumber: material.materialCode, name: material.name, specification: material.specification, brand: material.brand,
    quantity: 4, unit: '001', revision: 'W1', isComplete: true, source: 'FromDrawing',
    summaryItemIds: ids, summaryFingerprint: 'four-instances' }] }))
  await page.route('**/api/material-code/resolve', async route => {
    const selected = route.request().postDataJSON().bomItemIds as string[]
    requests.push(selected)
    await route.fulfill({ json: selected.map(id => ({ bomItemId: id, status: id === ids[0] ? 'Verified' : 'CodeNotFound',
      material: id === ids[0] ? material : null, candidates: [], application: null,
      issues: id === ids[0] ? [] : ['料号不存在于料品主档'] })) })
  })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: /标准件BOM/ }).click()
  await expect(page.getByRole('button', { name: '查看问题详情：料号不存在', exact: true })).toBeVisible()
  await page.locator('.pdm-bom-table').getByRole('checkbox', { name: '选择物料', exact: true }).check()
  await page.getByRole('button', { name: '核对料号', exact: true }).click()
  await expect(page.getByText(/已核对 4 行.*需人工维护 3 项/).first()).toBeVisible()
  expect(requests.at(-1)).toEqual(ids)
  await expect(page.locator('.pdm-edit-table tbody tr').first().locator('input[type="checkbox"]').first()).not.toBeChecked()
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(await page.title()).toContain('UPTON-PLM')
  await page.screenshot({ path: join(tmpdir(), 'standard-summary-material-check.png') })
  expect(errors).toEqual([])
})

test('unsaved new standard rows report unknown codes in check results and issue cells', async ({ page }, testInfo) => {
  test.setTimeout(120_000)
  await page.addInitScript(() => Object.defineProperty(window.crypto, 'randomUUID', { value: undefined, configurable: true }))
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.route('**/boms/Standard?*', route => route.fulfill({ json: [] }))
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [] }))
  await page.route('**/api/material-code/validate', async route => {
    const { items } = route.request().postDataJSON()
    await route.fulfill({ json: items.map((item: { id: string }) => ({ bomItemId: item.id, status: 'CodeNotFound',
      material: null, candidates: [], application: null, issues: ['料号不存在于料品主档'] })) })
  })
  await page.route('**/api/materials/matches?*', route => route.fulfill({ json: [] }))
  await page.goto('/', { timeout: 90_000 })
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: /标准件BOM/ }).click()
  for (const [index, code] of ['01030000968', '01030000666'].entries()) {
    await page.locator('tr.is-quick-entry').getByLabel('物料名称', { exact: true }).fill(`测试${index + 1}`)
    await page.locator('tr.is-quick-entry').getByLabel('物料名称', { exact: true }).press('Tab')
    await page.locator('tbody tr:not(.is-quick-entry)').last().getByLabel('物料编码', { exact: true }).fill(code)
  }
  await page.getByRole('button', { name: '核对料号', exact: true }).click()
  await expect(page.getByText(/已核对 2 行.*需人工维护 2 项/).first()).toBeVisible()
  for (const input of await page.locator('tbody tr:not(.is-quick-entry)').getByLabel('物料编码', { exact: true }).all()) await expect(input).toHaveValue('')
  for (const input of await page.locator('.pdm-bom-table').getByRole('checkbox', { name: '选择物料', exact: true }).all()) await expect(input).not.toBeChecked()
  await page.screenshot({ path: testInfo.outputPath('unsaved-codes-checked.png') })
  expect(errors).toEqual([])
})

test('electrical BOM exposes code verification and application actions', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.route('**/boms/Electrical**', route => route.fulfill({ json: [{ id: 'electrical-code', kind: 'Electrical', sequence: 1,
    drawingNumber: 'INVALID-ELECTRICAL', name: '电气模块', specification: 'FX20', brand: '品牌', quantity: 1, unit: '001', revision: 'W1', isComplete: true }] }))
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [{ bomItemId: 'electrical-code', status: 'CodeNotFound', material: null, candidates: [], issues: ['料号不存在于料品主档'] }] }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: /电气件BOM/ }).click()
  await page.locator('.pdm-bom-table').getByRole('checkbox', { name: '选择物料', exact: true }).check()
  await page.getByRole('button', { name: '核对料号', exact: true }).click()
  await expect(page.locator('.pdm-bom-table')).not.toContainText('INVALID-ELECTRICAL')
  await expect(page.locator('.pdm-bom-table').getByRole('button', { name: '申请料号', exact: true })).toBeVisible()
  await expect(page.locator('.pdm-bom-selection-actions').getByRole('button', { name: '申请料号', exact: true })).toBeDisabled()
  await page.locator('.pdm-bom-table').getByRole('checkbox', { name: '选择物料', exact: true }).check()
  await expect(page.locator('.pdm-bom-selection-actions').getByRole('button', { name: '申请料号', exact: true })).toBeEnabled()
  await page.screenshot({ path: testInfo.outputPath('electrical-material-code-actions.png') })
  expect(errors).toEqual([])
})

test('manual unknown standard code is detected and a rejected save keeps the edit', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.route('**/boms/Standard?*', async route => {
    if (route.request().method() === 'PUT') {
      await route.fulfill({ status: 400, json: { message: '料号不存在于料品主档：01021812461。本次BOM未保存。' } })
      return
    }
    await route.fulfill({ json: [{ id: 'manual-invalid', kind: 'Standard', sequence: 1, drawingNumber: '01021812460', name: '手动新增', specification: 'MODEL', brand: 'TEST',
      quantity: 1, unit: '001', revision: 'W1', isComplete: true, source: 'Manual', summaryItemIds: ['manual-invalid'], summaryFingerprint: 'snapshot' }] })
  })
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [{ bomItemId: 'manual-invalid', status: 'CodeNotFound', material: null, candidates: [], application: null, issues: ['料号不存在于料品主档'] }] }))
  await page.route('**/api/materials/matches?*', route => route.fulfill({ json: [] }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: /标准件BOM/ }).click()
  await expect(page.getByRole('button', { name: '查看问题详情：料号不存在', exact: true })).toBeVisible()
  const row = page.locator('.pdm-edit-table tbody tr').first()
  await row.getByRole('button', { name: '编辑物料编码', exact: true }).click()
  await row.getByLabel('内联编辑物料编码').fill('01021812461')
  await row.getByLabel('内联编辑物料编码').press('Enter')
  await expect(page.getByRole('button', { name: /保存BOM/ })).toBeEnabled()
  await page.getByRole('button', { name: /保存BOM/ }).click()
  await expect(page.getByText(/本次BOM未保存/).first()).toBeVisible()
  await expect(row.getByRole('button', { name: '编辑物料编码', exact: true })).toHaveText('01021812461')
  await page.screenshot({ path: testInfo.outputPath('manual-code-rejected.png') })
  expect(errors).toEqual([])
})

test('material master exits a stalled query with a timeout message', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.route('**/api/materials/page**', () => {})
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '料品管理', exact: true }).click()
  const spinner = page.locator('.material-table-shell .pdm-square-loader')
  await expect(spinner).toBeVisible()
  await expect(spinner).toHaveCount(0, { timeout: 18_000 })
  await expect(page.getByText('料品主档查询超时，请重试或缩小查询范围。', { exact: true }).first()).toBeVisible()
  expect(errors).toEqual([])
})

test('material master shares duplicate queries and keeps the latest search result', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  const queries: string[] = []
  let finishOld!: () => void
  await page.route('**/api/materials/page**', async route => {
    const query = new URL(route.request().url()).searchParams.get('query') || ''
    queries.push(query)
    if (query === '慢查询') await new Promise<void>(resolve => { finishOld = resolve })
    await route.fulfill({ json: { items: [{ id: 'responsive-material', materialCode: '01020015047',
      name: query === '慢查询' ? '迟到的旧结果' : query ? '精确料号查询结果' : '首屏料品',
      kind: 'Standard', supplyMode: 'Purchase', unitCode: '001', specification: 'ADN-16-50-A-P-A', brand: 'FESTO',
      approvalStatus: 'Approved', syncStatus: 'Succeeded', createdAt: '2026-10-08T01:00:00Z',
      updatedAt: '2026-10-08T01:00:00Z', referenceCount: 0 }], total: 1, page: 1, pageSize: 50 } }).catch(() => {})
  })
  await page.setViewportSize({ width: 1920, height: 1000 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '料品管理', exact: true }).click()
  await expect(page.locator('.material-table')).toContainText('首屏料品')
  expect(queries.filter(query => !query)).toHaveLength(1)
  const search = page.getByPlaceholder('搜索编码、名称、规格或分类')
  await search.fill('慢查询')
  await page.locator('.material-toolbar__search-button').click()
  await expect.poll(() => queries.filter(query => query === '慢查询').length).toBe(1)
  await page.locator('.material-toolbar__search-button').click()
  await search.fill('01020015047')
  await page.locator('.material-toolbar__search-button').click()
  await expect(page.locator('.material-table')).toContainText('精确料号查询结果')
  finishOld()
  expect(queries.filter(query => query === '慢查询')).toHaveLength(1)
  await expect(page.locator('.material-table')).not.toContainText('迟到的旧结果')
  await expect(page.getByText('正在查询料品主档')).toHaveCount(0)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(await page.title()).toBe('UPTON-PLM')
  expect(errors).toEqual([])
  await page.screenshot({ path: testInfo.outputPath('material-latest-query.png'), fullPage: false })
})

test('material badge matches ordinary pending work and stays clear after refresh over LAN HTTP', async ({ page }) => {
  await mockPlanCompanionPanels(page)
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.route('**/api/workbench*', route => route.fulfill({ json: { views: [], view: 'manager', projects: [], risks: [] } }))
  let ordinaryStatus = ''
  const syncUrls: string[] = []
  const header = { id: 'automatic-header', materialId: 'header-material', bomHeaderKind: 'Standard', operation: 'Create', status: 'Failed', materialCode: 'P-HEADER', createdAt: '2026-10-23T00:00:00Z', updatedAt: '2026-10-23T00:00:00Z' }
  await page.route('**/api/material-sync-tasks*', route => {
    syncUrls.push(route.request().url())
    return route.fulfill({ json: [header, ...(ordinaryStatus ? [{ ...header, id: 'ordinary', materialId: 'ordinary-material', bomHeaderKind: null,
      materialCode: '01020001', materialName: '待处理普通料品', status: ordinaryStatus, lastError: '请重新同步', payloadJson: '[]' }] : [])] })
  })
  await page.route('**/api/material-code/applications*', route => route.fulfill({ json: [{ id: 'header-application', projectId, materialId: 'header-material',
    applicationType: 'BomHeader', bomHeaderKind: 'Standard', status: 'Pending', workflowState: 'PendingApproval', canDecide: true, requestedBy: 'admin', requestedAt: '2026-10-23T00:00:00Z' }] }))
  await page.goto('/')
  await expect(page).toHaveTitle('UPTON-PLM')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', {name:'账号'}).fill('admin')
  await login.getByRole('textbox', {name:'密码'}).fill('correct-password')
  await login.getByRole('button', {name:'登录',exact:true}).click()
  const sidebar = page.locator('.pdm-sidebar')
  const materialNav = sidebar.getByRole('button', {name:/料品管理/})
  await expect(materialNav).toBeVisible()
  await expect(materialNav.locator('em')).toHaveCount(0)
  await materialNav.click()
  await page.getByRole('tab', {name:/料号审批/}).click()
  const workflow = page.getByLabel('料号审批与U9C同步', {exact:true})
  await expect(workflow.locator('tbody')).not.toContainText('P-HEADER')
  await expect(materialNav.locator('em')).toHaveCount(0)
  await sidebar.getByRole('button', {name:/我的待办/}).click()
  await expect(materialNav.locator('em')).toHaveCount(0)
  ordinaryStatus = 'Failed'
  await materialNav.click()
  await sidebar.getByRole('button', {name:/我的待办/}).click()
  await expect(materialNav.locator('em')).toHaveText('1')
  await materialNav.click()
  await page.locator('.material-code-refresh-toolbar').getByRole('button', {name:'刷新',exact:true}).click()
  await expect(workflow).toContainText('待处理普通料品')
  await expect(materialNav.locator('em')).toHaveText('1')
  ordinaryStatus = 'Succeeded'
  await page.locator('.material-code-refresh-toolbar').getByRole('button', {name:'刷新',exact:true}).click()
  await expect(workflow).not.toContainText('待处理普通料品')
  await expect(materialNav.locator('em')).toHaveCount(0)
  await sidebar.getByRole('button', {name:/我的待办/}).click()
  await expect(materialNav.locator('em')).toHaveCount(0)
  await materialNav.click()
  await page.screenshot({path:join(tmpdir(),'pdm-material-badge-consistency.png'),animations:'disabled'})
  expect(syncUrls.length).toBeGreaterThan(3)
  expect(syncUrls.every(url => url.includes('ordinaryOnly=true'))).toBe(true)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('material master loads and searches without waiting for background approvals', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  let approvalRequests = 0
  await page.route('**/api/material-code/applications', () => { approvalRequests++ })
  const queries: string[] = []
  await page.route('**/api/materials/page**', async route => {
    const query = new URL(route.request().url()).searchParams.get('query') || ''
    queries.push(query)
    await route.fulfill({ json: { items: [{ id: 'fast-material', materialCode: '01020001', name: query ? '搜索结果气缸' : '首屏气缸',
      kind: 'Standard', supplyMode: 'Purchase', unitCode: '001', specification: 'MDBB32', brand: 'SMC',
      approvalStatus: 'Approved', syncStatus: 'Succeeded', createdAt: '2026-10-08T01:00:00Z', updatedAt: '2026-10-08T01:00:00Z', referenceCount: 0 }], total: 1, page: 1, pageSize: 50 } })
  })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '料品管理', exact: true }).click()
  await expect(page.locator('.material-table')).toContainText('首屏气缸')
  await expect(page.getByText('正在加载料品数据')).toHaveCount(0)
  await page.getByPlaceholder('搜索编码、名称、规格或分类').fill('MDBB32')
  await page.locator('.material-toolbar__search-button').click()
  await expect(page.locator('.material-table')).toContainText('搜索结果气缸')
  expect(queries).toContain('MDBB32')
  expect(approvalRequests).toBe(0)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
  await page.screenshot({ path: testInfo.outputPath('material-responsive-search.png'), fullPage: false })
})

test('administrator can select one archived material and open the reactivate confirmation', async ({ page }, testInfo) => {
  const consoleErrors: string[] = []
  const failedResponses: string[] = []
  page.on('console', message => {
    if (message.type() === 'error') consoleErrors.push(message.text())
  })
  page.on('response', response => {
    if (response.status() >= 400) failedResponses.push(`${response.status()} ${response.url()}`)
  })
  await page.setViewportSize({ width: 956, height: 1114 })
  await page.goto('/')
  const loginForm = page.getByLabel('登录PLM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('admin')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.locator('.pdm-project-detail')).toBeVisible({ timeout: 15_000 })
  await page.waitForLoadState('networkidle')
  const materialNav = page.getByRole('button', { name: '料品管理', exact: true })
  await expect(materialNav).toBeVisible()
  await materialNav.click()
  await expect(materialNav).toHaveAttribute('aria-current', 'page')
  await expect(page.getByRole('tab', { name: '料品主档', exact: true })).toBeVisible()

  await page.getByText('显示已停用', { exact: true }).click()
  const archivedRow = page.locator('.material-table tbody tr').filter({ hasText: '01021000001' })
  await expect(archivedRow).toHaveCount(1)
  await archivedRow.locator('label.el-checkbox').click()

  const toolbar = page.locator('.material-toolbar')
  await expect(toolbar.getByRole('button', { name: '启用', exact: true })).toBeEnabled()
  await expect(toolbar.getByRole('button', { name: '停用', exact: true })).toBeDisabled()
  const toolbarLayout = await toolbar.evaluate(element => ({
    clientWidth: element.clientWidth,
    scrollWidth: element.scrollWidth,
  }))
  expect(toolbarLayout.scrollWidth).toBeLessThanOrEqual(toolbarLayout.clientWidth)

  await toolbar.getByRole('button', { name: '启用', exact: true }).click()
  await expect(page.getByRole('dialog')).toContainText('启用料品')
  await expect(page.getByRole('dialog')).toContainText('保留原料号、审批状态和历史记录')
  await page.screenshot({ path: testInfo.outputPath('material-reactivation-confirmation.png'), fullPage: false })
  await page.getByRole('dialog').getByRole('button', { name: '取消', exact: true }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  expect({ consoleErrors, failedResponses }).toEqual({ consoleErrors: [], failedResponses: [] })
})

test('administrator can configure category approval rules and 0204 defaults to no review', async ({ page }, testInfo) => {
  const categories = [
    { code: '0102', name: '机械外购件', parentCode: null, pdmKind: 'Standard', defaultSupplyMode: 'Purchase', allowCreate: true, isVisible: true, isActive: true, numberPrefix: '0102', sequenceLength: 7, counterScope: '0102', sortOrder: 1, updatedBy: 'admin', updatedAt: '2026-09-01T01:00:00Z', rowVersion: 1 },
    { code: '0204', name: '非标机加件', parentCode: null, pdmKind: 'NonStandard', defaultSupplyMode: 'Manufacture', allowCreate: true, isVisible: true, isActive: true, numberPrefix: '0204', sequenceLength: 7, counterScope: '0204', sortOrder: 2, updatedBy: 'admin', updatedAt: '2026-09-01T01:00:00Z', rowVersion: 1 },
  ]
  const approvalRules = [
    { categoryCode: '0102', requiresApproval: true },
    { categoryCode: '0204', requiresApproval: false },
  ]
  let savedRules: typeof approvalRules | null = null
  await page.route('**/api/material-categories?*', route => route.fulfill({ json: categories }))
  await page.route('**/api/material-approval-rules', route => {
    if (route.request().method() === 'PUT') {
      savedRules = (route.request().postDataJSON() as { rules: typeof approvalRules }).rules
      return route.fulfill({ json: savedRules })
    }
    return route.fulfill({ json: approvalRules })
  })
  await page.route('**/api/material-duplicate-rules', route => route.fulfill({ json: [
    { categoryCode: '0102', fields: ['Specification', 'Brand'] },
    { categoryCode: '0204', fields: ['Specification', 'Material'] },
  ] }))

  const consoleErrors: string[] = []
  page.on('pageerror', error => consoleErrors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()) })
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.goto('/')
  await expect(page).toHaveTitle('UPTON-PLM')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.locator('.pdm-project-detail')).toBeVisible({ timeout: 15_000 })
  await page.getByRole('button', { name: '料品管理', exact: true }).click()
  await page.getByRole('tab', { name: '取号设置', exact: true }).click()

  const settings = page.getByLabel('料品查重与审核规则设置')
  await expect(settings).toBeVisible()
  await expect(settings).toContainText('全局查重规则（按分类配置字段）')
  await expect(settings).toContainText('分类不参与查重')
  const nonStandardRule = settings.locator('.material-duplicate-rule-row').filter({ hasText: '0204 非标机加件' })
  await expect(nonStandardRule).toContainText('免审核')
  await expect(nonStandardRule.getByRole('switch')).not.toBeChecked()
  await settings.getByRole('button', { name: '保存规则', exact: true }).click()
  await expect.poll(() => savedRules).toEqual(approvalRules)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(consoleErrors).toEqual([])
  await page.screenshot({ path: testInfo.outputPath('material-approval-rules.png'), fullPage: false })
})

test('retired notification settings stay absent and production flow cards and tag close retain their native layout', async ({ page }, testInfo) => {
  await mockPlanCompanionPanels(page)
  const flowNodes = defaultProductionFlow(), packingId = flowNodes.find(node => node.post === 'Packing')!.id
  const roles = [{ role: 'manager', name: '机加主管' }, { role: 'worker', name: '机加人员' }]
  const catalog = { companyId: 'org-ks', rowVersion: 1, flowMode: 'DepartmentHandover', flowNodes, controlRoleCodes: [], labelWidthMm: 60, labelHeightMm: 40,
    workstations: [{ id: 'packing', name: '包装标签工位', resourceGroup: '', isActive: true }],
    bindings: roles.map((role, index) => ({ id: `binding-${index}`, flowNodeId: packingId, post: 'Packing', subjectType: 'Role', subject: role.role, workstationIds: ['packing'], isActive: true })) }
  let saved: typeof catalog | null = null
  const errors: string[] = [], legacyRequests: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  page.on('request', request => { if (request.url().includes('/settings/release-notifications')) legacyRequests.push(request.url()) })
  await page.route('**/*jelly*.html*', route => route.fulfill({ contentType: 'text/html', body: '<html><body></body></html>' }))
  await page.route('**/api/auth/login', route => route.fulfill({ json: { accessToken: 'e2e-token', expiresAt: '2099-01-01T00:00:00Z', username: 'admin', displayName: '系统管理员', role: 'Administrator', permissions: ['project.view', 'project.content.view', 'settings.storage.manage', 'production.scan.manage'], activeCompanyId: 'org-ks', primaryCompanyId: 'org-ks', activeCompanyName: '昆山阿普顿自动化系统有限公司', accessibleCompanies: [{ id: 'org-ks', name: '昆山阿普顿自动化系统有限公司', code: '7' }] } }))
  await page.route('**/api/production-tracking/scan-settings/options', route => route.fulfill({ json: { users: [], roles } }))
  await page.route('**/api/production-tracking/scan-settings', route => {
    if (route.request().method() === 'PUT') saved = route.request().postDataJSON()
    return route.fulfill({ json: saved || catalog })
  })
  await page.setViewportSize({ width: 1959, height: 1080 })
  await page.goto('/')
  await expect(page).toHaveTitle('UPTON-PLM')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '系统设置', exact: true }).click()
  await expect(page.getByRole('button', { name: '发布通知', exact: true })).toHaveCount(0)
  await page.getByRole('button', { name: '生产扫码设置', exact: true }).click()
  const panel = page.getByLabel('生产扫码设置', { exact: true })
  await expect(panel.locator('.production-scan-settings__node')).toHaveCount(7)
  const metrics = await panel.locator('.production-scan-settings__node').evaluateAll(nodes => nodes.map(node => ({ width: node.getBoundingClientRect().width, height: node.getBoundingClientRect().height, size: (node as HTMLElement).dataset.pdmButtonSize, kind: (node as HTMLElement).dataset.pdmButtonKind })))
  for (const metric of metrics) { expect(metric.width).toBeGreaterThan(180); expect(metric.height).toBeGreaterThanOrEqual(60); expect(metric.size).toBeUndefined(); expect(metric.kind).toBeUndefined() }
  await expect(panel.locator('.production-scan-settings__trace')).toHaveText(/资源组追溯/)
  const editor = panel.locator('.production-scan-settings__editor')
  const close = editor.locator('.el-tag__close').first()
  await expect(close).toBeVisible()
  expect(await close.evaluate(node => { const s = getComputedStyle(node); return { border: s.borderTopWidth, outline: s.outlineStyle, shadow: s.boxShadow, kind: (node as HTMLElement).dataset.pdmButtonKind } })).toEqual({ border: '0px', outline: 'none', shadow: 'none', kind: undefined })
  await page.mouse.move(950, 60)
  await page.screenshot({ path: testInfo.outputPath('production-flow-restored.png'), fullPage: false })
  await close.click()
  await expect(editor.locator('.el-select').first().getByText('机加主管', { exact: true })).toHaveCount(0)
  await expect(editor.locator('.el-select').first().getByText('机加人员', { exact: true })).toBeVisible()
  await panel.getByRole('button', { name: '保存扫码配置', exact: true }).click()
  await expect.poll(() => saved?.bindings.length).toBe(1)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(legacyRequests).toEqual([])
  expect(errors).toEqual([])
})

test('relations and kits share standard categories and relation codes expand contents', async ({ page }, testInfo) => {
  await mockPlanCompanionPanels(page)
  const root = { id: 'category-root', name: '机械标准件', parentId: null, sortOrder: 1, isActive: true }
  const child = { ...root, id: 'category-child', name: '安装附件', parentId: root.id }
  const main = { id: 'relation-main', materialCode: '01021000002', name: '弹垫 Φ5', specification: 'Φ5', brand: 'UPTON', approvalStatus: 'Approved', isArchived: false }
  const revision = { id: 'published', version: 1, groups: [{ id: 'group', name: '安装件', isRequired: true, selectionMode: 0, minSelection: 1, maxSelection: 1, options: [{ id: 'option', materialCode: '01021000006', materialName: '安装螺栓', specification: 'M6', brand: 'UPTON', unitCode: '001', quantityMode: 0, quantityPerSet: 2 }] }] }
  const relation = { id: 'relation-template', mainMaterialId: main.id, mainMaterialCode: main.materialCode, mainMaterialName: main.name, name: '主料配件', updatedAt: '2026-10-30T09:00:00Z', standardLibraryCategories: [child], publishedRevision: revision, draftRevision: null }
  const kit = { id: 'kit-category', code: 'UTJ-01-001', name: '安装套件', model: 'INSTALL', brand: 'UPTON', currentReleasedRevisionId: 'kit-release', revisions: [{ id: 'kit-release', versionNumber: 1, state: 'Released', standardLibraryCategoryId: child.id, components: [{ materialCode: '01021000006', materialName: '安装螺栓', quantity: 2, unit: '001' }] }] }
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.route('**/*jelly*.html*', route => route.fulfill({ contentType: 'text/html', body: '<html><body></body></html>' }))
  await page.route('**/api/material-relations/templates**', route => route.fulfill({ json: [relation, { ...relation, id: 'unclassified', mainMaterialCode: '01021000007', standardLibraryCategories: [] }] }))
  await page.route('**/api/material-relations/categories', route => route.fulfill({ json: [root, child] }))
  await page.route('**/api/materials?**', route => route.fulfill({ json: [main] }))
  await page.route('**/api/engineering-kits?**', route => route.fulfill({ json: [kit] }))
  await page.route('**/api/standard-library/categories**', route => route.fulfill({ json: [root, child] }))
  await page.route('**/api/standard-library/materials?**', route => route.fulfill({ json: { items: [{ material: { ...main, unitCode: '001', model3DAttachmentCount: 0, documentAttachmentCount: 0 }, categories: [child], coverImage: null, relation }], total: 1, page: 1, pageSize: 50 } }))
  await page.setViewportSize({ width: 1959, height: 1080 })
  await page.goto('/')
  await expect(page).toHaveTitle('UPTON-PLM')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.locator('.pdm-project-detail')).toBeVisible()
  await page.getByRole('button', { name: '料品管理', exact: true }).click()
  await page.getByRole('button', { name: '展开料品分类', exact: true }).click()
  const classificationMetrics = async (selector: string) => page.locator(selector).evaluate(element => {
    const style = getComputedStyle(element), row = element.querySelector('.el-tree-node__content')!, all = element.querySelector('.standard-library__all')!, icon = element.querySelector('.standard-library__category-label svg')!
    return { width: element.getBoundingClientRect().width, padding: style.padding, fontSize: style.fontSize, border: style.borderLeftWidth, rowHeight: row.getBoundingClientRect().height, rowFont: getComputedStyle(row).fontSize, allPadding: getComputedStyle(all).padding, iconWidth: icon.getBoundingClientRect().width }
  })
  await expect(page.locator('.material-category-nav .el-tree-node__content').first()).toBeVisible()
  const mainMetrics = await classificationMetrics('.material-category-nav')
  await page.screenshot({ path: testInfo.outputPath('master-classification-shared-style.png'), fullPage: false })
  await page.getByRole('tab', { name: '关联配置', exact: true }).click()
  const directory = page.getByLabel('关联配置汇总', { exact: true })
  await expect(directory.getByText('未分类（待补选）', { exact: true })).toBeVisible()
  await directory.getByRole('treeitem').filter({ hasText: '机械标准件' }).getByText('机械标准件', { exact: true }).click()
  await expect(directory.getByRole('cell', { name: '01021000007', exact: true })).toHaveCount(0)
  await directory.getByRole('button', { name: '展开关联配置 01021000002 的配件明细', exact: true }).click()
  await expect(directory.getByRole('cell', { name: '安装螺栓', exact: true })).toBeVisible()
  await expect(directory.getByText('V1 · 已生效', { exact: true })).toBeVisible()
  const relationMetrics = await classificationMetrics('.material-relation-directory .pdm-category-sidebar')
  await page.screenshot({ path: testInfo.outputPath('relation-classification-expanded.png'), fullPage: false })
  await directory.getByRole('button', { name: '收起关联配置 01021000002 的配件明细', exact: true }).click()
  await page.getByRole('tab', { name: '料品套件', exact: true }).click()
  const kits = page.getByLabel('料品套件', { exact: true })
  await kits.getByRole('treeitem').filter({ hasText: '机械标准件' }).getByText('机械标准件', { exact: true }).click()
  await expect(kits.getByRole('button', { name: '展开套件 UTJ-01-001 的物料明细', exact: true })).toBeVisible()
  const kitMetrics = await classificationMetrics('.engineering-kit-library .pdm-category-sidebar')
  await page.screenshot({ path: testInfo.outputPath('kit-classification-shared-style.png'), fullPage: false })
  await kits.getByRole('treeitem').filter({ hasText: '未分类' }).click()
  await expect(kits.getByText('暂无套件', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: '标准物料', exact: true }).click()
  await expect(page.locator('.standard-library .el-tree-node__content').first()).toBeVisible()
  const standardMetrics = await classificationMetrics('.standard-library .pdm-category-sidebar')
  expect(mainMetrics).toEqual(standardMetrics)
  expect(relationMetrics).toEqual(standardMetrics)
  expect(kitMetrics).toEqual(standardMetrics)
  await page.screenshot({ path: testInfo.outputPath('standard-classification-shared-style.png'), fullPage: false })
  await page.getByRole('button', { name: '查看 01021000002 的关联配置', exact: true }).click()
  await expect(page.getByRole('dialog', { name: '已生效关联配置', exact: true }).getByRole('cell', { name: '安装螺栓', exact: true })).toBeVisible()
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('material relation editor keeps confirmed columns inside the dialog and saves selection advice', async ({ page }, testInfo) => {
  const mainMaterial = {
    id: 'relation-main', materialCode: '01021000002', name: '弹垫 Φ5', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001',
    specification: '弹垫 Φ5', material: null, remark: '主物料备注', brand: 'MITSUBISHI', surfaceTreatment: null, weight: null, weightUnit: null,
    approvalStatus: 'Approved', approvedBy: 'admin', approvedAt: '2026-09-01T01:00:00Z', categoryCode: '0102', u9CategoryCode: '0102',
    u9ItemId: null, u9ItemCode: null, syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-09-01T01:00:00Z',
    updatedBy: 'admin', updatedAt: '2026-09-16T01:00:00Z', rowVersion: 3, isArchived: false, archivedBy: null,
    archivedAt: null, u9SyncConfirmed: true, sourceSystem: 'Pdm', masterOwner: 'Pdm', referenceCount: 0,
  }
  const optionMaterial = {
    ...mainMaterial,
    id: 'relation-option', materialCode: '01021000006', name: '弹垫 Φ6', specification: 'MR-J3-40A', brand: 'YASKAWA', remark: '候选备注', rowVersion: 2,
  }
  const relation = {
    id: 'relation-template', mainMaterialId: mainMaterial.id, mainMaterialCode: mainMaterial.materialCode, mainMaterialName: mainMaterial.name,
    name: `${mainMaterial.materialCode}关联物料`, isArchived: false, updatedBy: 'admin', updatedAt: '2026-09-16T01:00:00Z', rowVersion: 2,
    publishedRevision: null,
    draftRevision: {
      id: 'relation-revision', version: 1, state: 'Draft', changeNote: '', createdBy: 'admin', createdAt: '2026-09-16T01:00:00Z',
      publishedBy: null, publishedAt: null, rowVersion: 1,
      groups: [{
        id: 'relation-group', name: '安装件', isRequired: true, selectionMode: 'Single', minSelection: 1, maxSelection: 1,
        autoSelectUnique: false, sortOrder: 1,
        options: [{
          id: 'relation-option-row', materialId: optionMaterial.id, materialCode: optionMaterial.materialCode, materialName: optionMaterial.name,
          materialKind: 'Standard', unitCode: '001', quantityMode: 'PerMainQuantity', quantityPerSet: 1, isDefault: true, sortOrder: 1,
          selectionAdvice: '优先用于高速工位',
        }],
      }],
    },
  }
  let searchedByModel = false
  let savedBody: Record<string, unknown> | null = null
  await page.route('**/api/materials/page**', route => route.fulfill({ json: { items: [mainMaterial], total: 1, page: 1, pageSize: 50 } }))
  await page.route(/^http:\/\/(?:127\.0\.0\.1:\d+|192\.168\.2\.8:5173)\/api\/materials(?:\?.*)?$/, route => {
    const query = new URL(route.request().url()).searchParams.get('query') ?? ''
    if (query.includes('MR-J3')) searchedByModel = true
    return route.fulfill({ json: query && !optionMaterial.specification.includes(query) ? [] : [mainMaterial, optionMaterial] })
  })
  await page.route('**/api/material-relations/templates**', async route => {
    if (route.request().method() === 'GET') return route.fulfill({ json: [relation] })
    return route.fallback()
  })
  await page.route('**/api/material-relations/templates/relation-template/draft', async route => {
    savedBody = route.request().postDataJSON() as Record<string, unknown>
    return route.fulfill({ json: relation })
  })
  await page.route('**/api/materials/relation-main/attachments', route => route.fulfill({ json: [] }))

  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.addInitScript(() => { window.setInterval = (() => 0) as unknown as typeof window.setInterval })
  await page.setViewportSize({ width: 1236, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.locator('.pdm-project-detail')).toBeVisible({ timeout: 15_000 })
  await page.getByRole('button', { name: '料品管理', exact: true }).click()
  await page.getByRole('button', { name: `查看 ${mainMaterial.materialCode} 的关联物料` }).click()

  const dialog = page.getByRole('dialog', { name: '料品明细' })
  const editor = dialog.getByLabel('关联物料')
  await expect(editor).toBeVisible()
  await expect(editor.locator('.material-relation-summary')).toContainText('品牌MITSUBISHI')
  await expect(editor.locator('.material-relation-help')).toHaveCount(0)
  await expect(editor.getByText('分类名称 *', { exact: true })).toBeVisible()
  await expect(editor.locator('.material-relation-option-head span')).toHaveText([
    '料号', '名称', '型号', '品牌', '备注', '选型建议', '数量计算', '每套数量', '优先推荐', '操作',
  ])
  await expect(editor.locator('.material-relation-option-row')).toContainText('YASKAWA')
  const optionTableLayout = await editor.locator('.material-relation-option-table').evaluate(element => ({
    clientWidth: element.clientWidth,
    scrollWidth: element.scrollWidth,
  }))
  expect(optionTableLayout.scrollWidth).toBeLessThanOrEqual(optionTableLayout.clientWidth)

  const materialSearch = editor.locator('.material-relation-option-row .el-select input').first()
  await materialSearch.fill('MR-J3')
  await expect.poll(() => searchedByModel).toBe(true)
  await page.keyboard.press('Escape')
  await editor.getByRole('textbox', { name: '选型建议' }).fill('高速工位优先，低速工位可替代')
  await editor.getByRole('button', { name: '保存修改', exact: true }).click()
  await expect.poll(() => savedBody).not.toBeNull()
  expect(savedBody).toMatchObject({
    groups: [{ options: [{ selectionAdvice: '高速工位优先，低速工位可替代' }] }],
  })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
  await page.screenshot({ path: testInfo.outputPath('material-relation-editor-confirmed-layout.png'), fullPage: false })
})

test('engineer can read a material rejection reason without approval controls', async ({ page }, testInfo) => {
  materialCodeApplications = [{
    id: 'application-rejected', projectId, bomItemId: 'bom-standard-1', bomHeaderKind: 'Standard', applicationType: 'StandardBomItem',
    status: 'Rejected', requestedBy: 'engineer', requestedAt: '2026-09-04T01:00:00Z', decidedBy: 'standardizer',
    decidedAt: '2026-09-04T02:00:00Z', decisionComment: '型号资料不完整', materialId: null, materialCode: null, rowVersion: 4,
    applicationName: '标准紧固件', projectCode: 'PRJ-REAL-001', projectName: '真实装配项目', categoryCode: '0102',
    specification: 'M8', brand: 'UPTON', remark: null, workflowState: 'Rejected',
  }]
  const consoleErrors: string[] = []
  const failedResponses: string[] = []
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()) })
  page.on('response', response => { if (response.status() >= 400) failedResponses.push(`${response.status()} ${response.url()}`) })

  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto('/')
  const loginForm = page.getByLabel('登录PLM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('engineer')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.locator('.pdm-project-detail')).toBeVisible({ timeout: 15_000 })

  await page.getByRole('button', { name: '料品管理', exact: true }).click()
  await page.getByRole('tab', { name: /料号审批/ }).click()
  await page.getByRole('tab', { name: /审批\/同步历史/ }).click()
  const history = page.getByLabel('料号审批与U9C同步历史')
  await expect(history).toContainText('已退回')
  await expect(history).toContainText('说明')
  await expect(history).toContainText('型号资料不完整')
  await expect(history.getByRole('button', { name: '批准', exact: true })).toHaveCount(0)
  await expect(history.getByRole('button', { name: '退回', exact: true })).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('engineer-material-rejection-history.png'), fullPage: false })
  expect({ consoleErrors, failedResponses }).toEqual({ consoleErrors: [], failedResponses: [] })
})

test('validation plan selector keeps selections across categories and skips completed category', async ({ page }) => {
  const categories = [
    { id: 'validation-category-complete', name: '定位工装', sortOrder: 10, isActive: true, itemCount: 1, referenceCount: 1, createdBy: 'system', createdAt: '2026-09-09T00:00:00Z', updatedBy: 'system', updatedAt: '2026-09-09T00:00:00Z', rowVersion: 1 },
    { id: 'validation-category-second', name: '独立工装', sortOrder: 20, isActive: true, itemCount: 1, referenceCount: 0, createdBy: 'system', createdAt: '2026-09-09T00:00:00Z', updatedBy: 'system', updatedAt: '2026-09-09T00:00:00Z', rowVersion: 1 },
    { id: 'validation-category-third', name: '安全相关', sortOrder: 30, isActive: true, itemCount: 1, referenceCount: 0, createdBy: 'system', createdAt: '2026-09-09T00:00:00Z', updatedBy: 'system', updatedAt: '2026-09-09T00:00:00Z', rowVersion: 1 },
  ]
  const items = [
    { id: 'validation-item-complete', categoryId: categories[0]!.id, content: '定位销检查', defaultInformationSource: '内部评审', sortOrder: 10, isActive: true, referenceCount: 1, createdBy: 'system', createdAt: '2026-09-09T00:00:00Z', updatedBy: 'system', updatedAt: '2026-09-09T00:00:00Z', rowVersion: 1 },
    { id: 'validation-item-second', categoryId: categories[1]!.id, content: '独立工装装配确认', defaultInformationSource: '内部评审', sortOrder: 10, isActive: true, referenceCount: 0, createdBy: 'system', createdAt: '2026-09-09T00:00:00Z', updatedBy: 'system', updatedAt: '2026-09-09T00:00:00Z', rowVersion: 1 },
    { id: 'validation-item-third', categoryId: categories[2]!.id, content: '安全门互锁确认', defaultInformationSource: '内部评审', sortOrder: 10, isActive: true, referenceCount: 0, createdBy: 'system', createdAt: '2026-09-09T00:00:00Z', updatedBy: 'system', updatedAt: '2026-09-09T00:00:00Z', rowVersion: 1 },
  ]
  const plan = {
    id: 'validation-plan-e2e', projectId, preparedBy: 'engineer', validationDate: '2026-09-14', revisionNumber: 1, state: 'Draft', approvalTasks: [], attachments: [],
    items: [{ id: 'validation-plan-row-existing', catalogCategoryId: categories[0]!.id, catalogItemId: items[0]!.id, categoryName: categories[0]!.name, validationContent: items[0]!.content, informationSource: '内部评审', validationDate: null, result: null, responsiblePerson: null, remark: null, sortOrder: 1 }],
    createdBy: 'engineer', createdAt: '2026-09-14T00:00:00Z', updatedBy: 'engineer', updatedAt: '2026-09-14T00:00:00Z', rowVersion: 1,
  }
  await page.route('**/api/auth/login', route => route.fulfill({ json: {
    accessToken: 'e2e-token', expiresAt: '2099-01-01T00:00:00Z', resumeToken: 'e2e-resume-token', username: 'engineer', displayName: '真实工程师', role: 'Engineer',
    permissions: ['project.view', 'project.content.view', 'validation-plan.edit'], primaryCompanyId: 'org-ks', activeCompanyId: 'org-ks', activeCompanyName: '昆山阿普顿自动化系统有限公司', crossCompanyView: false,
    accessibleCompanies: [{ id: 'org-ks', name: '昆山阿普顿自动化系统有限公司', code: '7' }],
  } }))
  await page.route('**/api/validation-check-catalog', route => route.fulfill({ json: { categories, items } }))
  let savedStandard: string | null = null
  await page.route(`**/api/projects/${projectId}/validation-plan`, route => {
    if (route.request().method() === 'PUT') {
      const request = route.request().postDataJSON() as { items: Array<{ validationStandard?: string | null }> }
      savedStandard = request.items[0]?.validationStandard ?? null
      return route.fulfill({ json: { ...plan, items: request.items.map((item, index) => ({ ...plan.items[index], ...item })) } })
    }
    return route.fulfill({ json: plan })
  })
  await page.route('**/api/validation-plans/validation-plan-e2e/execution-records', route => route.fulfill({ json: [] }))
  await page.route(`**/api/quality/projects/${projectId}/validation-plan`, route => route.fulfill({ json: null }))
  await page.route(`**/api/projects/${projectId}/quality-inspections`, route => route.fulfill({ json: [] }))
  const consoleErrors: string[] = []
  const failedResponses: string[] = []
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()) })
  page.on('response', response => { if (response.status() >= 400) failedResponses.push(response.url()) })

  await page.setViewportSize({ width: 1925, height: 1114 })
  await page.goto('/')
  const loginForm = page.getByLabel('登录PLM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('engineer')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '质量', exact: true }).click()
  await page.locator('.validation-plan-summary__table tbody tr').first().click()
  await page.getByRole('button', { name: '选取内容' }).click()

  const dialog = page.getByRole('dialog', { name: '选择验证检查项' })
  const completeCategory = dialog.getByRole('button', { name: /定位工装/ })
  const secondCategory = dialog.getByRole('button', { name: /独立工装/ })
  const thirdCategory = dialog.getByRole('button', { name: /安全相关/ })
  await expect(completeCategory).toContainText('0')
  await expect(completeCategory).toHaveClass(/is-complete/)
  await expect(secondCategory).toHaveClass(/is-active/)
  await dialog.getByText('独立工装装配确认').click()
  await thirdCategory.click()
  await dialog.getByText('安全门互锁确认').click()
  await expect(dialog).toContainText('跨分类已选 2 项')
  await completeCategory.click()
  await expect(dialog).toContainText('该分类的 1 项已全部加入当前计划')
  await expect(dialog).toContainText('跨分类已选 2 项')
  await page.screenshot({ path: join(tmpdir(), 'uplm-validation-plan-multiselect-20260914.png'), fullPage: false })
  const addToPlan = dialog.getByRole('button', { name: '加入计划（2）' })
  await addToPlan.hover()
  await expect(addToPlan).toHaveCSS('background-color', 'rgb(30, 64, 175)')
  await expect(addToPlan).toHaveCSS('color', 'rgb(255, 255, 255)')
  await addToPlan.click()
  await expect(dialog).toBeHidden()
  await page.waitForTimeout(400)
  await expect(page.locator('.validation-plan__table')).toContainText('独立工装装配确认')
  await expect(page.locator('.validation-plan__table')).toContainText('安全门互锁确认')
  await expect(page.locator('.validation-plan__table thead th')).toHaveText(['序号', '分类', '验证内容', '验证标准', '信息来源', '验证日期', '责任人', '结果', '审核人', '备注', '操作'])
  const standardInput = page.getByRole('textbox', { name: '第 1 行验证标准' })
  await expect(page.locator('.validation-plan__table thead th').nth(3)).toHaveCSS('text-align', 'center')
  await expect(standardInput).toHaveCSS('text-align', 'center')
  await standardInput.fill('定位偏差不超过 2 mm')
  await page.screenshot({ path: join(tmpdir(), 'uplm-validation-standard-20260923.png'), fullPage: false })
  await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect(standardInput).toHaveValue('定位偏差不超过 2 mm')
  expect(savedStandard).toBe('定位偏差不超过 2 mm')
  expect(consoleErrors, JSON.stringify(failedResponses)).toEqual([])
})

test('production package search has synchronized package item and quantity totals over LAN HTTP', async ({ page }, testInfo) => {
  await page.route('**/assets/melon-jelly*.html*', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>登录背景占位</title>' }))
  await mockPlanCompanionPanels(page)
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  const summary = (id: string, totalLines: number, totalQuantity: number, status = '待排产') => ({
    releasePackageId: id, projectId, projectCode: 'P001', projectName: '生产统计验收', packageNumber: id === 'one' ? 'RP-P700016-1-20261019-74194570' : `RP-${id}`,
    scope: 'NonStandardWithDrawing', publishedAt: '2026-10-19T00:00:00Z', status, totalLines, totalQuantity,
    qualifiedLines: 0, issuedLines: 0, urgentLines: 0, priorityLines: 0, atRiskLines: 0, unplannedLines: totalLines, firstRequiredOn: '2026-11-05',
  })
  const packages = [summary('one', 3, 30, '无新增生产需求'), summary('two', 2, 5.25, '待接收'), summary('three', 1, 0.75), summary('closed', 4, 12, '已完结')]
  await page.route('**/api/production-tracking/releases/*/external-suppliers', route => route.fulfill({ json: { companyId: 'org-ks', suppliers: [], categories: [] } }))
  await page.route('**/api/production-tracking/capacity', route => route.fulfill({ json: { calendar: { rowVersion: 1, days: [] }, usage: [] } }))
  await page.route('**/api/production-tracking/projects*', route => route.fulfill({ json: [
    { id: projectId, code: 'P001', name: '生产统计验收', packages: packages.slice(0, 2) },
    { id: 'other-project', code: 'P002', name: '第二组', packages: packages.slice(2) },
  ] }))
  await page.route('**/api/production-tracking/releases/*', route => {
    const current = packages.find(item => route.request().url().endsWith(item.releasePackageId))!
    return route.fulfill({ json: { rowVersion: 1, state: { ...current, companyId: 'org-ks', lots: [], movements: [],
      lines: Array.from({ length: current.totalLines }, (_, index) => ({ id: `line-${index}`, materialKey: `material-${index}`, drawingNumber: `D-${index}`,
        name: `物料${index + 1}`, revision: 'A', unit: '个', requiredQuantity: current.totalQuantity / current.totalLines, priority: 'Normal' })) } } })
  })
  await page.setViewportSize({ width: 1916, height: 1080 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '制造中心', exact: true }).click()
  for (const tab of ['发布跟踪', '外协追溯', '自制追溯']) {
    await page.getByRole('button', { name: tab, exact: true }).click()
    await expect(page.locator('.production-workbench__package')).toHaveCount(3)
    const tree = page.getByRole('complementary', { name: '项目与发布包', exact: true })
    const selectedProject = tree.locator('.production-workbench__project.selected')
    const selectedPackage = tree.locator('.production-workbench__package.selected')
    const selectedText = await selectedPackage.textContent()
    await expect(selectedProject).toHaveAttribute('aria-expanded', 'true')
    await selectedProject.focus()
    await selectedProject.press('Enter')
    await expect(selectedProject).toHaveAttribute('aria-expanded', 'false')
    await expect(selectedPackage).not.toBeVisible()
    await expect(selectedPackage).toHaveText(selectedText!)
    await selectedProject.click()
    await expect(selectedPackage).toBeVisible()
    const otherProject = tree.locator('.production-workbench__project:not(.selected)')
    if (await otherProject.getAttribute('aria-expanded') === 'false') await otherProject.click()
    await expect(otherProject).toHaveAttribute('aria-expanded', 'true')
    await expect(selectedPackage).toHaveText(selectedText!)
    await expect(selectedProject).toHaveAttribute('aria-expanded', 'true')
    if (tab === '发布跟踪') await tree.screenshot({ path: testInfo.outputPath('compact-release-tree-expanded.png') })
    const appearance = await page.locator('.production-workbench__tree').evaluate(element => {
      const button = getComputedStyle(element.querySelector('.production-workbench__all-categories')!)
      return { solid: button.backgroundColor !== 'rgba(0, 0, 0, 0)' && button.backgroundColor !== button.color,
        unselected: [...element.querySelectorAll('.production-workbench__package:not(.selected)')].map(item => ({ border: getComputedStyle(item).borderLeftColor, shadow: getComputedStyle(item).boxShadow })) }
    })
    expect(appearance.solid).toBe(true); expect(appearance.unselected.length).toBeGreaterThan(0)
    expect(appearance.unselected.every(item => item.border === 'rgba(0, 0, 0, 0)' && item.shadow === 'none')).toBe(true)
  }
  await page.getByRole('button', { name: '发布跟踪', exact: true }).click()
  const totals = page.getByLabel('筛选发布包统计', { exact: true })
  const compactText = () => totals.innerText().then(text => text.replace(/\s/g, ''))
  await expect.poll(compactText).toBe('3包6项36个')
  const checkGeometry = async () => {
    const geometry = await page.locator('.production-workbench__tree-search').evaluate(element => {
      const row = element.getBoundingClientRect(), input = element.querySelector('input')!.getBoundingClientRect(), stats = element.querySelector('[role=status]')!.getBoundingClientRect()
      return { inputWidth: input.width, separated: input.right <= stats.left || input.bottom <= stats.top || stats.bottom <= input.top, fits: stats.right <= row.right + 1, overflow: element.scrollWidth > element.clientWidth + 1 }
    })
    expect(geometry).toMatchObject({ separated: true, fits: true, overflow: false })
    expect(geometry.inputWidth).toBeGreaterThan(35)
  }
  await checkGeometry()
  await page.screenshot({ path: join(tmpdir(), 'pdm-production-totals-desktop.png'), animations: 'disabled' })
  const search = page.getByLabel('搜索生产发布包', { exact: true })
  await search.fill('RP-two'); await expect.poll(compactText).toBe('1包2项5.25个')
  await search.fill('P002'); await expect.poll(compactText).toBe('1包1项0.75个')
  await page.getByLabel('已完结', { exact: true }).check(); await expect.poll(compactText).toBe('2包5项12.75个')
  await page.locator('.production-workbench__toolbar select').selectOption('待排产'); await expect.poll(compactText).toBe('1包1项0.75个')
  await search.fill('无匹配结果'); await expect.poll(compactText).toBe('0包0项0个')
  await search.fill(''); await page.locator('.production-workbench__toolbar select').selectOption(''); await page.getByLabel('已完结', { exact: true }).uncheck()
  await expect.poll(compactText).toBe('3包6项36个')
  await page.setViewportSize({ width: 1000, height: 900 }); await checkGeometry()
  expect(await page.locator('.production-workbench__tree').evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true)
  await page.screenshot({ path: join(tmpdir(), 'pdm-production-totals-narrow.png'), animations: 'disabled' })
  await page.setViewportSize({ width: 600, height: 900 }); await checkGeometry()
  await page.screenshot({ path: join(tmpdir(), 'pdm-production-totals-mobile.png'), animations: 'disabled' })
  expect(errors).toEqual([])
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
})

test('production drawing pagination scrolls and selects the whole package', async ({ page }, testInfo) => {
  await page.route('**/api/production-tracking/projects', route => route.fulfill({ json: [] }))
  const items = Array.from({ length: 121 }, (_, index) => ({
    projectId, projectCode: 'P-121', projectName: '分页图纸项目', documentId: `doc-${index}`, versionId: `version-${index}`,
    releasePackageId: 'package-121', releasePackageNumber: 'RP-121', drawingNumber: `D-${String(index).padStart(3, '0')}`,
    name: '分页图纸', model: 'M-121', revision: 'A', publishedAt: '2026-10-09T00:00:00Z', priority: 'Normal',
    isCurrent: true, pdfReady: true, legacyUnverified: false, bomItems: [], modelFiles: [],
  }))
  const requests: URL[] = []
  await page.route('**/api/production-drawings?*', route => {
    requests.push(new URL(route.request().url()))
    return route.fulfill({ json: items })
  })
  await page.setViewportSize({ width: 1552, height: 855 })
  await page.goto('/')
  await page.getByLabel('登录PLM').getByRole('textbox', { name: '账号' }).fill('engineer')
  await page.getByLabel('登录PLM').getByRole('textbox', { name: '密码' }).fill('correct-password')
  await page.getByLabel('登录PLM').getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '制造中心', exact: true }).click()
  await page.getByRole('button', { name: '图纸资料', exact: true }).click()
  const center = page.getByLabel('生产图纸中心')
  await expect(center.locator('.production-drawings__project')).toHaveCount(1)
  expect(requests).toHaveLength(1)
  expect(requests[0]!.searchParams.get('indexOnly')).toBe('true')
  await center.locator('.production-drawings__project').click()
  await expect(center.locator('tbody tr')).toHaveCount(50)
  expect(requests[1]!.searchParams.get('projectId')).toBe(projectId)
  await center.getByLabel('全选本包图纸').check()
  await expect(center.locator('.production-drawings__selection')).toContainText('已选 121 张')
  await center.getByLabel('生产图纸下一页').click()
  await expect(center.locator('tbody tr')).toHaveCount(50)
  await expect(center.locator('tbody input[type="checkbox"]').first()).toBeChecked()
  await expect(center.locator('.production-drawings__selection')).toContainText('已选 121 张')
  const geometry = await center.evaluate(element => {
    const scroll = element.querySelector('.pdm-table-scroll')!
    scroll.scrollTop = 500
    return { bottom: element.getBoundingClientRect().bottom, viewport: innerHeight, scrolled: scroll.scrollTop, filtersMargin: getComputedStyle(element.querySelector('.production-drawings__filters')!).marginBottom }
  })
  expect(geometry.scrolled).toBeGreaterThan(0)
  expect(geometry.viewport - geometry.bottom).toBeLessThanOrEqual(12)
  expect(geometry.filtersMargin).toBe('10px')
  await page.screenshot({ path: testInfo.outputPath('production-drawing-pagination.png') })
  await center.getByLabel('生产图纸每页条数').selectOption('30')
  await expect(center.locator('tbody tr')).toHaveCount(30)
  await expect(center.locator('.production-drawings__selection')).toContainText('已选 121 张')
  await center.locator('tbody tr').first().click()
  for (const theme of ['c', 'o']) {
    await page.evaluate(value => { document.documentElement.dataset.pdmTheme = value }, theme)
    const colors = await center.evaluate(element => {
      const style = getComputedStyle(element)
      return { background: style.getPropertyValue('--pdm-theme-accent-soft').trim(), border: style.getPropertyValue('--pdm-theme-accent').trim() }
    })
    const rgb = (hex: string) => `rgb(${parseInt(hex.slice(1, 3), 16)}, ${parseInt(hex.slice(3, 5), 16)}, ${parseInt(hex.slice(5, 7), 16)})`
    await expect(center.locator('.production-drawings__project.is-selected')).toHaveCSS('background-color', rgb(colors.background))
    await expect(center.locator('.production-drawings__package.is-selected')).toHaveCSS('border-color', rgb(colors.border))
    await expect(center.locator('tbody tr.is-selected')).toHaveCSS('background-color', rgb(colors.background))
    await page.screenshot({ path: testInfo.outputPath(`production-drawing-theme-${theme}.png`) })
  }
})
test('production drawings open by project and package, then download selected versions', async ({ page }) => {
  await page.addInitScript(() => { Object.defineProperty(window, 'showSaveFilePicker', { value: undefined, configurable: true }) })
  const first = {
    projectId: projectId, projectCode: 'P-001', projectName: '装配项目', documentId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    versionId: '11111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa', releasePackageId: '11111111-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
    releasePackageNumber: 'RP-001', drawingNumber: 'D-100', model: 'M-100', name: '装配图', revision: 'B',
    publishedAt: '2026-09-22T00:00:00Z', priority: 'Urgent', requiredOn: '2026-10-01',
    publishedBy: 'engineer', division: '机械事业部', projectManager: 'admin',
    isCurrent: true, pdfReady: true, legacyUnverified: false,
    modelFiles: [{ documentId: 'model-d-100', versionId: 'model-v-d-100', drawingNumber: 'M-100', name: '三维装配', revision: 'B', stepReady: true }],
    bomItems: [{ sequence: 1, drawingNumber: 'MAT-100', name: '冻结机架', specification: 'MODEL-X', revision: 'B', remark: '先加工', material: '铝', surfaceTreatment: '阳极', heatTreatment: '无', quantity: 2, unit: '001', complete: true, brand: '自制' }],
  }
  const second = {
    ...first, documentId: 'cccccccc-cccc-cccc-cccc-cccccccccccc', versionId: '22222222-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    releasePackageId: '22222222-bbbb-bbbb-bbbb-bbbbbbbbbbbb', releasePackageNumber: 'RP-002',
    drawingNumber: 'D-200', model: 'M-200', name: '机架图', publishedAt: '2026-09-23T00:00:00Z', pdfReady: false,
    modelFiles: [{ documentId: 'model-d-200', versionId: 'model-v-d-200', drawingNumber: 'M-200', name: '三维机架', revision: 'A', stepReady: false }], bomItems: [], publishedBy: 'admin',
  }
  const anotherProject = {
    ...first, projectId: '33333333-3333-3333-3333-333333333333', projectCode: 'P-002', projectName: '电气项目',
    documentId: 'dddddddd-dddd-dddd-dddd-dddddddddddd', versionId: '33333333-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    releasePackageId: '33333333-bbbb-bbbb-bbbb-bbbbbbbbbbbb', releasePackageNumber: 'RP-003', drawingNumber: 'E-100',
    publishedBy: 'admin', division: '电气事业部', projectManager: 'new-user',
  }
  let archiveRequest: { versionIds: string[]; format: string } | null = null
  await page.route('**/api/production-drawings?*', route => route.fulfill({ json: [first, second, anotherProject] }))
  await page.route('**/api/projects/*/production-drawings/archive', route => {
    archiveRequest = route.request().postDataJSON()
    if (route.request().headers().authorization !== 'Bearer e2e-token') {
      return route.fulfill({ status: 401, json: { detail: '未带登录令牌' } })
    }
    return route.fulfill({ contentType: 'application/zip', body: 'PK-test-archive' })
  })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto('/')
  await expect(page).toHaveURL(/^http:\/\/127\.0\.0\.1:\d+\/$/)
  await expect(page).toHaveTitle('UPTON-PLM')
  let releaseInitialProjects!: () => void
  const initialProjectsGate = new Promise<void>(resolve => { releaseInitialProjects = resolve })
  let initialProjects = true
  await page.route('**/api/projects', async route => {
    if (initialProjects) {
      initialProjects = false
      await initialProjectsGate
    }
    await route.fallback()
  })
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '生产图纸', exact: true }).click()
  releaseInitialProjects()
  await expect(page.getByLabel('生产图纸中心')).toBeVisible()
  await expect(page.locator('.production-drawings__header')).toHaveCount(0)
  await expect(page.locator('.production-drawings__project')).toHaveCount(2)
  await page.getByLabel('搜索生产图纸').fill('电气项目')
  await expect(page.locator('.production-drawings__project')).toHaveCount(1)
  await expect(page.locator('.production-drawings__project')).toContainText('P-002')
  await page.getByLabel('搜索生产图纸').fill('')
  await page.getByLabel('按发布日期筛选').fill('2026-09-23')
  await expect(page.locator('.production-drawings__project')).toHaveCount(1)
  await expect(page.locator('.production-drawings__project')).toContainText('P-001')
  await page.getByLabel('按发布日期筛选').fill('')
  await expect(page.locator('.production-drawings__project').first()).toContainText('P-001')
  const projectMeta = page.locator('.production-drawings__project-meta').first()
  await expect(projectMeta).toHaveText('2 张图纸2026-09-23')
  await expect(projectMeta).not.toContainText('发布')
  await expect(projectMeta).toHaveCSS('justify-content', 'space-between')
  await expect(page.locator('.production-drawings__project').first()).toHaveCSS('height', '40px')
  await expect(page.locator('.production-drawings__packages')).toHaveCount(0)
  await expect(page.getByLabel('按发布人筛选').locator('option[value="admin"]')).toHaveText('系统管理员')
  await expect(page.getByLabel('按项目经理筛选').locator('option[value="admin"]')).toHaveText('系统管理员')
  await page.locator('.production-drawings__project').filter({ hasText: 'P-001' }).click()
  await expect(page.locator('.production-drawings__package')).toHaveCount(2)
  await expect(page.locator('.production-drawings__package').first()).toContainText('RP-002')
  await expect(page.locator('.production-drawings__package').first()).toHaveCSS('height', '40px')
  await expect(page.locator('.production-drawings__project-header')).toHaveCount(0)
  await expect(page.locator('.production-drawings__projects h2')).toHaveCount(0)
  await expect(page.locator('.production-drawings__toolbar')).toHaveCount(0)
  await expect(page.locator('.production-drawings__selection').getByRole('button', { name: '下载本包 STEP' })).toBeVisible()
  await expect(page.locator('tbody')).toContainText('D-200')
  await page.locator('.production-drawings__package').filter({ hasText: 'RP-001' }).click()
  await expect(page.locator('.production-drawings__project-title').first()).toHaveCSS('font-size', '14px')
  await expect(page.locator('.production-drawings__project-title strong').first()).toHaveCSS('font-size', '14px')
  await expect(page.locator('.production-drawings__package strong').first()).toHaveCSS('font-size', '11px')
  await expect(page.locator('.production-drawings__package small').first()).toHaveCSS('font-size', '11px')
  await expect(page.locator('tbody')).toContainText('MAT-100')
  await expect(page.locator('tbody')).not.toContainText('D-200')
  await expect(page.locator('.production-drawings thead th')).toHaveText(['选择', '物料编码', '名称', '型号', '版本', '备注', '材质', '表面处理', '热处理', '数量', '图纸', '紧急程度', '需求货期'])
  await expect(page.locator('tbody')).toContainText('MAT-100')
  await expect(page.locator('tbody')).toContainText('冻结机架')
  await expect(page.locator('tbody')).toContainText('阳极')
  await expect(page.locator('tbody')).toContainText('紧急')
  await expect(page.locator('tbody')).toContainText('2026-10-01')
  await expect(page.getByRole('button', { name: '下载 M-100 STEP' })).toBeVisible()
  await expect(page.locator('.production-drawings thead th').nth(1)).toHaveCSS('text-align', 'center')
  await expect(page.locator('.production-drawings tbody td').nth(1)).toHaveCSS('text-align', 'center')
  await page.screenshot({ path: join(tmpdir(), 'production-drawings-bom-default-desktop.png'), fullPage: false })
  await page.getByRole('button', { name: '列设置' }).click()
  const columnDialog = page.getByRole('dialog', { name: '生产图纸列设置' })
  await expect(columnDialog.locator('input[type="checkbox"]')).toHaveCount(23)
  await page.screenshot({ path: join(tmpdir(), 'production-drawings-bom-column-settings.png'), fullPage: false })
  await columnDialog.getByLabel('品牌').check()
  await columnDialog.getByRole('button', { name: '保存' }).click()
  await expect(page.getByRole('columnheader', { name: '品牌' })).toBeVisible()
  await expect(page.locator('tbody')).toContainText('自制')
  await page.getByLabel('全选本包图纸').check()
  const selectedBom = page.waitForEvent('download')
  await page.getByRole('button', { name: '下载选中BOM', exact: true }).click()
  expect((await selectedBom).suggestedFilename()).toBe('RP-001-选中BOM.xlsx')
  const packageBom = page.waitForEvent('download')
  await page.getByRole('button', { name: '下载本包BOM', exact: true }).click()
  expect((await packageBom).suggestedFilename()).toBe('RP-001-本包BOM.xlsx')
  const stepArchive = page.waitForEvent('download')
  await page.getByRole('button', { name: '批量下载 STEP' }).click()
  expect((await stepArchive).suggestedFilename()).toContain('RP-001-STEP')
  expect(archiveRequest).toEqual({ versionIds: [first.versionId], format: 'Step' })
  const download = page.waitForEvent('download')
  await page.getByRole('button', { name: '批量下载 PDF' }).click()
  expect((await download).suggestedFilename()).toContain('RP-001-PDF')
  expect(archiveRequest).toEqual({ versionIds: [first.versionId], format: 'Pdf' })
  const downloadStatus = page.locator('.production-drawings__toolbar-actions > [role="status"]')
  await expect(downloadStatus).toContainText('文件已下载')
  expect(await downloadStatus.evaluate(element => element.nextElementSibling?.textContent)).toBe('列设置')
  await page.screenshot({ path: join(tmpdir(), 'production-drawings-bom-columns-desktop.png'), fullPage: false })
  await page.getByLabel('按发布人筛选').selectOption('admin')
  await page.getByLabel('按事业部筛选').selectOption('机械事业部')
  await page.getByLabel('按项目经理筛选').selectOption('admin')
  await expect(page.locator('.production-drawings__project')).toHaveCount(1)
  await expect(page.locator('.production-drawings__package')).toHaveCount(1)
  await expect(page.locator('.production-drawings__package')).toContainText('RP-002')
  await page.locator('.production-drawings__package').filter({ hasText: 'RP-002' }).click()
  await expect(page.locator('tbody')).toContainText('STEP待转换')
  await page.getByLabel('全选本包图纸').check()
  await page.getByRole('button', { name: '批量下载 STEP' }).click()
  await expect(page.getByRole('alert')).toContainText('不能用 SolidWorks 源文件替代')
  expect(archiveRequest).toEqual({ versionIds: [first.versionId], format: 'Pdf' })
  await page.screenshot({ path: join(tmpdir(), 'production-drawings-filtered-desktop.png'), fullPage: false })
  await page.setViewportSize({ width: 390, height: 844 })
  await expect(page.locator('.production-drawings__project').filter({ hasText: 'P-001' })).toBeVisible()
  await page.screenshot({ path: join(tmpdir(), 'production-drawings-bom-columns-mobile.png'), fullPage: false })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})


test('component drawings stay in source data and support classification filtering', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.setViewportSize({ width: 1920, height: 1080 })
  const component = { id: 'component-drawing', kind: 6, sequence: 1, drawingNumber: 'COMP-001', name: '按钮安装组件', quantity: 2, unit: '个', revision: 'W1', isComplete: false, source: 'Auto', sourceDocumentId: 'component-document', sourceInstancePath: 'root/component', isPendingClassification: false }
  const pending = { ...component, id: 'pending-component', kind: 4, sequence: 2, name: '待分类组件', drawingNumber: 'COMP-002', sourceDocumentId: 'pending-document', sourceInstancePath: 'root/pending', isPendingClassification: true }
  await page.route(`**/api/projects/${projectId}/boms/items/batch`, async route => {
    pending.kind = 6
    pending.isPendingClassification = false
    await route.fulfill({ json: [pending] })
  })
  await page.route(`**/api/projects/${projectId}/bom-source-data`, route => route.fulfill({ json: [component, pending] }))
  await page.route(`**/api/projects/${projectId}/boms/Unclassified`, route => route.fulfill({ json: pending.isPendingClassification ? [pending] : [] }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '源数据（2）' }).click()
  const manager = page.getByLabel('BOM维护', { exact: true })
  await manager.getByLabel('筛选物料分类').selectOption('ComponentDrawing')
  await expect(manager.locator('.pdm-bom-table > tbody tr')).toHaveCount(1)
  await expect(manager.locator('.pdm-bom-table > tbody')).toContainText('按钮安装组件')
  await expect(manager.locator('.pdm-bom-table > tbody')).toContainText('部件图')
  await expect(manager.locator('.pdm-bom-table > tbody .pdm-bom-classification-indicator')).toHaveAttribute('aria-label', '已归类')
  await expect(manager.getByRole('button', { name: '发起发布', exact: true })).toHaveCount(0)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(page.url()).toContain('127.0.0.1:5193')
  expect(await page.title()).not.toBe('')
  await page.screenshot({ path: join(tmpdir(), 'pdm-component-drawing-filter.png'), fullPage: true })
  await manager.getByLabel('筛选物料分类').selectOption('Unclassified')
  await manager.getByLabel('选择物料', { exact: true }).check()
  const requestPromise = page.waitForRequest(request => request.url().includes('/boms/items/batch') && request.method() !== 'GET')
  await manager.getByRole('button', { name: '归部件图', exact: true }).click()
  const request = await requestPromise
  expect(request.postDataJSON()).toMatchObject({ itemIds: ['pending-component'], fields: ['kind'], targetKind: 'ComponentDrawing' })
  await manager.getByLabel('筛选物料分类').selectOption('ComponentDrawing')
  await expect(manager.locator('.pdm-bom-table > tbody')).toContainText('待分类组件')
  await expect(manager.locator('.pdm-bom-table > tbody tr')).toHaveCount(2)
  expect(errors).toEqual([])
})

test('BOM release buttons remain inside the right edge at all widths', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  for (const width of [1552, 994, 834]) {
    await page.setViewportSize({ width, height: 855 })
    for (const category of ['标准件BOM', '非标件BOM', '电气件BOM', '易损件BOM']) {
      await page.getByRole('tab', { name: new RegExp(category) }).click()
      const strip = page.locator('.pdm-bom-release-strip').first()
      await expect(strip).toBeVisible()
      const result = await strip.evaluate(element => {
        const box = element.getBoundingClientRect()
        const buttons = [...element.querySelectorAll('button')].map(button => button.getBoundingClientRect())
        return { inside: buttons.every(button => button.right <= box.right && button.left >= box.left && button.left >= 0 && button.right <= window.innerWidth),
          aligned: Math.abs(Math.max(...buttons.map(button => button.right)) - box.right) <= 13 }
      })
      expect(result.inside, `${width} ${category}: ${JSON.stringify(result)}`).toBe(true)
      expect(result.aligned).toBe(true)
    }
    await page.screenshot({ path: testInfo.outputPath(`bom-buttons-${width}.png`) })
  }
  expect(errors).toEqual([])
})

test('U9 correction previews selected attributes before saving BOM', async ({ page }, testInfo) => {
  let saved: any = null
  await page.route('**/api/u9-material-query/*', route => route.fulfill({ json: { responseCode: 0, items: [{ u9ItemCode: 'REAL-STD-001', u9Material: 'U9确认材质', u9Weight: 4.93 }] } }))
  await page.route('**/api/projects/*/boms/Standard**', async route => {
    if (route.request().method() !== 'PUT') return route.fallback()
    saved = route.request().postDataJSON()
    return route.fulfill({ json: { items: saved.items, draft: null } })
  })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.setViewportSize({ width: 1552, height: 855 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: /标准件BOM/ }).click()
  await page.locator('.pdm-edit-table tbody tr').first().getByRole('checkbox').check()
  await page.getByRole('button', { name: '按U9校正', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '按U9校正BOM属性' })
  await expect(dialog).toBeVisible()
  expect(saved).toBeNull()
  await dialog.getByRole('checkbox', { name: 'REAL-STD-001 材质' }).check()
  await page.screenshot({ path: testInfo.outputPath('u9-bom-confirmation.png') })
  await dialog.getByRole('button', { name: '确认保存' }).click()
  await expect.poll(() => saved).not.toBeNull()
  expect(saved.items[0].material).toBe('U9确认材质')
  expect(saved.items[0].weight).not.toBe('4.93')
  expect(errors).toEqual([])
})


test('project calendar LCD watermark and grouped toolbar stay usable', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.route('**/api/workbench*', route => route.fulfill({ json: { views: [{ key: 'manager', label: '项目经理' }], view: 'manager', updatedAt: new Date().toISOString(), projects: [], risks: [] } }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.locator('.pdm-project-workspace, .pdm-project-manager').first().waitFor()
  await page.locator('.pdm-sidebar').getByRole('button', { name: '项目总览', exact: true }).click()
  await page.locator('.pdm-navigation-tabs').getByRole('tab', { name: '项目日历', exact: true }).click()
  const calendar = page.getByLabel('项目日历', { exact: true })
  await expect(calendar.locator('.pc-month-watermark')).toHaveCount(2)
  await expect(calendar.locator('.pc-controls').getByRole('button', { name: '双月', exact: true })).toBeVisible()
  await expect(calendar.locator('.pc-toolbar .pc-filters')).toBeVisible()
  await page.setViewportSize({ width: 1518, height: 855 })
  await calendar.getByRole('button', { name: '最大化看板', exact: true }).click()
  const centers = await calendar.locator('.pc-month-watermark').evaluateAll(nodes => nodes.map(node => {
    const svg = node as SVGSVGElement
    const bounds = svg.getBBox()
    const point = new DOMPoint(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2).matrixTransform(svg.getScreenCTM()!)
    const grid = svg.parentElement!.getBoundingClientRect()
    return { x: Math.abs(point.x - grid.x - grid.width / 2), y: Math.abs(point.y - grid.y - grid.height / 2), bbox: [bounds.x,bounds.y,bounds.width,bounds.height], viewBox: svg.getAttribute("viewBox") }
  }))
  for (const center of centers) { expect(center.x).toBeLessThan(1); expect(center.y).toBeLessThan(1) }
  const before = await calendar.locator('.pc-month h2').first().innerText()
  await calendar.getByRole('button', { name: '下一期', exact: true }).click()
  await expect(calendar.locator('.pc-month h2').first()).not.toHaveText(before)
  await calendar.getByRole('button', { name: '回到本月', exact: true }).click()
  await page.screenshot({ path: testInfo.outputPath('calendar-lcd-board.png') })
  const overflow = await calendar.evaluate(el => el.scrollHeight > el.clientHeight + 2 || el.scrollWidth > el.clientWidth + 2)
  expect(overflow).toBe(false)
  await page.keyboard.press('Escape')
  await expect(calendar).not.toHaveClass(/is-maximized/)
  expect(errors).toEqual([])
})

test('drawing review automatically checks drawings awaiting the current reviewer', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1959, height: 1080 })
  await page.route('**/api/projects/*/drawing-review-candidates', route => route.fulfill({ json: [{ candidateId: 'review-candidate', modelDocumentId: 'doc-assembly', drawingDocumentId: 'doc-drawing', drawingNumber: 'DRAW-REVIEW', name: '待审核图纸', bomKinds: ['NonStandard'], state: 'InReview', selectable: false }] }))
  await page.route('**/api/projects/*/drawing-reviews', route => route.fulfill({ json: [{ id: 'auto-review', projectId, number: 'DR-AUTO', state: 'InReview', createdBy: 'engineer', createdAt: '2026-10-10T00:00:00Z', assignedReviewers: ['admin'], items: [{ id: 'review-item', packageId: 'auto-review', modelDocumentId: 'doc-assembly', drawingDocumentId: 'doc-drawing', drawingNumber: 'DRAW-REVIEW', name: '待审核图纸', drawingState: 'Pending', modelState: 'NotRequired', drawingCreatedBy: 'engineer' }], markups: [] }] }))
  await page.goto('/')
  await page.getByLabel('登录PLM').getByRole('textbox', { name: '账号' }).fill('admin')
  await page.getByLabel('登录PLM').getByRole('textbox', { name: '密码' }).fill('correct-password')
  await page.getByLabel('登录PLM').getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('navigation', { name: '项目功能' }).getByRole('button', { name: '图档', exact: true }).click()
  await expect(page.getByLabel('图纸审核面板', { exact: true })).toHaveClass(/is-collapsed/)
  await expect(page.locator('.drawing-review-panel')).toHaveCSS('width', '34px')
  await page.screenshot({ path: join(tmpdir(), 'pdm-drawing-review-default-collapsed.png'), animations: 'disabled' })
  await page.getByRole('button', { name: '展开图纸审核栏', exact: true }).click()
  await expect(page.getByLabel('选择图纸 DRAW-REVIEW')).toBeChecked()
  await expect(page.locator('.drawing-review-overview__batch')).toContainText('已选 1 张')
  await page.getByRole('button', { name: '折叠图纸审核栏', exact: true }).click()
  await expect(page.getByLabel('图纸审核面板', { exact: true })).toHaveClass(/is-collapsed/)
  const projectNavigation = page.getByRole('navigation', { name: '项目功能' })
  await projectNavigation.getByRole('button', { name: '信息', exact: true }).click()
  await projectNavigation.getByRole('button', { name: '图档', exact: true }).click()
  await expect(page.getByLabel('图纸审核面板', { exact: true })).toHaveClass(/is-collapsed/)
  await page.screenshot({ path: testInfo.outputPath('drawing-review-auto-selected.png') })
})

test('drawing review select all excludes approved and unavailable drawings', async ({ page }, testInfo) => {
  await page.route('**/api/projects/*/drawing-review-candidates', route => route.fulfill({ json: [
    { candidateId: 'ready', modelDocumentId: 'doc-assembly', drawingDocumentId: 'doc-drawing', drawingNumber: 'READY', bomKinds: ['NonStandard'], state: 'Ready', selectable: true },
    { candidateId: 'approved', modelDocumentId: 'approved-model', drawingDocumentId: 'approved-drawing', drawingNumber: 'APPROVED', bomKinds: ['NonStandard'], state: 'ApprovedCurrent', selectable: true },
    { candidateId: 'blocked', modelDocumentId: 'blocked-model', drawingNumber: 'BLOCKED', bomKinds: ['NonStandard'], state: 'Ready', selectable: false },
  ] }))
  await page.route('**/api/projects/*/drawing-reviews', route => route.fulfill({ json: [] }))
  await page.goto('/')
  await page.getByLabel('登录PLM').getByRole('textbox', { name: '账号' }).fill('admin')
  await page.getByLabel('登录PLM').getByRole('textbox', { name: '密码' }).fill('correct-password')
  await page.getByLabel('登录PLM').getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('navigation', { name: '项目功能' }).getByRole('button', { name: '图档', exact: true }).click()
  await page.getByRole('button', { name: '展开图纸审核栏' }).click()
  await expect(page.getByLabel('选择图纸 APPROVED', { exact: true })).toBeDisabled()
  await expect(page.getByLabel('选择图纸 BLOCKED', { exact: true })).toBeDisabled()
  await page.getByLabel('全选可操作图纸').check()
  await expect(page.getByLabel('选择图纸 READY', { exact: true })).toBeChecked()
  await expect(page.getByLabel('选择图纸 APPROVED', { exact: true })).not.toBeChecked()
  await expect(page.locator('.drawing-review-overview__batch')).toContainText('已选 1 张')
  await page.screenshot({ path: testInfo.outputPath('drawing-review-selectable-only.png') })
  await page.locator('.drawing-review-overview__batch .is-submit').click()
  await expect(page.getByRole('combobox', { name: '指定图纸审核人' })).toBeVisible()
  await expect(page.locator('.drawing-review-candidate.is-selected')).toHaveCount(1)
  await page.screenshot({ path: testInfo.outputPath('drawing-review-choose-reviewer.png') })
})

test('all release pages collapse workflow and package information and omit execution notes over LAN HTTP', async ({ page }, testInfo) => {
  test.setTimeout(120_000)
  await mockPlanCompanionPanels(page)
  await page.route('**/api/material-code/resolve', route => route.fulfill({ json: [] }))
  await page.setViewportSize({ width: 1959, height: 1080 })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  const scopes = ['StandardLongLead', 'StandardFormal', 'StandardSupplement', 'NonStandardLongLead', 'NonStandardWithDrawing', 'NonStandardSupplement', 'ElectricalLongLead', 'ElectricalFormal', 'ElectricalSupplement']
  const releases = scopes.map((scope, index) => ({ id: `compact-release-${index}`, projectId, number: `RP-COMPACT-${index}`, scope, state: 'Published', workflowVersion: 1,
    createdAt: '2026-10-23T00:00:00Z', publishedAt: '2026-10-23T01:00:00Z', selectedBomItemIds: [], standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [],
    approvalTasks: [{ id: `approved-${index}`, stepName: '工程师自检', stage: 0, assignee: 'engineer', decisionBy: 'engineer', decision: 'Approved', decidedAt: '2026-10-23T01:00:00Z', comment: '提交人自检' }] }))
  await page.route('**/api/projects/*/release-packages', route => route.fulfill({ json: releases }))
  await page.route('**/api/projects/*/release-preview-items*', route => route.fulfill({ json: [] }))
  await page.route('**/api/release-packages/*/item-comments', route => route.fulfill({ json: [] }))
  await page.route('**/api/release-packages/*/execution', route => route.fulfill({ json: { id: 'execution', state: 'Published', ready: true, acknowledgementIds: [], confirmedRemovedBomItemIds: [],
    workflowNotifications: [{ enabled: true, state: 'Sent', message: '应删除的通知说明' }],
    progress: { u9: { Standard: { state: 'Succeeded', message: '应删除的U9说明' } }, previewRequired: false, previewState: 'Succeeded', notificationRules: [], departments: [], deliveries: [] } } }))
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: '发布', exact: true }).click()
  const overview = page.getByRole('region', { name: '发布总览' })
  const drawer = page.locator('.pdm-bom-release-drawer')
  for (const release of releases) {
    await page.getByRole('button', { name: '发布', exact: true }).click()
    await overview.getByRole('button', { name: new RegExp(`${release.number} ·`) }).click()
    await expect(drawer).toBeVisible()
    const workflow = drawer.locator('details[aria-label="审批流程"]')
    const information = drawer.locator('details[aria-label="发布包信息"]')
    await expect(drawer.locator('.pdm-approval-chain')).toBeHidden()
    await expect(drawer.locator('.pdm-release-summary')).toBeHidden()
    await expect(drawer.locator('.execution-statuses')).toContainText('已回查确认')
    await expect(drawer).not.toContainText('应删除的通知说明')
    await expect(drawer).not.toContainText('应删除的U9说明')
    await expect(drawer).not.toContainText('本发布版本的U9同步和所需生产图纸已齐套')
    if (release === releases[0]) await drawer.screenshot({ path: join(tmpdir(), 'pdm-release-default-collapsed.png'), animations: 'disabled' })
    await workflow.locator('summary').click()
    await expect(drawer.locator('.pdm-approval-chain')).toBeVisible()
    await expect(workflow).toContainText('工程师自检')
    await information.locator('summary').click()
    await expect(drawer.locator('.pdm-release-summary')).toBeVisible()
    await expect(information).toContainText(release.number)
    await workflow.locator('summary').click()
    await information.locator('summary').click()
    await expect(drawer.locator('.pdm-approval-chain')).toBeHidden()
    await expect(drawer.locator('.pdm-release-summary')).toBeHidden()
    await drawer.locator('.el-drawer__close-btn').click()
    await expect(drawer).toBeHidden()
  }
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('empty electrical release never displays standard BOM items', async ({ page }, testInfo) => {
  await page.route('**/api/projects/*/boms/Electrical**', route => route.fulfill({ json: [] }))
  const releaseProject = { id: projectId, code: 'PRJ-REAL-001', name: '电气发布项目', owner: 'admin', vaultLocation: 'D:/PDM', releaseLocation: 'D:/Release', isActive: true, quantity: 1, serialNumbers: [], designers: [], canReadContent: true, effectiveProjectPermissions: ['project.edit', 'bom.electrical.edit', 'release.manage'] }
  await page.route('**/api/projects', route => route.fulfill({ json: [releaseProject] }))
  await page.route(`**/api/projects/${projectId}`, route => route.fulfill({ json: releaseProject }))
  await page.route('**/api/projects/*/release-packages', route => route.fulfill({ json: [] }))
  await page.goto('/')
  await page.getByLabel('登录PLM').getByRole('textbox', { name: '账号' }).fill('admin')
  await page.getByLabel('登录PLM').getByRole('textbox', { name: '密码' }).fill('correct-password')
  await page.getByLabel('登录PLM').getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: /电气件BOM/ }).click()
  await page.getByRole('button', { name: '发起发布', exact: true }).click()
  const drawer = page.locator('.pdm-bom-release-drawer')
  await expect(drawer).toContainText('电气件BOM审批发布')
  await drawer.getByLabel('发布类型', { exact: true }).selectOption('ElectricalFormal')
  await drawer.getByLabel('整包需求日期').fill('2026-10-20')
  await expect(drawer).toContainText('当前没有可发布的物料')
  await expect(drawer.getByRole('button', { name: '创建草稿', exact: true })).toBeDisabled()
  await expect(drawer.locator('input[aria-label^="本次发布物料"]')).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('empty-electrical-release.png') })
})

test('nonstandard supplement selects only two approved drawing updates against instance baseline', async ({ page }, testInfo) => {
  const items = Array.from({ length: 35 }, (_, i) => ({ id: `current-${i}`, summaryItemIds: [`current-${i}`, `second-${i}`], kind: 'NonStandard', sequence: i + 1, drawingNumber: `NS-${i}`, name: `零件${i}`, specification: `NS-${i}`, sourceDocumentId: `model-${i}`, sourceConfiguration: '默认', quantity: 2, unit: '个', revision: 'A', isComplete: true }))
  const baseline = items.flatMap((item, i) => [0, 1].map(n => ({ ...item, id: `old-${i}-${n}`, summaryItemIds: undefined, quantity: 1, revision: 'W1' })))
  const project = { id: projectId, code: 'PRJ-REAL-001', name: '非标变更项目', owner: 'admin', vaultLocation: 'D:/PDM', releaseLocation: 'D:/Release', isActive: true, quantity: 1, serialNumbers: [], designers: [], canReadContent: true, effectiveProjectPermissions: ['project.edit', 'bom.nonstandard.edit', 'release.manage'] }
  await page.route('**/api/projects', route => route.fulfill({ json: [project] }))
  await page.route(`**/api/projects/${projectId}`, route => route.fulfill({ json: project }))
  await page.route('**/api/projects/*/boms/NonStandard**', route => route.fulfill({ json: items }))
  await page.route('**/api/projects/*/bom-versions**', route => route.fulfill({ json: [{ id: 'baseline', projectId, kind: 'NonStandard', versionNumber: 1, label: 'N-B01', state: 'Released', items: baseline, createdBy: 'admin', createdAt: '2026-10-09T01:00:00Z', updatedBy: 'admin', updatedAt: '2026-10-09T01:00:00Z' }] }))
  await page.route('**/api/projects/*/release-packages', route => route.fulfill({ json: [{ id: 'published', projectId, number: 'RP-PUBLISHED', scope: 'NonStandardWithDrawing', state: 'Published', workflowVersion: 1, createdAt: '2026-10-09T00:00:00Z', publishedAt: '2026-10-09T01:00:00Z', selectedBomItemIds: [], standardBomSnapshot: [], nonStandardBomSnapshot: baseline, electricalBomSnapshot: [], steps: [] }] }))
  await page.route('**/api/projects/*/drawing-review-candidates', route => route.fulfill({ json: items.map((item, i) => ({ candidateId: `candidate-${i}`, bomItemId: item.id, modelDocumentId: `model-${i}`, drawingDocumentId: `drawing-${i}`, drawingNumber: item.drawingNumber, name: item.name, bomKinds: ['NonStandard'], modelRevision: 'W1', drawingRevision: 'W1', state: 'ApprovedCurrent', selectable: false, hasUnreleasedDrawingChanges: i === 7 || i === 9 })) }))
  await page.route('**/api/projects/*/drawing-reviews', route => route.fulfill({ json: [{ id: 'update-review', projectId, number: 'DR-UPDATE', state: 'Approved', createdAt: '2026-10-10T01:00:00Z', approvedAt: '2026-10-10T02:00:00Z', createdBy: 'designer', markups: [], items: [7, 9].map(i => ({ id: `review-${i}`, packageId: 'update-review', bomItemId: items[i]!.id, drawingNumber: `NS-${i}`, name: `零件${i}`, modelDocumentId: `model-${i}`, drawingDocumentId: `drawing-${i}`, modelRevision: 'W1', drawingRevision: 'W1', modelState: 'NotRequired', drawingState: 'Approved', effectiveModelVersionId: 'v2', effectiveDrawingVersionId: 'v2' })) }] }))
  await page.goto('/')
  await page.getByLabel('登录PLM').getByRole('textbox', { name: '账号' }).fill('admin')
  await page.getByLabel('登录PLM').getByRole('textbox', { name: '密码' }).fill('correct-password')
  await page.getByLabel('登录PLM').getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: /非标件BOM/ }).click()
  await page.getByRole('button', { name: '发起发布', exact: true }).click()
  const drawer = page.locator('.pdm-bom-release-drawer')
  await expect(drawer.getByLabel('发布类型', { exact: true })).toHaveValue('NonStandardSupplement')
  await expect(drawer.locator('.release-change-tag')).toHaveCount(2)
  await expect(drawer).toContainText('NS-7')
  await expect(drawer).toContainText('NS-9')
  for (const checkbox of await drawer.locator('.pdm-edit-table.is-supplement tbody input[type="checkbox"]').all()) await expect(checkbox).toBeChecked()
  const changedChoices = drawer.locator('.pdm-edit-table.is-supplement tbody input[type="checkbox"]')
  await expect(changedChoices.first()).toBeEnabled()
  await changedChoices.first().uncheck()
  await expect(changedChoices.first()).not.toBeChecked()
  await expect(changedChoices.last()).toBeChecked()
  await expect(drawer.locator('.is-release-unselected')).toContainText('本次不发布')
  await page.screenshot({ path: testInfo.outputPath('nonstandard-two-drawing-updates.png') })
})

test('OPL uses the existing sidebar and a responsive right drawer on LAN HTTP', async ({ page }) => {
  const failedResponses: string[] = []
  page.on('response', response => { if (response.status() >= 400) failedResponses.push(`${response.status()} ${response.url()}`) })
  const childId = '22222222-2222-2222-2222-222222222222'
  const root = { id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: '真实工程师', primaryProjectManager: 'admin', vaultLocation: 'D:/PDM/TEST', releaseLocation: 'D:/Release/TEST', isActive: true, quantity: 1, serialNumbers: [], designers: [], canReadContent: true }
  const child = { ...root, id: childId, code: 'PRJ-REAL-001-1', name: '子设备', parentProjectId: projectId, rootProjectId: projectId }
  await page.route('**/api/projects**', async route => {
    const url = new URL(route.request().url())
    if (url.pathname === '/api/projects') return route.fulfill({ json: [root, child] })
    if (url.pathname.endsWith('/plan')) return route.fulfill({ json: null })
    if (url.pathname.endsWith('/budget')) return route.fulfill({ json: { projectId: url.pathname.split('/')[3], rows: [], orders: [], notes: [], budgetAmount: null, actualAmount: null, estimatedAmount: null, overrunAmount: null, canEdit: false, canViewReserve: false, canViewSettlement: false, rowVersion: 0, updatedBy: null, updatedAt: null } })
    if (url.pathname === `/api/projects/${projectId}`) return route.fulfill({ json: root })
    if (url.pathname === `/api/projects/${childId}`) return route.fulfill({ json: child })
    if (url.pathname.startsWith(`/api/projects/${childId}/`)) return route.fallback({ url: url.toString().replace(childId, projectId) })
    return route.fallback()
  })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  let saved = false
  let submitted = ''
  const issue = { id: 'opl-test', projectId: 'project-1', number: 1, description: '相机支架松动', priority: '高', status: '待处理', owner: 'admin', dueDate: '2026-10-10', solution: '', createdBy: 'admin', createdAt: '2026-10-12T00:00:00Z', completedAt: null, updatedBy: 'admin', updatedAt: '2026-10-12T00:00:00Z', rowVersion: 1, attachments: [] }
  await page.route(/\/api\/projects\/[^/]+\/opl(?:\/[^/]+)?$/, async route => {
    if (route.request().method() !== 'GET') { saved = true; submitted = route.request().postData() || ''; return route.fulfill({ json: issue }) }
    return route.fulfill({ json: { rows: [{ issue, canEdit: true, canComplete: true }, { issue: { ...issue, id: 'child-issue', number: 2, description: '子项目专属问题', relatedProjectId: childId }, canEdit: true, canComplete: true }], people: [{ username: 'admin', displayName: '系统管理员' }], rootProject: root, subprojects: [child] } })
  })
  const completedAt = new Date().toISOString()
  let completionRequested = false
  await page.route('**/api/projects/*/opl/*/complete', async route => {
    expect(route.request().postDataJSON()).toEqual({ expectedRowVersion: 1, notifyGroup: true })
    completionRequested = true
    return route.fulfill({ json: { ...issue, status: '已完成', completedAt, rowVersion: 2 } })
  })
  await page.setViewportSize({ width: 1600, height: 1000 })
  await page.goto('http://192.168.2.8:5173/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: '选择项目号 PRJ-REAL-001', exact: true }).click()
  const sidebar = page.locator('.pdm-project-sidebar')
  await expect(page.locator('.pdm-project-family button[aria-current="page"] .pdm-project-family__identity strong')).toHaveText('PRJ-REAL-001')
  await expect(sidebar).not.toContainText('切换中…')
  const original = await sidebar.innerText()
  const geometry = await sidebar.boundingBox()
  await page.getByRole('button', { name: 'OPL', exact: true }).click()
  await expect(page.getByLabel('开口问题清单')).toBeVisible()
  expect(await sidebar.innerText()).toBe(original)
  expect(await sidebar.boundingBox()).toEqual(geometry)
  await expect(page.locator('.opl-table')).toContainText('相机支架松动')
  await expect(page.locator('.opl-form')).toHaveCount(0)
  expect(await page.locator('body').evaluate(el => el.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: join(tmpdir(), 'pdm-opl-list.png') })
  await page.getByRole('button', { name: '新增问题', exact: true }).click()
  const drawer = page.getByRole('dialog', { name: '新增开口问题' })
  await expect(drawer).toBeVisible()
  await expect.poll(async () => { const rect = await drawer.boundingBox(); return Math.abs(rect!.x + rect!.width - 1600) }).toBeLessThan(2)
  const saveAction = drawer.getByRole('button', { name: '保存问题', exact: true })
  await expect(drawer.locator('.el-drawer__header').getByRole('button', { name: '保存问题', exact: true })).toBeVisible()
  await expect(drawer.locator('.pdm-panel-topbar-title [role="heading"]')).toHaveCSS('font-size', '16px')
  await expect(drawer.locator('.el-drawer__footer')).toHaveCount(0)
  const closeBounds = await drawer.locator('.el-drawer__close-btn').boundingBox()
  const actionBounds = await saveAction.boundingBox()
  expect(actionBounds!.x + actionBounds!.width).toBeLessThanOrEqual(closeBounds!.x)
  for (const name of ['问题描述', '解决措施']) {
    expect((await drawer.getByRole('textbox', { name, exact: true }).boundingBox())!.height).toBe(100)
  }
  await drawer.getByRole('button', { name: '保存问题', exact: true }).click()
  await expect(drawer).toContainText('请填写问题描述。')
  await drawer.getByRole('textbox', { name: '问题描述', exact: true }).fill('浏览器录入验证')
  await page.screenshot({ path: join(tmpdir(), 'pdm-opl-drawer.png') })
  await drawer.getByRole('button', { name: '取消', exact: true }).click()
  await page.getByRole('button', { name: '继续编辑', exact: true }).click()
  await expect(drawer).toBeVisible()
  await drawer.getByRole('button', { name: '保存问题', exact: true }).click()
  await expect(drawer).toBeHidden()
  expect(saved).toBe(true)
  expect(submitted).toContain('"relatedProjectId":null')
  await page.locator('.opl-table tbody tr').first().click()
  const edit = page.getByRole('dialog', { name: '编辑开口问题' })
  await expect(edit.getByRole('textbox', { name: '问题描述', exact: true })).toHaveValue('相机支架松动')
  await edit.getByRole('button', { name: '取消', exact: true }).click()
  await page.getByRole('button', { name: '选择项目号 PRJ-REAL-001-1', exact: true }).click()
  await expect(page.locator('.opl-table')).toContainText('子项目专属问题')
  await expect(page.locator('.opl-table')).not.toContainText('相机支架松动')
  await page.getByRole('button', { name: '新增问题', exact: true }).click()
  await expect(drawer.locator('.el-select').first()).toContainText('PRJ-REAL-001-1 · 子设备')
  await expect(drawer.locator('input[disabled]').first()).toHaveValue('PRJ-REAL-001 · 真实装配项目')
  await drawer.getByRole('textbox', { name: '问题描述', exact: true }).fill('子项目新增验证')
  await drawer.getByRole('button', { name: '保存问题', exact: true }).click()
  await expect(drawer).toBeHidden()
  expect(submitted).toContain(`"relatedProjectId":"${childId}"`)
  await page.getByRole('button', { name: '选择项目号 PRJ-REAL-001', exact: true }).click()
  await expect(page.locator('.opl-table')).toContainText('相机支架松动')
  await expect(page.locator('.opl-table')).toContainText('子项目专属问题')
  await page.setViewportSize({ width: 1100, height: 760 })
  await expect(page.locator('.opl-table')).toBeVisible()
  expect(await page.locator('body').evaluate(el => el.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: join(tmpdir(), 'pdm-opl-narrow.png') })
  await page.getByRole('button', { name: '新增问题', exact: true }).click()
  await page.setViewportSize({ width: 600, height: 800 })
  await expect(drawer).toBeVisible()
  await expect.poll(async () => (await drawer.boundingBox())!.width).toBeLessThanOrEqual(600)
  const narrowHeader = await drawer.locator('.el-drawer__header').boundingBox()
  const narrowSave = await drawer.getByRole('button', { name: '保存问题', exact: true }).boundingBox()
  expect(narrowSave!.y).toBeGreaterThanOrEqual(narrowHeader!.y)
  expect(narrowSave!.y + narrowSave!.height).toBeLessThanOrEqual(narrowHeader!.y + narrowHeader!.height)
  await drawer.getByRole('button', { name: '取消', exact: true }).click()
  await page.setViewportSize({ width: 1600, height: 1000 })
  await expect(page.locator('.opl-table').getByRole('columnheader', { name: '子项目', exact: true })).toBeVisible()
  await page.getByText('更新或完成后通知钉钉群', { exact: true }).click()
  await page.locator('.opl-table').getByRole('button', { name: '完成', exact: true }).first().click()
  await expect(page.locator('.opl-table')).not.toContainText('相机支架松动')
  expect(completionRequested).toBe(true)
  await expect(page.getByRole('dialog', { name: '编辑开口问题' })).toBeHidden()
  await page.locator('.opl-filters .el-select').first().locator('.el-select__wrapper').click()
  await page.getByRole('option', { name: '全部状态', exact: true }).click()
  await expect(page.locator('.opl-table')).toContainText('相机支架松动')
  const expectedDate = await page.evaluate(value => new Date(value).toLocaleDateString('sv-SE'), completedAt)
  await expect(page.locator('.opl-table')).toContainText(expectedDate)
  await page.screenshot({ path: join(tmpdir(), 'pdm-opl-completed.png') })
  expect(errors, failedResponses.join('\n')).toEqual([])
})

test('custom BOM drawer keeps batch actions beside the header close button', async ({ page }) => {
  await page.setViewportSize({ width: 1518, height: 891 })
  await page.goto('http://192.168.2.8:5173/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '标准件BOM' }).click()
  await page.locator('.pdm-edit-table tbody tr').first().getByLabel('选择物料').check()
  await page.getByRole('button', { name: '编辑', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '批量编辑BOM属性' })
  const header = dialog.locator(':scope > header')
  await expect(header.locator('.pdm-panel-topbar-title h3')).toHaveCSS('font-size', '16px')
  const save = header.getByRole('button', { name: '确认批量修改', exact: true })
  await expect(save).toBeVisible()
  await dialog.getByLabel('修改品牌').check()
  await dialog.getByPlaceholder('勾选后留空即清空').nth(2).fill('UPTON')
  await expect(save).toBeEnabled()
  const close = await header.getByRole('button', { name: '关闭批量编辑', exact: true }).boundingBox()
  const bounds = await save.boundingBox()
  expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(close!.x)
  await expect(dialog.locator(':scope > footer')).toHaveCount(0)
  await page.screenshot({ path: join(tmpdir(), 'pdm-custom-drawer-header-actions.png') })
  await header.getByRole('button', { name: '取消', exact: true }).click()
  await expect(dialog).toHaveCount(0)
})
