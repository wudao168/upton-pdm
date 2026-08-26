import { mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { describe, expect, it, vi } from 'vitest'
import FolderTemplateSettings from '../src/components/FolderTemplateSettings.vue'
import type { ProjectFolderTemplateNode } from '../src/types'

const roles = [
  { role: 'Engineer', name: '机械工程师', description: '', baseRole: 'Engineer', isSystem: true, isSystemAdministrator: false, permissions: [], userCount: 1 },
  { role: 'ElectricalEngineer', name: '电气工程师', description: '', baseRole: 'Engineer', isSystem: true, isSystemAdministrator: false, permissions: [], userCount: 1 },
]

const nodes: ProjectFolderTemplateNode[] = [
  { folderKey: 'mechanical', name: '机械图纸', purpose: 'MechanicalRoot', sortOrder: 10, isSystem: true, inheritPermissions: true, permissions: [] },
  { folderKey: 'electrical', name: '电气图纸', purpose: 'ElectricalRoot', sortOrder: 20, isSystem: true, inheritPermissions: true, permissions: [] },
  { folderKey: 'electrical.project', parentKey: 'electrical', name: '项目目录（自动生成）', purpose: 'ProjectContainer', sortOrder: 10, isSystem: true, inheritPermissions: true, permissions: [] },
  { folderKey: 'mechanical.project', parentKey: 'mechanical', name: '项目目录（自动生成）', purpose: 'ProjectContainer', sortOrder: 10, isSystem: true, inheritPermissions: true, permissions: [] },
]

function mountSettings(onSave = vi.fn().mockResolvedValue(nodes)) {
  return mount(FolderTemplateSettings, {
    props: { nodes, users: [], roles, pending: false, onSave },
    global: { plugins: [ElementPlus] },
  })
}

describe('FolderTemplateSettings', () => {
  it('以上级目录中文名称显示层级', () => {
    const wrapper = mountSettings()
    const parentLabels = wrapper.findAll('tbody tr').map(row => row.findAll('td')[0].text())

    expect(wrapper.find('.pdm-pagebar').exists()).toBe(false)
    expect(wrapper.get('.pdm-template-toolbar').text()).toContain('保存模板')
    expect(parentLabels).toEqual(['项目号', '项目号', '电气图纸', '机械图纸'])
    expect(wrapper.text()).not.toContain('electrical')
    expect(wrapper.text()).not.toContain('mechanical')
  })

  it('使用上下箭头调整同级目录顺序并保存交换后的顺序值', async () => {
    const onSave = vi.fn().mockResolvedValue(nodes)
    const wrapper = mountSettings(onSave)

    expect(wrapper.get('[aria-label="上移 机械图纸"]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('[aria-label="下移 电气图纸"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[aria-label="下移 机械图纸"]').trigger('click')

    const names = wrapper.findAll('tbody tr').slice(0, 2).map(row => (row.findAll('td')[1].get('input').element as HTMLInputElement).value)
    expect(names).toEqual(['电气图纸', '机械图纸'])
    await wrapper.get('button.pdm-primary-action').trigger('click')

    expect(onSave).toHaveBeenCalledWith(expect.arrayContaining([
      expect.objectContaining({ folderKey: 'electrical', sortOrder: 10 }),
      expect.objectContaining({ folderKey: 'mechanical', sortOrder: 20 }),
    ]))
  })

  it('权限角色来自实际角色目录并显示中文名称', async () => {
    const wrapper = mountSettings()
    await wrapper.findAll('tbody tr')[0].find('button.pdm-secondary-action').trigger('click')
    await wrapper.get('.el-dialog button.pdm-secondary-action').trigger('click')

    const roleSelect = wrapper.findAllComponents({ name: 'ElSelect' })[1]
    const options = wrapper.findAllComponents({ name: 'ElOption' }).filter(option => ['Engineer', 'ElectricalEngineer'].includes(String(option.props('value'))))

    expect(options.map(option => [option.props('label'), option.props('value')])).toEqual([
      ['机械工程师', 'Engineer'],
      ['电气工程师', 'ElectricalEngineer'],
    ])
    expect(roleSelect.props('modelValue')).toBe('Engineer')
  })
})
