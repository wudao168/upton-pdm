export const projectPhasePersonnelRoles = [
  { key: 'ElectricalEngineer', label: '电气工程师' },
  { key: 'ElectricalHardware', label: '硬件工程师' },
  { key: 'StandardProcurement', label: '采购部负责人' },
  { key: 'NonStandardProcurement', label: '供应链负责人' },
  { key: 'NonStandardProduction', label: '生产负责人' },
  { key: 'MechanicalAssembly', label: '机械装配' },
  { key: 'ElectricalAssembly', label: '电气装配' },
  { key: 'ElectricalCommissioning', label: '调试工程师' },
  { key: 'Acceptance', label: '质量' },
] as const

export const projectPersonnelRoles = [
  { key: 'ProjectManager', label: '项目经理' },
  { key: 'CollaborativeProjectManager', label: '协同项目经理' },
  { key: 'DesignLead', label: '主设计' },
  { key: 'Designer', label: '设计人员' },
  { key: 'SalesPeople', label: '销售经理' },
  { key: 'PresalesPeople', label: '售前工程师' },
  ...projectPhasePersonnelRoles,
] as const
