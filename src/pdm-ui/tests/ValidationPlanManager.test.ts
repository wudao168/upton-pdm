import ElementPlus from 'element-plus'
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ValidationPlanManager from '../src/components/ValidationPlanManager.vue'
import { userDisplayNameKey } from '../src/userDisplay'

const api = vi.hoisted(() => ({
  readValidationCheckCatalog: vi.fn(),
  readProjectValidationPlan: vi.fn(),
  saveProjectValidationPlan: vi.fn(),
  saveValidationCheckCategory: vi.fn(),
  deleteValidationCheckCategory: vi.fn(),
  saveValidationCheckItem: vi.fn(),
  deleteValidationCheckItem: vi.fn(),
  exportProjectValidationPlan: vi.fn(),
  createProjectValidationPlanRevision: vi.fn(),
  submitProjectValidationPlan: vi.fn(),
  decideValidationPlanApproval: vi.fn(),
  uploadValidationPlanAttachment: vi.fn(),
  downloadValidationPlanAttachment: vi.fn(),
  recognizeValidationPlanAttachment: vi.fn(),
  readValidationPlanExecutionRecords: vi.fn(),
  confirmValidationPlanExecution: vi.fn(),
}))

vi.mock('../src/api', () => api)

const category = { id: 'category-1', name: '安全相关', sortOrder: 10, isActive: true, itemCount: 1, referenceCount: 0, createdBy: 'system', createdAt: '2026-09-09T00:00:00Z', updatedBy: 'system', updatedAt: '2026-09-09T00:00:00Z', rowVersion: 1 }
const item = { id: 'item-1', categoryId: category.id, content: '急停按钮应在一秒内触及', defaultInformationSource: '内部评审', sortOrder: 10, isActive: true, referenceCount: 0, createdBy: 'system', createdAt: '2026-09-09T00:00:00Z', updatedBy: 'system', updatedAt: '2026-09-09T00:00:00Z', rowVersion: 1 }
const projectDefaults = { owner: 'engineer', stage: '正常', vaultName: 'UPLM', vaultLocation: '', releaseLocation: '', quantity: 1, serialNumbers: [], responsibleUsers: [], collaborativeProjectManagers: [], designers: [], canAssignExecutionUnit: false, canManageMainStaffing: false, canAssignDesigners: false, canReadContent: true }
const rootProject = { ...projectDefaults, id: 'project-root', code: 'P700005', name: '钢珠送料机' }
const childProject = { ...projectDefaults, id: 'project-1', code: 'P700005-3', name: '切料机构', parentProjectId: rootProject.id, rootProjectId: rootProject.id }
const childPlan = {
  id: 'plan-1', projectId: childProject.id, preparedBy: 'engineer', validationDate: '2026-09-09',
  revisionNumber: 1, state: 'Draft' as const, approvalTasks: [],
  attachments: [
    { id: 'file-plan', planId: 'plan-1', kind: 'PlanDocument' as const, originalFileName: '验证计划.xlsx', fileVersion: 2, storageRelativePath: '验收资料/验证计划/.versions/R001/PlanDocument/验证计划.xlsx', fileLength: 2048, sha256: 'A'.repeat(64), uploadedBy: 'engineer', uploadedAt: '2026-09-09T12:00:00Z' },
    { id: 'file-evidence', planId: 'plan-1', kind: 'Evidence' as const, originalFileName: 'GRR数据.xlsx', fileVersion: 1, storageRelativePath: '验收资料/验证计划/.versions/R001/Evidence/GRR数据.xlsx', fileLength: 4096, sha256: 'B'.repeat(64), uploadedBy: 'engineer', uploadedAt: '2026-09-09T12:10:00Z' },
  ],
  items: [], createdBy: 'engineer', createdAt: '2026-09-09T00:00:00Z', updatedBy: 'engineer', updatedAt: '2026-09-09T12:10:00Z', rowVersion: 1,
}

describe('ValidationPlanManager', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    api.readValidationCheckCatalog.mockResolvedValue({ categories: [category], items: [item] })
    api.readProjectValidationPlan.mockResolvedValue(null)
    api.readValidationPlanExecutionRecords.mockResolvedValue([])
    api.saveProjectValidationPlan.mockImplementation(async (projectId: string, input: any) => ({
      id: 'plan-1', projectId, preparedBy: input.preparedBy, validationDate: input.validationDate,
      revisionNumber: 1, state: 'Draft', approvalTasks: [], attachments: [],
      items: input.items.map((value: any, index: number) => ({ ...value, id: `row-${index}`, catalogCategoryId: category.id, categoryName: category.name, validationContent: item.content })),
      createdBy: 'engineer', createdAt: '2026-09-09T00:00:00Z', updatedBy: 'engineer', updatedAt: '2026-09-09T00:00:00Z', rowVersion: 1,
    }))
  })

  afterEach(() => { document.body.innerHTML = '' })

  it('最近保存人显示姓名而不是账号', async () => {
    api.readProjectValidationPlan.mockResolvedValue(childPlan)
    const wrapper = mount(ValidationPlanManager, {
      attachTo: document.body,
      props: { projectId: 'project-1', projectCode: 'P700005-3', projectName: '切料机构', projects: [rootProject, childProject], token: 'token', currentUsername: 'engineer', currentDisplayName: '工程师', canEdit: true, canManageCatalog: true },
      global: { plugins: [ElementPlus], provide: { [userDisplayNameKey as symbol]: (username?: string | null) => username === 'engineer' ? '王勇煌' : username || '' } },
    })
    await flushPromises()
    await wrapper.findAll('tbody tr').find(row => row.text().includes('P700005-3'))!.trigger('click')
    await flushPromises()

    expect(wrapper.get('.validation-plan__footer').text()).toContain('最近保存：王勇煌')
    expect(wrapper.get('.validation-plan__footer').text()).not.toContain('engineer')
    wrapper.unmount()
  })

  it('按分类选择检查项并保存项目快照字段', async () => {
    const wrapper = mount(ValidationPlanManager, {
      attachTo: document.body,
      props: { projectId: 'project-1', projectCode: 'P700005-3', projectName: '切料机构', projects: [rootProject, childProject], token: 'token', currentUsername: 'engineer', currentDisplayName: '工程师', canEdit: true, canManageCatalog: true },
      global: { plugins: [ElementPlus], provide: { [userDisplayNameKey as symbol]: (username?: string | null) => username === 'engineer' ? '工程师' : username || '' } },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('P700005 · 主项目及子项目验证计划')
    expect(wrapper.text()).toContain('模板管理')
    expect(wrapper.text()).not.toContain('检查项库管理')
    await wrapper.findAll('tbody tr').find(row => row.text().includes('P700005-3'))!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('当前项目还没有验证检查项')
    const uploadActions = wrapper.findAll<HTMLButtonElement>('.validation-plan__upload-action')
    expect(uploadActions).toHaveLength(2)
    expect(uploadActions.every(button => button.element.disabled)).toBe(true)
    await wrapper.findAll('button').find(button => button.text().includes('选取内容'))!.trigger('click')
    await flushPromises()

    const checkbox = document.querySelector<HTMLInputElement>('.validation-selector__items input[type="checkbox"]')!
    checkbox.click()
    await flushPromises()
    const addButton = [...document.querySelectorAll<HTMLButtonElement>('button')].find(button => button.textContent?.includes('加入计划'))!
    addButton.click()
    await flushPromises()

    expect(wrapper.text()).toContain('急停按钮应在一秒内触及')
    await wrapper.findAll('button').find(button => button.text().includes('保存'))!.trigger('click')
    await flushPromises()

    expect(api.saveProjectValidationPlan).toHaveBeenCalledWith('project-1', expect.objectContaining({
      preparedBy: '工程师',
      items: [expect.objectContaining({ catalogItemId: item.id, informationSource: '内部评审', sortOrder: 1 })],
    }), 'token')
    wrapper.unmount()
  })

  it('新建计划自动带入当前项目、当前用户和当天日期且四项只读', async () => {
    const wrapper = mount(ValidationPlanManager, {
      attachTo: document.body,
      props: { projectId: 'project-1', projectCode: 'P700005-3', projectName: '切料机构', projects: [rootProject, childProject], token: 'token', currentUsername: 'engineer', currentDisplayName: '王勇煌', canEdit: true, canManageCatalog: true },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    await wrapper.findAll('tbody tr').find(row => row.text().includes('P700005-3'))!.trigger('click')
    await flushPromises()

    const fields = wrapper.findAll<HTMLInputElement>('.validation-plan__meta input')
    expect(fields.map(field => field.element.value)).toEqual([
      'P700005-3',
      '切料机构',
      '王勇煌',
      new Date().toLocaleDateString('sv-SE', { timeZone: 'Asia/Shanghai' }),
    ])
    expect(fields.every(field => field.element.readOnly)).toBe(true)
    wrapper.unmount()
  })

  it('责任人列显示在结果列之前', async () => {
    const planWithRow = { ...childPlan, items: [{ id: 'row-1', catalogCategoryId: category.id, catalogItemId: item.id, categoryName: category.name, validationContent: item.content, informationSource: '内部评审', validationDate: null, result: null, responsiblePerson: null, remark: null, sortOrder: 1 }] }
    api.readProjectValidationPlan.mockImplementation(async (projectId: string) => projectId === childProject.id ? planWithRow : null)
    const wrapper = mount(ValidationPlanManager, {
      attachTo: document.body,
      props: { projectId: 'project-1', projectCode: 'P700005-3', projectName: '切料机构', projects: [rootProject, childProject], token: 'token', currentUsername: 'engineer', currentDisplayName: '王勇煌', canEdit: true, canManageCatalog: true },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    await wrapper.findAll('tbody tr').find(row => row.text().includes('P700005-3'))!.trigger('click')
    await flushPromises()

    expect(wrapper.findAll('.validation-plan__table th').map(cell => cell.text())).toEqual([
      '序号', '分类', '验证内容', '信息来源', '验证日期', '责任人', '结果', '备注', '操作',
    ])
    wrapper.unmount()
  })

  it('可添加并保存人工验证行', async () => {
    const wrapper = mount(ValidationPlanManager, {
      attachTo: document.body,
      props: { projectId: 'project-1', projectCode: 'P700005-3', projectName: '切料机构', projects: [rootProject, childProject], token: 'token', currentUsername: 'engineer', currentDisplayName: '工程师', canEdit: true, canManageCatalog: true },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    await wrapper.findAll('tbody tr').find(row => row.text().includes('P700005-3'))!.trigger('click')
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().includes('添加人工项'))!.trigger('click')
    await flushPromises()

    const content = wrapper.find<HTMLInputElement>('input[placeholder="请输入验证内容"]')
    await content.setValue('人工确认安全门互锁')
    await wrapper.findAll('button').find(button => button.text().includes('保存'))!.trigger('click')
    await flushPromises()

    expect(api.saveProjectValidationPlan).toHaveBeenCalledWith('project-1', expect.objectContaining({
      items: [expect.objectContaining({ catalogItemId: null, validationContent: '人工确认安全门互锁', sortOrder: 1 })],
    }), 'token')
    wrapper.unmount()
  })

  it('支持全选和取消全选当前检查项列表', async () => {
    const wrapper = mount(ValidationPlanManager, {
      attachTo: document.body,
      props: { projectId: 'project-1', projectCode: 'P700005-3', projectName: '切料机构', projects: [rootProject, childProject], token: 'token', currentUsername: 'engineer', currentDisplayName: '工程师', canEdit: true, canManageCatalog: true },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    await wrapper.findAll('tbody tr').find(row => row.text().includes('P700005-3'))!.trigger('click')
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().includes('选取内容'))!.trigger('click')
    await flushPromises()

    const selectAll = document.querySelector<HTMLInputElement>('.validation-selector__select-all input')!
    selectAll.click()
    await flushPromises()
    expect(document.body.textContent).toContain('已选 1 项')

    selectAll.click()
    await flushPromises()
    expect(document.body.textContent).toContain('已选 0 项')
    wrapper.unmount()
  })

  it('汇总页只展示当前主项目及其子项目并可进入后返回', async () => {
    const unrelatedProject = { ...projectDefaults, id: 'project-other', code: 'P700006', name: '其他主项目' }
    const wrapper = mount(ValidationPlanManager, {
      attachTo: document.body,
      props: { projectId: childProject.id, projectCode: childProject.code, projectName: childProject.name, projects: [rootProject, childProject, unrelatedProject], token: 'token', currentUsername: 'engineer', currentDisplayName: '工程师', canEdit: true, canManageCatalog: true },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('P700005')
    expect(wrapper.text()).toContain('P700005-3')
    expect(wrapper.text()).not.toContain('P700006')
    expect(api.readProjectValidationPlan).toHaveBeenCalledWith(rootProject.id, 'token')
    expect(api.readProjectValidationPlan).toHaveBeenCalledWith(childProject.id, 'token')
    expect(api.readProjectValidationPlan).not.toHaveBeenCalledWith(unrelatedProject.id, 'token')

    const summaryRow = wrapper.findAll('tbody tr').find(row => row.text().includes('P700005-3'))!
    expect(wrapper.text()).not.toContain('进入计划')
    await summaryRow.trigger('click')
    await flushPromises()
    expect(wrapper.find('button[title="返回验证计划列表"]').exists()).toBe(true)
    await wrapper.find<HTMLButtonElement>('button[title="返回验证计划列表"]').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('共 2 个项目')
    wrapper.unmount()
  })

  it('后台项目列表刷新时保持在当前验证计划明细', async () => {
    const wrapper = mount(ValidationPlanManager, {
      attachTo: document.body,
      props: { projectId: childProject.id, projectCode: childProject.code, projectName: childProject.name, projects: [rootProject, childProject], token: 'token', currentUsername: 'engineer', currentDisplayName: '工程师', canEdit: true, canManageCatalog: true },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    await wrapper.findAll('tbody tr').find(row => row.text().includes('P700005-3'))!.trigger('click')
    await flushPromises()
    expect(wrapper.find('button[title="返回验证计划列表"]').exists()).toBe(true)

    await wrapper.setProps({ projects: [{ ...rootProject }, { ...childProject }] })
    await flushPromises()
    expect(wrapper.find('button[title="返回验证计划列表"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('P700005-3 · 切料机构')
    wrapper.unmount()
  })

  it('汇总页分别查看验证计划文件和附件且不触发行进入', async () => {
    api.readProjectValidationPlan.mockImplementation(async (projectId: string) => projectId === childProject.id ? childPlan : null)
    const wrapper = mount(ValidationPlanManager, {
      attachTo: document.body,
      props: { projectId: childProject.id, projectCode: childProject.code, projectName: childProject.name, projects: [rootProject, childProject], token: 'token', currentUsername: 'engineer', currentDisplayName: '工程师', canEdit: true, canManageCatalog: true },
      global: { plugins: [ElementPlus], provide: { [userDisplayNameKey as symbol]: (username?: string | null) => username === 'engineer' ? '工程师' : username || '' } },
    })
    await flushPromises()

    expect(wrapper.find('thead').text()).toContain('验证计划')
    expect(wrapper.find('thead').text()).toContain('附件')
    const planFilesButton = wrapper.find<HTMLButtonElement>('button[aria-label="查看 P700005-3 的验证计划文件"]')
    expect(planFilesButton.text()).toBe('1')
    await planFilesButton.trigger('click')
    await flushPromises()

    expect(wrapper.find('button[title="返回验证计划列表"]').exists()).toBe(false)
    expect(document.body.textContent).toContain('P700005-3 · 验证计划文件')
    expect(document.body.textContent).toContain('验证计划.xlsx')
    expect(document.body.textContent).toContain('V2')
    expect(document.body.textContent).toContain('工程师')
    expect(document.body.textContent).toContain('2.0 KB')
    expect(document.body.textContent).toContain('A'.repeat(64))

    const closeButton = [...document.querySelectorAll<HTMLButtonElement>('.el-dialog button')].find(button => button.textContent?.trim() === '关闭')!
    closeButton.click()
    await flushPromises()
    const evidenceButton = wrapper.find<HTMLButtonElement>('button[aria-label="查看 P700005-3 的附件"]')
    expect(evidenceButton.text()).toBe('1')
    await evidenceButton.trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('GRR数据.xlsx')
    expect(document.body.textContent).toContain('B'.repeat(64))
    wrapper.unmount()
  })

  it('审批生效后开放上传并由人工确认OCR结果到执行记录', async () => {
    const planItem = { id: 'row-1', catalogCategoryId: category.id, catalogItemId: item.id, categoryName: category.name, validationContent: item.content, informationSource: '内部评审', validationDate: null, result: null, responsiblePerson: null, remark: null, sortOrder: 1 }
    const attachment = { id: 'file-image', planId: childPlan.id, kind: 'PlanDocument' as const, originalFileName: '现场结果.png', fileVersion: 1, storageRelativePath: '验收资料/验证计划/现场结果.png', fileLength: 2048, sha256: 'C'.repeat(64), uploadedBy: 'engineer', uploadedAt: '2026-09-10T00:00:00Z' }
    const effectivePlan = { ...childPlan, state: 'Effective' as const, effectiveAt: '2026-09-10T00:20:00Z', items: [planItem], attachments: [attachment] }
    api.readProjectValidationPlan.mockImplementation(async (projectId: string) => projectId === childProject.id ? effectivePlan : null)
    api.recognizeValidationPlanAttachment.mockResolvedValue({ planId: childPlan.id, attachmentId: attachment.id, originalFileName: attachment.originalFileName, recognizedAt: '2026-09-10T00:10:00Z', ocrText: `${item.content} 结果：合格`, candidates: [{ planItemId: planItem.id, categoryName: category.name, validationContent: item.content, matchConfidence: 0.98, matchStatus: 'Matched', recognizedResult: '合格', recognizedValidationDate: '2026-09-10', recognizedResponsiblePerson: '张三', recognizedRemark: null, sourceText: `${item.content} 结果：合格` }] })
    api.confirmValidationPlanExecution.mockResolvedValue({ id: 'execution-1', planId: childPlan.id, sourceAttachmentId: attachment.id, sourceFileName: attachment.originalFileName, ocrText: `${item.content} 结果：合格`, items: [], confirmedBy: 'engineer', confirmedAt: '2026-09-10T00:20:00Z' })
    const wrapper = mount(ValidationPlanManager, {
      attachTo: document.body,
      props: { projectId: childProject.id, projectCode: childProject.code, projectName: childProject.name, projects: [rootProject, childProject], token: 'token', currentUsername: 'engineer', currentDisplayName: '工程师', canEdit: true, canManageCatalog: true },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    await wrapper.findAll('tbody tr').find(row => row.text().includes('P700005-3'))!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('编制日期')
    expect(wrapper.findAll<HTMLInputElement>('.validation-plan__meta input[type="date"]')[0]!.element.value).toBe('2026-09-10')
    expect(wrapper.findAll<HTMLInputElement>('.validation-plan__meta input[type="date"]')[0]!.element.readOnly).toBe(true)
    expect(wrapper.findAll<HTMLButtonElement>('button').find(button => button.text().includes('上传计划'))!.element.disabled).toBe(false)
    expect(wrapper.findAll<HTMLButtonElement>('button').find(button => button.text().includes('上传附件'))!.element.disabled).toBe(false)
    await wrapper.findAll('button').find(button => button.text().includes('识别结果'))!.trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('OCR结果不会修改已审批的验证计划')
    expect(document.body.textContent).toContain('已匹配')
    expect(document.querySelector<HTMLInputElement>('.validation-recognition__table input[placeholder="人工确认结果"]')!.value).toBe('合格')
    const confirmButton = [...document.querySelectorAll<HTMLButtonElement>('.el-dialog button')].find(button => button.textContent?.includes('确认写入执行记录'))!
    confirmButton.click()
    await flushPromises()
    expect(api.confirmValidationPlanExecution).toHaveBeenCalledWith(childPlan.id, expect.objectContaining({
      sourceAttachmentId: attachment.id,
      items: [expect.objectContaining({ planItemId: planItem.id, result: '合格', responsiblePerson: '张三' })],
    }), 'token')
    wrapper.unmount()
  })
})
