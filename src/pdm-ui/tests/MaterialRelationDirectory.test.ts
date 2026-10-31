import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { describe, expect, it, vi } from 'vitest'
import MaterialRelationDirectory from '../src/components/MaterialRelationDirectory.vue'
import MaterialRelationCategories from '../src/components/MaterialRelationCategories.vue'
import { matchesStandardCategory } from '../src/standardLibraryCategories'
import type { MaterialRelationTemplate, StandardLibraryCategory } from '../src/types'

const api = vi.hoisted(() => ({ listMaterials: vi.fn(), listMaterialRelationCategories: vi.fn(), getMaterialRelationCategories: vi.fn(), assignMaterialRelationCategory: vi.fn() }))
vi.mock('../src/api', () => api)
const category = (id: string, name: string, parentId: string | null = null) => ({ id, name, parentId, isActive: true, sortOrder: 1 }) as StandardLibraryCategory
const root = category('root', '机械件'), child = category('child', '附件', 'root')
const revision = { id: 'released', version: 1, groups: [{ id: 'group', name: '附件组', isRequired: true, selectionMode: 0, minSelection: 1, maxSelection: 1, options: [{ id: 'part', materialCode: 'PART-1', materialName: '安装附件', quantityMode: 0, quantityPerSet: 2, unitCode: '001' }] }] }
const relation = { id: 'relation', mainMaterialId: 'main', mainMaterialCode: 'MAIN-1', mainMaterialName: '主料', name: '主料配件', updatedAt: '', standardLibraryCategories: [child], publishedRevision: revision, draftRevision: { ...revision, version: 2 } } as unknown as MaterialRelationTemplate

describe('关联配置统一分类', () => {
  it('父分类包含子分类，多分类匹配，未分类仅包含无归属项', () => {
    expect(matchesStandardCategory([root, child], 'root', ['child'])).toBe(true)
    expect(matchesStandardCategory([root, child], 'child', ['other', 'child'])).toBe(true)
    expect(matchesStandardCategory([root, child], '__unclassified__', ['child'])).toBe(false)
    expect(matchesStandardCategory([root, child], '__unclassified__', [])).toBe(true)
  })

  it('按继承分类筛选，点击料号展开已发布配件，重复点击收起', async () => {
    api.listMaterials.mockResolvedValue([])
    api.listMaterialRelationCategories.mockResolvedValue([root, child])
    const wrapper = mount(MaterialRelationDirectory, { props: { token: 'token', canManage: false, canPublish: false, relations: [relation, { ...relation, id: 'missing', mainMaterialCode: 'MAIN-2', standardLibraryCategories: [], draftRevision: null }] }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const code = wrapper.get('button[aria-label="展开关联配置 MAIN-1 的配件明细"]')
    await code.trigger('click'); await flushPromises()
    expect(code.attributes('aria-expanded')).toBe('true')
    const contents = wrapper.get('#relation-contents-relation')
    expect(contents.text()).toContain('V1 · 已生效')
    expect(contents.text()).toContain('PART-1')
    expect(contents.text()).toContain('随主料数量')
    expect(contents.text()).not.toContain('草稿')
    await code.trigger('click'); await flushPromises()
    expect(code.attributes('aria-expanded')).toBe('false')
    const tree = wrapper.findComponent({ name: 'ElTree' })
    tree.vm.$emit('node-click', { id: 'root' }); await flushPromises()
    expect(wrapper.findAllComponents({ name: 'ElTable' })[0]!.props('data')).toHaveLength(1)
    tree.vm.$emit('node-click', { id: '__unclassified__' }); await flushPromises()
    expect(wrapper.findAllComponents({ name: 'ElTable' })[0]!.props('data')[0].id).toBe('missing')
    wrapper.unmount()
  })

  it('标准化人员补选写回主料分类，已有分类只显示继承信息', async () => {
    api.listMaterialRelationCategories.mockResolvedValue([root, child])
    api.getMaterialRelationCategories.mockResolvedValue([])
    api.assignMaterialRelationCategory.mockResolvedValue([child])
    const wrapper = mount(MaterialRelationCategories, { props: { token: 'token', materialId: 'main', canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    wrapper.findComponent({ name: 'ElTreeSelect' }).vm.$emit('update:modelValue', 'child'); await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '保存分类')!.trigger('click'); await flushPromises()
    expect(api.assignMaterialRelationCategory).toHaveBeenCalledWith('main', 'child', 'token')
    expect(wrapper.text()).toContain('继承主料归属')
    expect(wrapper.findComponent({ name: 'ElTreeSelect' }).exists()).toBe(false)
    expect(wrapper.emitted('changed')).toHaveLength(1)
    wrapper.unmount()
  })
})
